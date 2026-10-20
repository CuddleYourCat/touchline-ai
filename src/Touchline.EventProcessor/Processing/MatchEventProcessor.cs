using System.Text.Json;
using Microsoft.Extensions.Logging;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Processes a batch from the event stream (ADR-023). For each match in it: check each
/// envelope, read the events a tolerant reader accepts, apply them to the match state in
/// order, detect key moments, then store the events, publish the moments and store the state.
/// </summary>
/// <remarks>
/// Delivery is at least once, so a batch can arrive again. The stored state's last sequence
/// is the watermark: an event at or below it was already processed and is passed over, so a
/// redelivery changes nothing. The state is written last. If the processor stops after
/// publishing moments but before writing the state, the batch is processed again and its
/// moments published again, each with the same message ID ({matchId}:{sequence}), so the
/// subscribers (and Service Bus duplicate detection, on the Standard tier) can drop them.
/// </remarks>
/// <param name="store">Where events and match state are stored.</param>
/// <param name="publisher">Where key moments go.</param>
/// <param name="detector">Decides which events are key moments.</param>
/// <param name="telemetry">Counters for processed, skipped and duplicate events.</param>
/// <param name="logger">Skips, gaps and progress.</param>
public sealed partial class MatchEventProcessor(
    IMatchStore store,
    IKeyMomentPublisher publisher,
    KeyMomentDetector detector,
    PipelineTelemetry telemetry,
    ILogger<MatchEventProcessor> logger)
{
    /// <summary>
    /// Processes a batch of messages, which may hold events from several matches.
    /// </summary>
    /// <param name="batch">The messages, in the order they arrived.</param>
    /// <param name="cancellationToken">Stops the processing.</param>
    /// <returns>A task that completes once every match in the batch is stored.</returns>
    public async Task ProcessAsync(IReadOnlyList<IncomingEvent> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var checks = batch.Select(message => (Message: message, Check: EventEnvelope.Check(message.Properties))).ToList();

        // Without a match ID and sequence there's nowhere to place the message.
        foreach (var (_, check) in checks.Where(c => c.Check.MatchId is null || c.Check.Sequence is null))
        {
            telemetry.Skipped(check.Skipped ?? SkipReason.MissingEnvelope);
            LogUnplaceable(logger);
        }

        foreach (var match in checks.Where(c => c.Check is { MatchId: not null, Sequence: not null }).GroupBy(c => c.Check.MatchId!))
        {
            await ProcessMatchAsync(match.Key, [.. match.OrderBy(c => c.Check.Sequence)], cancellationToken);
        }
    }

    private async Task ProcessMatchAsync(string matchId, List<(IncomingEvent Message, EnvelopeCheck Check)> messages, CancellationToken cancellationToken)
    {
        using var activity = PipelineTelemetry.Source.StartActivity("process match events");
        activity?.SetTag("match.id", matchId);
        activity?.SetTag("messaging.batch.message_count", messages.Count);

        var record = await store.GetMatchAsync(matchId, cancellationToken) ?? MatchRecord.New(matchId);
        var state = record.State;
        var metadata = record.Metadata;
        var stored = new List<MatchEvent>();
        var moments = new List<KeyMoment>();

        foreach (var (message, check) in messages)
        {
            var sequence = check.Sequence!.Value;
            if (sequence <= state.LastSequence)
            {
                telemetry.Duplicate();
                continue;
            }

            if (sequence != state.LastSequence + 1)
            {
                // Within a partition events arrive in order, so a gap means events were lost:
                // usually a batch that failed every retry before the trigger checkpointed past
                // it. They can't arrive later, so waiting for them would freeze the match for
                // good. The missing sequences are skipped instead, counted and logged as an
                // error, and the match carries on with incomplete totals.
                var lost = sequence - state.LastSequence - 1;
                telemetry.Lost(lost);
                LogGap(logger, matchId, state.LastSequence + 1, sequence, lost);
                for (var missing = state.LastSequence + 1; missing < sequence; missing++)
                {
                    state = state.Skip(missing);
                }
            }

            if (check.Skipped is { } reason || !TryRead(message, check, out var matchEvent, out reason))
            {
                telemetry.Skipped(reason);
                LogSkipped(logger, matchId, sequence, reason);
                state = state.Skip(sequence);
                continue;
            }

            if (detector.Detect(matchEvent, state) is { } moment)
            {
                moments.Add(moment);
            }

            state = state.Apply(matchEvent);
            metadata = matchEvent.Metadata ?? metadata;
            stored.Add(matchEvent);
        }

        if (state.LastSequence == record.State.LastSequence)
        {
            return;
        }

        // The metadata (and its Kind, which the season projection relies on) only comes from
        // the event at sequence 1. If that was skipped or lost, the match can't be told apart
        // from an exhibition, so say so loudly rather than leave it to be noticed later.
        if (metadata is null && record.State.LastSequence == 0)
        {
            telemetry.MissingMetadata();
            LogMissingMetadata(logger, matchId);
        }

        await store.UpsertEventsAsync(matchId, stored, cancellationToken);
        await publisher.PublishAsync(moments, cancellationToken);
        await store.UpsertMatchAsync(record with { Metadata = metadata, State = state }, cancellationToken);

        telemetry.Processed(stored.Count);
        telemetry.KeyMomentsPublished(moments.Count);
        activity?.SetTag("touchline.events.stored", stored.Count);
        activity?.SetTag("touchline.key_moments", moments.Count);
    }

    // The body must be a valid event that agrees with its envelope. Anything else is skipped
    // rather than retried, so one bad message can never stall the stream (ADR-023).
    private static bool TryRead(IncomingEvent message, EnvelopeCheck check, out MatchEvent matchEvent, out SkipReason reason)
    {
        reason = SkipReason.InvalidEvent;
        matchEvent = null!;
        try
        {
            var read = message.Body.ToObjectFromJson<MatchEvent>(DomainJson.Options);
            if (read is null || read.MatchId != check.MatchId || read.Sequence != check.Sequence)
            {
                return false;
            }

            read.EnsureValid();
            matchEvent = read;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a message with no usable envelope (no match ID or sequence)")]
    private static partial void LogUnplaceable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped event {Sequence} of {MatchId}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string matchId, long sequence, SkipReason reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event gap in {MatchId}: expected {Expected} but got {Actual}. {Lost} events are lost and skipped, so this match's totals are incomplete.")]
    private static partial void LogGap(ILogger logger, string matchId, long expected, long actual, long lost);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match {MatchId} has no metadata: its first event was skipped or lost, so its kind (fixture or not) is unknown")]
    private static partial void LogMissingMetadata(ILogger logger, string matchId);
}
