using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain.Storage;
using Touchline.MatchCentre.Live;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Reads a container's change feed with the pull model (ADR-037): no change feed processor and
/// no leases. Every replica needs every change for its own open streams, so each keeps its own
/// continuation, in memory, and the Reader role is enough.
/// </summary>
/// <remarks>
/// A restarted replica starts again from "now". It loses nothing a stream needs, because a hub
/// loads a match's stored events only after the reader's first poll has taken "now", and takes
/// newer ones from the feed; a gap between the two is backfilled from the store.
/// </remarks>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
internal sealed class CosmosChangeFeedSource(CosmosClient client) : IChangeFeedSource
{
    /// <summary>The most pages read in one poll, so a burst can't hold the reader for long.</summary>
    internal const int MaxPagesPerPoll = 10;

    private readonly ConcurrentDictionary<string, string> _continuations = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<JsonElement>> ReadChangesAsync(string container, CancellationToken cancellationToken)
    {
        // Now() with no range covers the whole container, every partition. LatestVersion gives
        // each changed document once, as it is now, which is all a stream needs.
        var start = _continuations.TryGetValue(container, out var token)
            ? ChangeFeedStartFrom.ContinuationToken(token)
            : ChangeFeedStartFrom.Now();
        using var iterator = client.GetContainer(CosmosNames.Database, container)
            .GetChangeFeedStreamIterator(start, ChangeFeedMode.LatestVersion);

        var documents = new List<JsonElement>();
        var latest = token;
        for (var page = 0; page < MaxPagesPerPoll && iterator.HasMoreResults; page++)
        {
            using var response = await iterator.ReadNextAsync(cancellationToken);

            // 304 Not Modified means "nothing new": it still carries the continuation to keep,
            // which on the first poll is the point "now" was taken.
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                latest = response.ContinuationToken ?? latest;
                break;
            }

            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);
            documents.AddRange(json.RootElement.GetProperty("Documents").EnumerateArray().Select(d => d.Clone()));
            latest = response.ContinuationToken ?? latest;
        }

        // Only a poll that read every page moves the continuation on, so a failure part-way
        // reads the same changes again next time; the hub drops the repeats by sequence.
        if (latest is not null)
        {
            _continuations[container] = latest;
        }

        return documents;
    }
}
