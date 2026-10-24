using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Touchline.Domain;
using Touchline.EventProcessor.Processing;

namespace Touchline.EventProcessor;

/// <summary>
/// The search indexer host (ADR-039): a Service Bus trigger on the <c>search-index</c> subscription of
/// the <c>match-finished</c> topic that indexes a finished match's possessions once, through
/// <see cref="PossessionIndexWriter"/>. A thin adapter, like <see cref="RecapFunction"/>: it reads
/// the message, runs the writer, and settles the message.
/// </summary>
/// <remarks>
/// An unreadable message is dead-lettered. A match that isn't stored at full time is abandoned until
/// delivery <see cref="MaxNotFinishedDeliveries"/>, then dead-lettered. A failed embedding call
/// abandons the message, so it's delivered again, up to the subscription's max delivery count of 3
/// (ADR-052), and then it dead-letters. A failed index write throws, so the message isn't completed.
/// With Search switched off the message is completed without any reads.
/// </remarks>
/// <param name="writer">Indexes the possessions.</param>
/// <param name="logger">The logger.</param>
public sealed partial class PossessionIndexFunction(PossessionIndexWriter writer, ILogger<PossessionIndexFunction> logger)
{
    /// <summary>The topic the trigger reads.</summary>
    public const string TopicName = ServiceBusMatchFinishedPublisher.TopicName;

    /// <summary>The subscription the trigger reads (<c>infra/main.bicep</c> creates it whether or not Search is deployed).</summary>
    public const string SubscriptionName = "search-index";

    /// <summary>
    /// The trigger's own connection name: every Service Bus trigger needs a unique one. The AppHost
    /// references the Service Bus namespace under this name too, and <c>infra/main.bicep</c> sets its
    /// <c>servicebussearch__*</c> app settings to the processor identity.
    /// </summary>
    public const string ConnectionName = "servicebussearch";

    /// <summary>The dead-letter reason for a message that isn't a readable "match finished".</summary>
    public const string UnreadableReason = "UnreadableMatchFinished";

    /// <summary>The dead-letter reason for a match that isn't stored at full time.</summary>
    public const string NotFinishedReason = "MatchNotFinished";

    /// <summary>
    /// The delivery at which a match that isn't stored at full time is dead-lettered; earlier
    /// deliveries are abandoned, so Service Bus delivers them again.
    /// </summary>
    public const int MaxNotFinishedDeliveries = 3;

    /// <summary>Indexes the possessions for one "match finished" message, then settles it.</summary>
    /// <param name="message">The message, as <see cref="ServiceBusMatchFinishedPublisher.ToMessage"/> wrote it.</param>
    /// <param name="actions">Settles the message.</param>
    /// <param name="cancellationToken">Signalled when the host shuts down.</param>
    /// <returns>A task that completes once the message is settled.</returns>
    [Function(nameof(PossessionIndexFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(TopicName, SubscriptionName, Connection = ConnectionName, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(actions);

        if (Read(message) is not { } matchId)
        {
            LogDeadLettered(logger, message.MessageId, UnreadableReason);
            await actions.DeadLetterMessageAsync(message, deadLetterReason: UnreadableReason, cancellationToken: cancellationToken);
            return;
        }

        var run = await writer.WriteAsync(matchId, message.DeliveryCount, cancellationToken);
        switch (run.Status)
        {
            case PossessionIndexStatus.NotFinished when message.DeliveryCount < MaxNotFinishedDeliveries:
                LogAbandoned(logger, message.MessageId, message.DeliveryCount, "the match is not stored at full time");
                await actions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                return;
            case PossessionIndexStatus.NotFinished:
                LogDeadLettered(logger, message.MessageId, NotFinishedReason);
                await actions.DeadLetterMessageAsync(message, deadLetterReason: NotFinishedReason, cancellationToken: cancellationToken);
                return;
            case PossessionIndexStatus.EmbeddingFailed:
                // Documents are never indexed without vectors (ADR-056), so the model may answer on a later delivery.
                LogAbandoned(logger, message.MessageId, message.DeliveryCount, "the embedding call failed");
                await actions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                return;
            default:
                await actions.CompleteMessageAsync(message, cancellationToken);
                return;
        }
    }

    private static string? Read(ServiceBusReceivedMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize<MatchFinished>(message.Body.ToMemory().Span, DomainJson.Options)?.MatchId is { Length: > 0 } id
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match-finished message {MessageId} was abandoned on delivery {DeliveryCount} for redelivery: {Why}")]
    private static partial void LogAbandoned(ILogger logger, string messageId, int deliveryCount, string why);

    [LoggerMessage(Level = LogLevel.Error, Message = "Match-finished message {MessageId} was dead-lettered: {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, string messageId, string reason);
}
