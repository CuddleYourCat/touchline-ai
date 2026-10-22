using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// What one event turned into on the stream.
/// </summary>
/// <param name="Messages">The stream messages, in the order to send them.</param>
/// <param name="Moment">The key moment the event triggered, or null.</param>
/// <param name="State">The match state after the event.</param>
internal sealed record Projection(IReadOnlyList<LiveMessage> Messages, KeyMoment? Moment, MatchState State);

/// <summary>
/// Turns a match's events, fed in order, into live stream messages: the event itself, the
/// state, key moments, closed momentum windows and full time (ADR-027). It's pure, so the
/// demo replay and the change-feed reader (ADR-029) produce identical streams from identical
/// events.
/// </summary>
/// <remarks>
/// Commentary, analysis, milestones and overlays come from elsewhere (the agents and the
/// moment tiers) and are merged in by the feed.
/// </remarks>
internal sealed class MatchStreamProjector
{
    private readonly KeyMomentDetector _detector;
    private readonly List<MatchEvent> _events = [];
    private MatchState _state;
    private (MatchPeriod Period, int FromMinute)? _openWindow;

    /// <summary>Starts a projection at kick-off.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="detector">The key-moment detector.</param>
    public MatchStreamProjector(string matchId, KeyMomentDetector detector)
    {
        _detector = detector;
        _state = MatchState.KickOff(matchId);
    }

    /// <summary>The match state after the last event fed in.</summary>
    public MatchState State => _state;

    /// <summary>
    /// Feeds the next event in sequence order.
    /// </summary>
    /// <param name="matchEvent">The event.</param>
    /// <returns>The messages it produces.</returns>
    public Projection Apply(MatchEvent matchEvent)
    {
        var wasFullTime = _state.FullTime;
        var messages = new List<LiveMessage>();
        var id = LiveMessage.IdFor(matchEvent.Sequence);

        // A new window means the previous one is complete, so its momentum is final.
        var window = WindowOf(matchEvent.Time);
        if (_openWindow is { } open && open != window && ClosedReading(open) is { } closed)
        {
            messages.Add(new LiveMessage(StreamEvents.Momentum, id, closed));
        }

        _openWindow = window;

        var moment = _detector.Detect(matchEvent, _state);
        _state = _state.Apply(matchEvent);
        _events.Add(matchEvent);

        messages.Add(new LiveMessage(StreamEvents.Event, id, matchEvent));
        if (moment is not null)
        {
            messages.Add(new LiveMessage(StreamEvents.Moment, id, MomentViews.From(moment, _state.MatchId, commentary: null)));
        }

        var state = LiveState.From(_state);
        messages.Add(new LiveMessage(StreamEvents.State, id, state));

        if (_state.FullTime && !wasFullTime)
        {
            if (ClosedReading(window) is { } last)
            {
                messages.Add(new LiveMessage(StreamEvents.Momentum, id, last));
            }

            messages.Add(new LiveMessage(StreamEvents.FullTime, id, state));
        }

        return new Projection(messages, moment, _state);
    }

    private static (MatchPeriod Period, int FromMinute) WindowOf(MatchTime time) =>
        (time.Period, (int)time.Clock.TotalMinutes / Momentum.WindowMinutes * Momentum.WindowMinutes);

    private MomentumReading? ClosedReading((MatchPeriod Period, int FromMinute) window) =>
        Momentum.Read(_events).LastOrDefault(r => r.Period == window.Period && r.FromMinute == window.FromMinute);
}

/// <summary>
/// Builds the wire view of a moment.
/// </summary>
internal static class MomentViews
{
    /// <summary>The view of a key moment.</summary>
    /// <param name="moment">The moment.</param>
    /// <param name="matchId">The match, which may differ from the trigger's (a demo lap).</param>
    /// <param name="commentary">Its commentary for the viewer, or null.</param>
    /// <returns>The view.</returns>
    public static MomentView From(KeyMoment moment, string matchId, CommentaryLine? commentary) =>
        new(MomentIds.For(matchId, moment.Trigger.Sequence),
            MomentTier.Key,
            moment.Kind.ToString(),
            moment.Trigger.Sequence,
            ClockReading.From(moment.Trigger.Time),
            moment.Trigger.Team,
            moment.Trigger.PlayerId,
            moment.Reason,
            commentary);
}
