using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Touchline.Domain;
using Touchline.EventProcessor.Processing;

namespace Touchline.EventProcessor;

/// <summary>
/// Announces finished matches for their recaps (ADR-030, amended 23 Oct), with the <c>matches</c>
/// container's change feed as the outbox: saving the full-time state is the event, so there's no
/// second write that a crash could lose.
/// </summary>
/// <remarks>
/// Each changed document at full time is published on <c>match-finished</c> with message ID
/// <c>{matchId}:finished</c>. If a publish fails, the function throws: the trigger retries, and the
/// lease (its own, under <see cref="LeaseContainerPrefix"/>) checkpoints only after a batch succeeds.
/// A repeat, from a retry or a later save of the same document, is dropped by the topic's duplicate
/// detection, and then by <see cref="RecapWriter"/> skipping recaps already stored.
/// </remarks>
/// <param name="publisher">Where finished matches are announced.</param>
public sealed class AnnounceFinishedMatches(IMatchFinishedPublisher publisher)
{
    /// <summary>
    /// This trigger's lease prefix, so its checkpoints in the shared lease container are separate
    /// from <see cref="ProjectSeason"/>'s.
    /// </summary>
    public const string LeaseContainerPrefix = "recaps-";

    /// <summary>Publishes "match finished" for every changed document at full time.</summary>
    /// <param name="documents">The changed match documents, latest version of each, as a JSON array.</param>
    /// <param name="cancellationToken">Stops the publishing when the host shuts down.</param>
    /// <returns>A task that completes once every finished match in the batch is announced.</returns>
    [Function(nameof(AnnounceFinishedMatches))]
    // Retry forever (-1): a Cosmos DB trigger redelivers a batch only while its retry policy
    // lasts, then the host checkpoints the lease and the announcement is lost for good. The lease
    // prefix is this trigger's own, so only announcements wait on an outage, not ProjectSeason.
    [ExponentialBackoffRetry(-1, "00:00:02", "00:05:00")]
    public async Task RunAsync(
        [CosmosDBTrigger(
            CosmosMatchStore.DatabaseName,
            ProjectSeason.ContainerName,
            Connection = ProjectSeason.ConnectionName,
            LeaseContainerName = ProjectSeason.LeaseContainerName,
            LeaseContainerPrefix = LeaseContainerPrefix)] string documents,
        CancellationToken cancellationToken)
    {
        var changed = JsonSerializer.Deserialize<List<MatchRecord>>(documents, DomainJson.Options) ?? [];
        foreach (var matchId in changed.Where(m => m.State.FullTime).Select(m => m.MatchId))
        {
            await publisher.PublishAsync(matchId, cancellationToken);
        }
    }
}
