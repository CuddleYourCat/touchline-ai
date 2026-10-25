using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Touchline.Agents;
using Touchline.Domain;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.EventProcessor.Processing;

namespace Touchline.EventProcessor;

/// <summary>
/// The live commentary host (ADR-030): a Service Bus trigger on the <c>commentary</c>
/// subscription of the <c>key-moments</c> topic that runs <see cref="CommentaryWorkflow"/> for
/// each key moment. A thin adapter: it reads the message, builds the match's context, skips a
/// moment the models have already written about, and dead-letters a message it can't read.
/// </summary>
/// <remarks>
/// Messages are settled by hand (<c>AutoCompleteMessages = false</c>), so a poison message is
/// dead-lettered with its reason rather than retried until the delivery count runs out. Any other
/// failure (Cosmos DB unavailable, an English model line that couldn't be saved, the host shutting down) throws, the host abandons the message
/// and Service Bus redelivers it. <c>maxConcurrentCalls</c> in <c>host.json</c> is kept low (4)
/// because each moment makes up to five model calls, and the deployments' tokens-per-minute
/// quota, not this host, is the limit.
/// </remarks>
/// <param name="workflow">Writes, checks, translates and stores the moment's lines.</param>
/// <param name="commentary">The stored commentary, read to recognise a redelivery.</param>
/// <param name="matches">The stored match documents, for the line-ups.</param>
/// <param name="telemetry">Counts skips and dead letters.</param>
/// <param name="logger">The logger.</param>
/// <param name="teams">
/// The team profiles of each match (ADR-050), so the roster guardrail allows a fan match's fan
/// players; null for the embedded catalogue alone.
/// </param>
public sealed partial class CommentaryFunction(
    CommentaryWorkflow workflow,
    ICommentaryStore commentary,
    IMatchStore matches,
    PipelineTelemetry telemetry,
    ILogger<CommentaryFunction> logger,
    IMatchTeamProfiles? teams = null)
{
    /// <summary>The topic the trigger reads.</summary>
    public const string TopicName = ServiceBusKeyMomentPublisher.TopicName;

    /// <summary>The subscription the trigger reads.</summary>
    public const string SubscriptionName = "commentary";

    /// <summary>
    /// The trigger's connection name, which Aspire's reference to the Service Bus namespace supplies
    /// locally and the <c>servicebus__*</c> app settings in <c>infra/main.bicep</c> supply in Azure.
    /// </summary>
    public const string ConnectionName = "servicebus";

    /// <summary>The dead-letter reason for a message that isn't a readable key moment.</summary>
    public const string UnreadableReason = "UnreadableKeyMoment";

    /// <summary>Runs the commentary for one key-moment message, then completes or dead-letters it.</summary>
    /// <param name="message">The key-moment message, as <see cref="ServiceBusKeyMomentPublisher.ToMessage"/> wrote it.</param>
    /// <param name="actions">Settles the message.</param>
    /// <param name="cancellationToken">Signalled when the host shuts down.</param>
    /// <returns>A task that completes once the message is settled.</returns>
    [Function(nameof(CommentaryFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(TopicName, SubscriptionName, Connection = ConnectionName, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(actions);

        if (!TryRead(message, out var moment, out var problem))
        {
            LogDeadLettered(logger, message.MessageId, problem);
            telemetry.CommentaryDeadLettered();
            await actions.DeadLetterMessageAsync(
                message,
                deadLetterReason: UnreadableReason,
                deadLetterErrorDescription: problem,
                cancellationToken: cancellationToken);
            return;
        }

        var matchId = moment.Trigger.MatchId;
        var momentId = MomentIds.For(matchId, moment.Trigger.Sequence);

        // Delivery is at least once. A moment whose English model lines are stored for both modes
        // was finished by an earlier delivery, so it's completed without paying for the models
        // again. Two point reads, not a query over the match: this runs for every moment.
        if (await IsModelLineStoredAsync(matchId, momentId, ViewingMode.Casual, cancellationToken)
            && await IsModelLineStoredAsync(matchId, momentId, ViewingMode.Analyst, cancellationToken))
        {
            LogSkipped(logger, momentId);
            telemetry.CommentarySkipped();
            await actions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var match = await matches.GetMatchAsync(matchId, cancellationToken);
        if (match?.Metadata is null)
        {
            LogNoMetadata(logger, momentId);
        }

        // The names reach the agents as JSON data in the user message, never in the system prompt (ADR-044).
        var profiles = teams is null ? null : await teams.ForMatchAsync(matchId, cancellationToken);
        var outcome = await workflow.RunAsync(moment, MatchContexts.For(moment, match, profiles, logger), cancellationToken);
        LogStored(logger, momentId, outcome.Lines.Count);

        // An English model line that failed to save would be lost for good if the message were
        // completed, so it's thrown instead: the host abandons the message, Service Bus redelivers
        // it, and the skip check above lets the retry run because the lines aren't stored. The
        // cost is a second set of model calls (the Commentator and every Localiser) for that
        // moment, bounded by the subscription's max delivery count; a save failure means Cosmos DB
        // is struggling, which is rare and worth paying for to keep the model line. A template line
        // or a translation that failed to save is completed: the template is only a stand-in and a
        // reader falls back to English, so a retry would buy nothing the skip check can see.
        if (outcome.EnglishModelLineLost)
        {
            LogModelLineLost(logger, momentId, outcome.FailedSaves.Count);
            throw new InvalidOperationException($"An English model line for key moment {momentId} could not be saved; the message will be redelivered.");
        }

        await actions.CompleteMessageAsync(message, cancellationToken);
    }

    private async Task<bool> IsModelLineStoredAsync(string matchId, string momentId, ViewingMode mode, CancellationToken cancellationToken) =>
        await commentary.FindAsync(matchId, momentId, mode, CommentaryWorkflow.English, cancellationToken) is { Source: CommentarySource.Model };

    private static bool TryRead(ServiceBusReceivedMessage message, out KeyMoment moment, out string problem)
    {
        moment = null!;
        try
        {
            var read = JsonSerializer.Deserialize<KeyMoment>(message.Body.ToMemory().Span, DomainJson.Options);
            if (read?.Trigger?.MatchId is not { Length: > 0 })
            {
                problem = "The body is not a key moment with a trigger event and a match ID.";
                return false;
            }

            moment = read;
            problem = "";
            return true;
        }
        catch (JsonException ex)
        {
            problem = $"The body is not valid key-moment JSON: {ex.Message}";
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Key-moment message {MessageId} was dead-lettered: {Problem}")]
    private static partial void LogDeadLettered(ILogger logger, string messageId, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped key moment {MomentId}: its English model lines are already stored (a redelivery)")]
    private static partial void LogSkipped(ILogger logger, string momentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Key moment {MomentId} has no stored match metadata, so the rosters are empty and the commentary falls back to the template")]
    private static partial void LogNoMetadata(ILogger logger, string momentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored {LineCount} commentary lines for key moment {MomentId}")]
    private static partial void LogStored(ILogger logger, string momentId, int lineCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Key moment {MomentId} had {FailedCount} line(s) fail to save, including an English model line; abandoning it for redelivery")]
    private static partial void LogModelLineLost(ILogger logger, string momentId, int failedCount);
}
