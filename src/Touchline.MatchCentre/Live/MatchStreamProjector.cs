using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Matches.Shape;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// What one event turned into on the stream.
/// </summary>
/// <param name="Messages">The stream messages, in the order to send them.</param>
/// <param name="Moment">The key moment the event triggered, or null.</param>
/// <param name="State">The match state after the event.</param>
/// <param name="Notable">
/// The notable moment the event triggered (ADR-046), or null. A minor one (a throw-in or goal
/// kick) is reported here but puts nothing in <paramref name="Messages"/>; see <paramref name="Minor"/>.
/// </param>
/// <param name="Overlay">The numbers an Analyst subtitle can use, taken after the event.</param>
/// <param name="Minor">
/// The minor moment's moment message and card (ADR-055), kept off the shared messages so the hub
/// can send it to Analyst subscribers only. Null unless the notable moment is minor.
/// </param>
internal sealed record Projection(
    IReadOnlyList<LiveMessage> Messages,
    KeyMoment? Moment,
    MatchState State,
    OverlayContext Overlay,
    NotableMoment? Notable = null,
    MinorExtras? Minor = null);

/// <summary>
/// Turns a match's events, fed in order, into live stream messages: the event itself, the
/// modelled shape of both teams (ADR-035), the state, key moments, closed momentum windows and
/// full time (ADR-027). It's pure, so the demo replay and the change-feed reader (ADR-029)
/// produce identical streams from identical events.
/// </summary>
/// <remarks>
/// Key and notable moments (ADR-046) each come with an overlay card (ADR-047). Model commentary,
/// analysis and milestones come from elsewhere and are merged in by the feed, and a notable
/// moment's template line is chosen per viewer by the feed, so this projection stays the same
/// for every viewer.
/// </remarks>
internal sealed partial class MatchStreamProjector
{
    private readonly KeyMomentDetector _detector;
    private readonly NotableMomentDetector _notable;
    private readonly IReadOnlyDictionary<string, TeamProfile>? _teams;
    private readonly ILogger _logger;
    private readonly List<MatchEvent> _events = [];
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private static readonly PitchPosition _centreSpot = new(PitchDimensions.Length / 2, PitchDimensions.Width / 2);
    private PitchPosition? _lastBall;
    private readonly HashSet<string> _defaulted = new(StringComparer.Ordinal);
    private (ShapeTeam Home, ShapeTeam Away)? _shapeTeams;
    private MatchState _state;
    private (MatchPeriod Period, int FromMinute)? _openWindow;
    private long _lastKeyCard = long.MinValue / 2;

    /// <summary>
    /// A notable moment gets no overlay card if a key moment's card went out this many events or
    /// fewer before it (ADR-047), so the two never clash on screen. By event count, not by clock,
    /// so a replay at any speed decides the same way.
    /// </summary>
    public const int NotableCardQuietEvents = 3;

    /// <summary>Starts a projection at kick-off.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="detector">The key-moment detector.</param>
    /// <param name="teams">
    /// The team profiles, keyed by team ID, for the shape model. Without them no <c>shape</c>
    /// messages are sent. A team that isn't there gets average tendencies and a 4-4-2.
    /// </param>
    /// <param name="lineups">
    /// The match metadata, when it's known up front, so shapes start even if the metadata
    /// event (sequence 1) is never fed in. The metadata event replaces it when it arrives.
    /// </param>
    /// <param name="logger">Logs a team given default tendencies, once per team.</param>
    /// <param name="notable">
    /// The notable-moment detector. Defaults to one over <paramref name="detector"/> and the given
    /// team profiles (or the embedded ones).
    /// </param>
    public MatchStreamProjector(
        string matchId,
        KeyMomentDetector detector,
        IReadOnlyDictionary<string, TeamProfile>? teams = null,
        MatchMetadata? lineups = null,
        ILogger? logger = null,
        NotableMomentDetector? notable = null)
    {
        _detector = detector;
        _notable = notable ?? new NotableMomentDetector(detector, teams?.Values);
        _teams = teams;
        _logger = logger ?? NullLogger.Instance;
        _state = MatchState.KickOff(matchId);
        if (lineups is not null)
        {
            SetLineups(lineups);
        }
    }

    /// <summary>The match state after the last event fed in.</summary>
    public MatchState State => _state;

    /// <summary>
    /// Feeds the next event in sequence order. Sequences missing before it are skipped, as the
    /// Event Processor skips events it lost, so a match with gaps projects instead of throwing.
    /// </summary>
    /// <param name="matchEvent">The event, with a sequence after every one fed in so far.</param>
    /// <returns>The messages it produces.</returns>
    public Projection Apply(MatchEvent matchEvent)
    {
        for (var missing = _state.LastSequence + 1; missing < matchEvent.Sequence; missing++)
        {
            _state = _state.Skip(missing);
        }

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
        var notable = moment is null ? _notable.DetectAfterKey(matchEvent, _state) : null;
        _state = _state.Apply(matchEvent);
        _events.Add(matchEvent);

        messages.Add(new LiveMessage(StreamEvents.Event, id, matchEvent));
        if (ShapeAfter(matchEvent) is { } shape)
        {
            messages.Add(new LiveMessage(StreamEvents.Shape, id, shape));
        }

        if (moment is not null)
        {
            messages.Add(new LiveMessage(StreamEvents.Moment, id, MomentViews.From(moment, _state.MatchId, commentary: null)));
        }
        else if (notable is { IsMinor: false })
        {
            messages.Add(new LiveMessage(StreamEvents.Moment, id, MomentViews.From(notable, _state.MatchId, commentary: null)));
        }

        if (OverlayFor(moment, notable) is { } card)
        {
            messages.Add(new LiveMessage(StreamEvents.Overlay, id, card));
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

        var context = new OverlayContext(matchEvent.Shot?.ExpectedGoals, _state.Home.ExpectedGoals, _state.Away.ExpectedGoals);
        return new Projection(messages, moment, _state, context, notable, MinorFor(notable));
    }

    // A minor moment's moment message and card, off the shared stream (ADR-055). Its card follows
    // the same quiet window as a notable one, and neither starts nor resets it.
    private MinorExtras? MinorFor(NotableMoment? notable) =>
        notable is { IsMinor: true } minor
            ? new MinorExtras(
                MomentViews.From(minor, _state.MatchId, commentary: null),
                minor.Trigger.Sequence - _lastKeyCard <= NotableCardQuietEvents ? null : OverlayCardBuilder.For(minor, _state.MatchId))
            : null;

    // The one place a moment becomes a shared card (ADR-047). A minor moment's card is built in
    // MinorFor, off the shared stream, because only the Analyst is sent it (ADR-055). When
    // milestones exist (skill shot 3), a milestone reached on this event replaces the notable
    // card here, so a first booking shows as a milestone and not as a plain yellow card.
    // A notable card is skipped when a key card went out within NotableCardQuietEvents events,
    // so a corner straight after a big chance doesn't cover the key card; the notable moment
    // still has its Moment message and template line.
    private OverlayCard? OverlayFor(KeyMoment? key, NotableMoment? notable)
    {
        if (key is not null)
        {
            _lastKeyCard = key.Trigger.Sequence;
            return OverlayCardBuilder.For(key, _state.MatchId);
        }

        return notable is { IsMinor: false } n && !(n.Trigger.Sequence - _lastKeyCard <= NotableCardQuietEvents)
            ? OverlayCardBuilder.For(n, _state.MatchId)
            : null;
    }

    // The shape model runs once the metadata has named both teams. A red card takes its player
    // off the pitch for the rest of the match. A substitution (ADR-064) puts the substitute into
    // his side's eleven at the departing player's place, and the formation is worked out again
    // from the new eleven, so a forward who replaces a defender plays up front. A substitution
    // has no ball in it, so its shape is drawn where the ball stopped (the centre spot at half
    // time), with play stopped, and the pitch shows the new player at once.
    private ShapeView? ShapeAfter(MatchEvent matchEvent)
    {
        if (matchEvent is { Metadata: { } metadata })
        {
            SetLineups(metadata);
        }

        if (matchEvent is { Type: MatchEventType.Card, Card: CardColour.Red, PlayerId: { } sentOff })
        {
            _dismissed.Add(sentOff);
        }

        if (matchEvent is { Type: MatchEventType.Substitution, PlayerId: { } off, ReplacementPlayerId: { } on } && _shapeTeams is { } current)
        {
            _shapeTeams = matchEvent.Team == TeamSide.Home
                ? (Substituted(current.Home, off, on), current.Away)
                : (current.Home, Substituted(current.Away, off, on));
        }

        var ball = ShapeModel.BallAfter(matchEvent);
        if (ball is not null)
        {
            _lastBall = ball;
        }
        else if (matchEvent.Type == MatchEventType.PeriodEnd)
        {
            // The next half kicks off from the centre spot, so half-time changes are drawn there.
            _lastBall = _centreSpot;
        }
        else if (matchEvent.Type == MatchEventType.Substitution)
        {
            ball = _lastBall;
        }

        return _shapeTeams is { } teams && ball is { } at
            ? ShapeView.From(matchEvent.Sequence, ShapeModel.Place(teams.Home, teams.Away, at, ShapeModel.PossessionAfter(matchEvent), _dismissed))
            : null;
    }

    // The team with the substitute in the departing player's place in the eleven. The copy works
    // out its formation afresh. A team with no profile has no position for the substitute, so he
    // plays in the departing player's line.
    private static ShapeTeam Substituted(ShapeTeam team, string off, string on)
    {
        var eleven = team.Sheet.StartingEleven.ToList();
        var index = eleven.IndexOf(off);
        if (index < 0 || eleven.Contains(on))
        {
            return team;
        }

        eleven[index] = on;
        var sheet = new TeamSheet(team.Sheet.Id, team.Sheet.Name, team.Sheet.ShortName, eleven, [.. team.Sheet.Substitutes.Where(id => id != on)]);
        var positions = team.Positions.ContainsKey(on) || !team.Positions.TryGetValue(off, out var line)
            ? team.Positions
            : new Dictionary<string, PlayerPosition>(team.Positions, StringComparer.Ordinal) { [on] = line };
        return team with { Sheet = sheet, Positions = positions };
    }

    // Each team's formation is worked out here, once, and cached in its ShapeTeam.
    private void SetLineups(MatchMetadata metadata)
    {
        if (_teams is null)
        {
            return;
        }

        _shapeTeams = (ShapeTeamFor(metadata.Home), ShapeTeamFor(metadata.Away));
    }

    private ShapeTeam ShapeTeamFor(TeamSheet sheet)
    {
        if (_teams!.TryGetValue(sheet.Id, out var profile))
        {
            return new ShapeTeam(sheet, profile);
        }

        if (_defaulted.Add(sheet.Id))
        {
            LogDefaultShape(_logger, _state.MatchId, sheet.Id);
        }

        return ShapeTeam.WithDefaults(sheet);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match {MatchId}: team {TeamId} has no profile, so its shape uses default tendencies and a 4-4-2")]
    private static partial void LogDefaultShape(ILogger logger, string matchId, string teamId);

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
            moment.Side,
            moment.FeaturedPlayerId,
            moment.Reason,
            commentary);

    /// <summary>The view of a notable moment.</summary>
    /// <param name="moment">The moment.</param>
    /// <param name="matchId">The match, which may differ from the trigger's (a demo lap).</param>
    /// <param name="commentary">Its template line for the viewer, or null.</param>
    /// <returns>The view. Its team is the side the moment is about, which for a corner is the side that wins it.</returns>
    public static MomentView From(NotableMoment moment, string matchId, CommentaryLine? commentary) =>
        new(MomentIds.For(matchId, moment.Trigger.Sequence),
            moment.IsMinor ? MomentTier.Minor : MomentTier.Notable,
            moment.Kind.ToString(),
            moment.Trigger.Sequence,
            ClockReading.From(moment.Trigger.Time),
            moment.Team,
            moment.FeaturedPlayerId,
            moment.Reason,
            commentary);
}

/// <summary>
/// Trims a replay to the one shape worth drawing.
/// </summary>
internal static class ReplayShapes
{
    /// <summary>
    /// Keeps only the last <c>shape</c> in a replay, moved to just after the last <c>event</c>,
    /// since a reconnecting client draws the latest shape and nothing before it. Every other
    /// message keeps its place.
    /// </summary>
    /// <param name="replay">The replayed messages, in order.</param>
    /// <returns>The messages with at most one shape.</returns>
    public static List<LiveMessage> LatestOnly(IEnumerable<LiveMessage> replay)
    {
        var messages = replay.ToList();
        var latest = messages.FindLast(m => m.Event == StreamEvents.Shape);
        if (latest is null)
        {
            return messages;
        }

        messages.RemoveAll(m => m.Event == StreamEvents.Shape);
        messages.Insert(messages.FindLastIndex(m => m.Event == StreamEvents.Event) + 1, latest);
        return messages;
    }
}
