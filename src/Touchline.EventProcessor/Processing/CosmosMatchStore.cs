using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Storage;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Stores events and match state in Cosmos DB for NoSQL (ADR-023): the <c>events</c> and
/// <c>matches</c> containers, both partitioned by <c>/matchId</c>.
/// </summary>
/// <remarks>
/// The Cosmos DB SDK serialises with Newtonsoft.Json by default, which knows nothing of the
/// domain's System.Text.Json attributes and options. So every read and write here uses the
/// stream APIs, with the JSON written and read by <see cref="DomainJson"/>.
/// </remarks>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosMatchStore(CosmosClient client) : IMatchStore
{
    /// <summary>The database name.</summary>
    public const string DatabaseName = CosmosNames.Database;

    /// <summary>The container holding every event, one document per event.</summary>
    public const string EventsContainerName = CosmosNames.Events;

    /// <summary>The container holding each match's metadata and live state.</summary>
    public const string MatchesContainerName = CosmosNames.Matches;

    // Enough to overlap round trips, few enough to stay well inside the RU budget's burst.
    private const int _maxConcurrentWrites = 8;

    private Container Events => client.GetContainer(DatabaseName, EventsContainerName);

    private Container Matches => client.GetContainer(DatabaseName, MatchesContainerName);

    /// <inheritdoc />
    public async Task<MatchRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        // A point read (ID and partition key): the cheapest Cosmos DB operation.
        using var response = await Matches.ReadItemStreamAsync(matchId, new PartitionKey(matchId), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<MatchRecord>(response.Content, DomainJson.Options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertEventsAsync(string matchId, IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Upserts are independent and idempotent, so they run several at a time rather than one
        // round trip after another. A failure still fails the whole call, and the retried batch
        // writes every event again harmlessly.
        var partition = new PartitionKey(matchId);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentWrites, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(events, parallel, async (matchEvent, token) =>
        {
            // The document is the event's own JSON, plus an id: its sequence, so an upsert of
            // the same event replaces it rather than adding a second copy.
            var document = JsonSerializer.SerializeToNode(matchEvent, DomainJson.Options)!.AsObject();
            document["id"] = matchEvent.Sequence.ToString(CultureInfo.InvariantCulture);

            using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, DomainJson.Options));
            using var response = await Events.UpsertItemStreamAsync(content, partition, cancellationToken: token);
            response.EnsureSuccessStatusCode();
        });
    }

    /// <inheritdoc />
    public async Task UpsertMatchAsync(MatchRecord match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);

        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(match, DomainJson.Options));
        using var response = await Matches.UpsertItemStreamAsync(content, new PartitionKey(match.MatchId), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string matchId, CancellationToken cancellationToken)
    {
        // One partition, so the query never fans out. The stored documents are the events' own
        // JSON plus Cosmos DB's system properties, which DomainJson ignores.
        var events = await CosmosQueries.ReadAllAsync<MatchEvent>(
            Events, new QueryDefinition("SELECT * FROM c"), new PartitionKey(matchId), cancellationToken);
        return [.. events.OrderBy(e => e.Sequence)];
    }
}
