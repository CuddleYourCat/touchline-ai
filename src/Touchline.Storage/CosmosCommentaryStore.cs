using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;

namespace Touchline.Storage;

/// <summary>
/// Keeps commentary in the Cosmos DB <c>commentary</c> container (ADR-029, ADR-038): one document
/// per moment, mode and language, partitioned by <c>/matchId</c>. The commentary agents write
/// through it and the Match Centre reads through it.
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosCommentaryStore(CosmosClient client) : ICommentaryStore
{
    /// <summary>A match's lines, in moment order. Always run in the match's own partition.</summary>
    internal const string MatchQuery = "SELECT * FROM c ORDER BY c.sequence";

    private Container Commentary => client.GetContainer(CosmosNames.Database, CosmosNames.Commentary);

    /// <summary>
    /// A line's document ID, <c>{momentId}:{mode}:{language}</c>, with the language normalised
    /// (<see cref="NormaliseLanguage"/>). A line has one slot per moment, mode and language, so a
    /// model line saved after the template line replaces it.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>For example <c>demo-3:412:Casual:es-MX</c>.</returns>
    public static string DocumentId(CommentaryLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return $"{line.MomentId}:{line.Mode}:{NormaliseLanguage(line.Language)}";
    }

    /// <summary>
    /// Puts a language tag in one canonical casing, so <c>ES</c> and <c>es</c> share a slot: the
    /// primary subtag lower-case and a two-letter region upper-case.
    /// </summary>
    /// <param name="language">A tag such as <c>es-mx</c>.</param>
    /// <returns>For example <c>es-MX</c>.</returns>
    public static string NormaliseLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var parts = language.Split('-');
        parts[0] = parts[0].ToLowerInvariant();
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 2)
            {
                parts[i] = parts[i].ToUpperInvariant();
            }
        }

        return string.Join('-', parts);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The moment ID doesn't match the line's match and sequence, or the match ID or the document
    /// ID isn't one Cosmos DB accepts.
    /// </exception>
    public async Task SaveAsync(CommentaryLine line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.MomentId != MomentIds.For(line.MatchId, line.Sequence))
        {
            throw new ArgumentException(
                $"Moment '{line.MomentId}' isn't sequence {line.Sequence} of match '{line.MatchId}'.", nameof(line));
        }

        line = line with { Language = NormaliseLanguage(line.Language) };
        var id = DocumentId(line);
        if (!CosmosIds.IsValid(line.MatchId) || !CosmosIds.IsValid(id))
        {
            throw new ArgumentException($"'{id}' in match '{line.MatchId}' isn't a valid Cosmos DB ID.", nameof(line));
        }

        // The document is the line's own JSON plus an id, so the slot is the id.
        var document = JsonSerializer.SerializeToNode(line, DomainJson.Options)!.AsObject();
        document["id"] = id;

        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, DomainJson.Options));
        var partition = new PartitionKey(line.MatchId);

        // A model line may replace anything, so it's upserted. A template line only fills an empty
        // slot: a create that conflicts means a line (perhaps a model one) is already there, and
        // it's kept, so a late template never downgrades a model line.
        if (line.Source == CommentarySource.Model)
        {
            using var upserted = await Commentary.UpsertItemStreamAsync(content, partition, cancellationToken: cancellationToken);
            upserted.EnsureSuccessStatusCode();
            return;
        }

        using var created = await Commentary.CreateItemStreamAsync(content, partition, cancellationToken: cancellationToken);
        if (created.StatusCode != HttpStatusCode.Conflict)
        {
            created.EnsureSuccessStatusCode();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CommentaryLine>> GetAsync(string matchId, CancellationToken cancellationToken)
    {
        // An ID Cosmos DB would refuse can't name a stored match, so it has no commentary.
        if (!CosmosIds.IsValid(matchId))
        {
            return [];
        }

        return await CosmosQueries.ReadAllAsync<CommentaryLine>(
            Commentary, new QueryDefinition(MatchQuery), new PartitionKey(matchId), cancellationToken);
    }
}
