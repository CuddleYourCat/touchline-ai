using System.Globalization;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Touchline.Domain;
using Touchline.Domain.Moments;

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
    /// moment published twice (after a redelivered batch) can be recognised as the same one.
    /// </summary>
    /// <param name="moment">The key moment.</param>
    /// <returns>The message.</returns>
    public static ServiceBusMessage ToMessage(KeyMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment);

        var trigger = moment.Trigger;
        return new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(moment, DomainJson.Options))
        {
            MessageId = string.Create(CultureInfo.InvariantCulture, $"{trigger.MatchId}:{trigger.Sequence}"),
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
