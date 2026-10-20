using Azure.Messaging.EventHubs;
using Microsoft.Azure.Functions.Worker;
using Touchline.EventProcessor.Processing;

namespace Touchline.EventProcessor;

/// <summary>
/// The Event Hubs trigger (ADR-015): receives batches from the match-events hub and hands them
/// to <see cref="MatchEventProcessor"/>.
/// </summary>
/// <param name="processor">The batch processor.</param>
public sealed class ProcessMatchEvents(MatchEventProcessor processor)
{
    /// <summary>The hub the trigger reads.</summary>
    public const string HubName = "match-events";

    /// <summary>The trigger's connection name, which Aspire's reference to the Event Hubs namespace supplies.</summary>
    public const string ConnectionName = "eventhubs";

    /// <summary>
    /// Processes one batch. A transient failure (a storage or messaging outage) retries the
    /// batch with backoff before the trigger checkpoints past it. Processing is idempotent, so
    /// a retry is safe.
    /// </summary>
    /// <param name="events">The batch, in partition order.</param>
    /// <param name="cancellationToken">Signalled when the host shuts down.</param>
    /// <returns>A task that completes once the batch is processed.</returns>
    [Function(nameof(ProcessMatchEvents))]
    [ExponentialBackoffRetry(5, "00:00:02", "00:00:30")]
    public Task RunAsync(
        [EventHubTrigger(HubName, Connection = ConnectionName)] EventData[] events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var batch = events.Select(e => new IncomingEvent(e.Properties.AsReadOnly(), e.EventBody)).ToList();
        return processor.ProcessAsync(batch, cancellationToken);
    }
}
