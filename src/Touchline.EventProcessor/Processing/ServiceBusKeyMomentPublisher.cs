using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Touchline.Domain;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Publishes key moments to the Service Bus topic <c>key-moments</c> (ADR-004, ADR-023), which
/// the commentary and highlights subscriptions read.
/// </summary>
/// <param name="client">The Service Bus client, configured by Aspire.</param>
public sealed class ServiceBusKeyMomentPublisher(ServiceBusClient client) : IKeyMomentPublisher, IAsyncDisposable
{
    /// <summary>The topic name.</summary>
    public const string TopicName = "key-moments";

    private readonly ServiceBusSender _sender = client.CreateSender(TopicName);

    /// <inheritdoc />
    public async Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moments);
        if (moments.Count == 0)
        {
            return;
        }

        await _sender.SendMessagesAsync(moments.Select(ToMessage), cancellationToken);
    }

    /// <summary>
    /// The Service Bus message for a key moment. Its message ID is {matchId}:{sequence}, so a
    /// moment published twice (after a redelivered batch) can be recognised as the same one. The
    /// body is the moment in <see cref="DomainJson"/>, including the optional <c>homeScore</c> and
    /// <c>awayScore</c> after the trigger event, which the processor sets and the commentary reads.
    /// </summary>
    /// <param name="moment">The key moment.</param>
    /// <returns>The message.</returns>
    public static ServiceBusMessage ToMessage(KeyMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment);

        var trigger = moment.Trigger;
        return new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(moment, DomainJson.Options))
        {
            // The message ID is the moment ID, so a moment published twice is one message to
            // Service Bus. With duplicate detection on the topic (Bicep, slice D6) a repeat inside
            // the detection window is dropped before any subscriber sees it, so two concurrent
            // deliveries of the same moment can't both pay for the models. Until then the
            // commentary function's skip check and the store's idempotent slots keep repeats harmless.
            MessageId = MomentIds.For(trigger.MatchId, trigger.Sequence),
            Subject = moment.Kind.ToString(),
            ContentType = "application/json",
            ApplicationProperties =
            {
                ["matchId"] = trigger.MatchId,
                ["kind"] = moment.Kind.ToString()
            }
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
