using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Touchline.Domain;
using Touchline.Domain.Matches;

namespace Touchline.Simulator.Publishing;

/// <summary>Sends a match's events to the event stream.</summary>
public interface IMatchEventSender
{
    /// <summary>
    /// Sends events, in order.
    /// </summary>
    /// <param name="events">Events from a single match.</param>
    /// <param name="cancellationToken">Stops the send.</param>
    /// <returns>A task that completes once every event has been sent.</returns>
    Task SendAsync(IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes match events to Event Hubs (ADR-004, ADR-023): one event per message, the
/// envelope as message properties, the match ID as the partition key so a match stays in
/// order, and as many messages per batch as fit.
/// </summary>
/// <param name="producer">The producer for the match-events hub.</param>
public sealed class EventHubsMatchEventSender(EventHubProducerClient producer) : IMatchEventSender
{
    /// <inheritdoc />
    public async Task SendAsync(IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        var options = new CreateBatchOptions { PartitionKey = events[0].MatchId };
        var batch = await producer.CreateBatchAsync(options, cancellationToken);
        try
        {
            foreach (var matchEvent in events)
            {
                if (matchEvent.MatchId != options.PartitionKey)
                {
                    throw new ArgumentException("Every event sent together must belong to the same match.", nameof(events));
                }

                var message = ToEventData(matchEvent);
                if (batch.TryAdd(message))
                {
                    continue;
                }

                // TryAdd returns false when the batch is full, rather than throwing, so a full
                // batch is sent and the event goes into a fresh one. Ignoring false loses events.
                if (batch.Count == 0)
                {
                    throw new InvalidOperationException($"Event {matchEvent.Sequence} of '{matchEvent.MatchId}' is too large for a single Event Hubs message.");
                }

                await producer.SendAsync(batch, cancellationToken);
                batch.Dispose();
                batch = await producer.CreateBatchAsync(options, cancellationToken);
                if (!batch.TryAdd(message))
                {
                    throw new InvalidOperationException($"Event {matchEvent.Sequence} of '{matchEvent.MatchId}' is too large for a single Event Hubs message.");
                }
            }

            if (batch.Count > 0)
            {
                await producer.SendAsync(batch, cancellationToken);
            }
        }
        finally
        {
            batch.Dispose();
        }
    }

    /// <summary>
    /// The Event Hubs message for an event: its JSON in <see cref="DomainJson"/>, and the
    /// envelope as properties so consumers can decide before reading the body (ADR-023).
    /// </summary>
    /// <param name="matchEvent">The event.</param>
    /// <returns>The message.</returns>
    public static EventData ToEventData(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);

        var message = new EventData(JsonSerializer.SerializeToUtf8Bytes(matchEvent, DomainJson.Options))
        {
            ContentType = "application/json",
            MessageId = $"{matchEvent.MatchId}:{matchEvent.Sequence}"
        };

        foreach (var (name, value) in EventEnvelope.For(matchEvent))
        {
            message.Properties[name] = value;
        }

        return message;
    }
}
