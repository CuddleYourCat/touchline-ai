using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Touchline.Domain;
using Touchline.EventProcessor.Processing;

namespace Touchline.EventProcessor;

/// <summary>
/// The recap host (ADR-030, amended 23 Oct): a Service Bus trigger on the <c>recaps</c>
/// subscription of the <c>match-finished</c> topic that writes a finished match's recaps once,
/// through <see cref="RecapWriter"/>. A thin adapter: it reads the message, runs the writer, and
/// settles the message.
/// </summary>
/// <remarks>
/// An unreadable message is dead-lettered. A match that isn't stored at full time is abandoned
/// until delivery <see cref="MaxNotFinishedDeliveries"/>, then dead-lettered. A recap
/// that couldn't be read or saved throws, so Service Bus redelivers the message; the recaps already
/// stored are skipped on the retry, so it pays only for the missing ones.
/// </remarks>
/// <param name="writer">Writes and saves the recaps.</param>
/// <param name="logger">The logger.</param>
public sealed partial class RecapFunction(RecapWriter writer, ILogger<RecapFunction> logger)
{
    /// <summary>The topic the trigger reads.</summary>
    public const string TopicName = ServiceBusMatchFinishedPublisher.TopicName;

    /// <summary>The subscription the trigger reads.</summary>
    public const string SubscriptionName = "recaps";

    /// <summary>The dead-letter reason for a message that isn't a readable "match finished".</summary>
    public const string UnreadableReason = "UnreadableMatchFinished";

    /// <summary>The dead-letter reason for a match that isn't stored at full time.</summary>
    public const string NotFinishedReason = "MatchNotFinished";

    /// <summary>
    /// The delivery at which a match that isn't stored at full time is dead-lettered; earlier
    /// deliveries are abandoned, so Service Bus delivers them again.
    /// </summary>
    public const int MaxNotFinishedDeliveries = 3;

    /// <summary>Writes the recaps for one "match finished" message, then completes or dead-letters it.</summary>
    /// <param name="message">The message, as <see cref="ServiceBusMatchFinishedPublisher.ToMessage"/> wrote it.</param>
    /// <param name="actions">Settles the message.</param>
    /// <param name="cancellationToken">Signalled when the host shuts down.</param>
    /// <returns>A task that completes once the message is settled.</returns>
    [Function(nameof(RecapFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(TopicName, SubscriptionName, Connection = CommentaryFunction.ConnectionName, AutoCompleteMessages = false)]
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

        var run = await writer.WriteAsync(matchId, cancellationToken);
        if (run.Status == RecapRunStatus.NotFinished)
        {
            // The match may not be readable yet (a replica behind, or a store blip), so give it
            // a few deliveries before parking it.
            if (message.DeliveryCount < MaxNotFinishedDeliveries)
            {
                LogAbandoned(logger, message.MessageId, message.DeliveryCount);
                await actions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                return;
            }

            LogDeadLettered(logger, message.MessageId, NotFinishedReason);
            await actions.DeadLetterMessageAsync(message, deadLetterReason: NotFinishedReason, cancellationToken: cancellationToken);
            return;
        }

        if (run.StoreFailures > 0)
        {
            throw new InvalidOperationException($"{run.StoreFailures} recap(s) for match {matchId} couldn't be read or saved; the message will be redelivered.");
        }

        await actions.CompleteMessageAsync(message, cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match-finished message {MessageId} found no finished match on delivery {DeliveryCount}; abandoned for redelivery")]
    private static partial void LogAbandoned(ILogger logger, string messageId, int deliveryCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Match-finished message {MessageId} was dead-lettered: {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, string messageId, string reason);
}
