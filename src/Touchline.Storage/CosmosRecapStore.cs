using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Viewing;

namespace Touchline.Storage;

/// <summary>
/// Keeps full-time recaps in the Cosmos DB <c>recaps</c> container (ADR-030, amended 23 Oct): one
/// document per match, mode and language, partitioned by <c>/matchId</c>. The Event Processor
/// writes through it and the Match Centre reads through it. Beside each recap's slot sits a small
/// attempt marker, <c>{id}:attempts</c>, counting the model's fallbacks for that pair.
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosRecapStore(CosmosClient client) : IRecapStore
{
    private const string _attemptsSuffix = ":attempts";

    private Container Recaps => client.GetContainer(CosmosNames.Database, CosmosNames.Recaps);

    /// <summary>A recap's document ID, <c>{matchId}:{mode}:{language}</c>, with the language normalised.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">The language tag.</param>
    /// <returns>For example <c>m-7:Casual:es</c>.</returns>
    public static string DocumentId(string matchId, ViewingMode mode, string language) =>
        $"{matchId}:{mode}:{CosmosCommentaryStore.NormaliseLanguage(language)}";

    /// <inheritdoc />
    public async Task<MatchRecap?> GetAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(language);
        var id = DocumentId(matchId, mode, language);

        // An attempt marker shares the container, so an ID that names one is never read as a recap.
        if (!CosmosIds.IsValid(matchId) || !CosmosIds.IsValid(id) || id.EndsWith(_attemptsSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        using var response = await Recaps.ReadItemStreamAsync(id, new PartitionKey(matchId), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<MatchRecap>(response.Content, DomainJson.Options, cancellationToken);
    }

    /// <summary>A pair's attempt marker ID, <c>{matchId}:{mode}:{language}:attempts</c>.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">The language tag.</param>
    /// <returns>For example <c>m-7:Casual:es:attempts</c>.</returns>
    public static string AttemptsId(string matchId, ViewingMode mode, string language) =>
        DocumentId(matchId, mode, language) + _attemptsSuffix;

    /// <inheritdoc />
    public async Task<int> GetAttemptsAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(language);
        var id = AttemptsId(matchId, mode, language);
        if (!CosmosIds.IsValid(matchId) || !CosmosIds.IsValid(id))
        {
            return 0;
        }

        using var response = await Recaps.ReadItemStreamAsync(id, new PartitionKey(matchId), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        response.EnsureSuccessStatusCode();
        var marker = await JsonSerializer.DeserializeAsync<JsonObject>(response.Content, DomainJson.Options, cancellationToken);
        return marker?["attempts"]?.GetValue<int>() ?? 0;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The match ID or the marker ID isn't one Cosmos DB accepts.</exception>
    public async Task SaveAttemptsAsync(string matchId, ViewingMode mode, string language, int attempts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(language);
        var id = AttemptsId(matchId, mode, language);
        if (!CosmosIds.IsValid(matchId) || !CosmosIds.IsValid(id))
        {
            throw new ArgumentException($"'{id}' in match '{matchId}' isn't a valid Cosmos DB ID.", nameof(matchId));
        }

        var marker = new JsonObject { ["id"] = id, ["matchId"] = matchId, ["attempts"] = attempts };
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(marker, DomainJson.Options));

        // Create or replace: the count only grows, and the last writer's count is the one kept.
        using var saved = await Recaps.UpsertItemStreamAsync(content, new PartitionKey(matchId), cancellationToken: cancellationToken);
        saved.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The match ID or the document ID isn't one Cosmos DB accepts.</exception>
    public async Task<bool> SaveAsync(MatchRecap recap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recap);
        recap = recap with { Language = CosmosCommentaryStore.NormaliseLanguage(recap.Language) };
        var id = DocumentId(recap.MatchId, recap.Mode, recap.Language);
        if (!CosmosIds.IsValid(recap.MatchId) || !CosmosIds.IsValid(id))
        {
            throw new ArgumentException($"'{id}' in match '{recap.MatchId}' isn't a valid Cosmos DB ID.", nameof(recap));
        }

        var document = JsonSerializer.SerializeToNode(recap, DomainJson.Options)!.AsObject();
        document["id"] = id;
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, DomainJson.Options));

        // Create, not upsert: a redelivered message finds its recap already there and keeps it.
        using var created = await Recaps.CreateItemStreamAsync(content, new PartitionKey(recap.MatchId), cancellationToken: cancellationToken);
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        created.EnsureSuccessStatusCode();
        return true;
    }
}
