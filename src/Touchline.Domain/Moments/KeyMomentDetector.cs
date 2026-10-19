using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Domain.Moments;

/// <summary>
/// Decides which events are key moments. This is the cost guard of ADR-004: of about
/// 1,600 events in a match, only the few dozen it returns ever reach a model.
/// </summary>
/// <remarks>
/// Rules, most specific first, so one event is never two moments:
/// <list type="bullet">
/// <item>a foul inside the fouling team's own penalty area;</item>
/// <item>a goal at any xG;</item>
/// <item>a penalty saved, or missed (ADR-022);</item>
/// <item>a shot cleared off the line (ADR-022);</item>
/// <item>any other shot that didn't go in, at or above <see cref="BigChanceThreshold"/>;</item>
/// <item>a red card.</item>
/// </list>
/// A goal or a clearance off the line within <see cref="CloseCallCm"/> of the line also
/// carries the goal-line margin. Everything else returns null. Reasons name players and
/// teams, so they read as plain sentences in logs and agent context.
/// </remarks>
public sealed class KeyMomentDetector
{
    /// <summary>The default xG at or above which a missed shot counts as a big chance.</summary>
    public const double DefaultBigChanceThreshold = 0.30;

    /// <summary>How close to the line, in centimetres, a call has to be to count as close (ADR-022).</summary>
    public const int CloseCallCm = 5;

    private readonly IReadOnlyDictionary<string, (Player Player, TeamProfile Team)> _players;

    /// <summary>
    /// Creates a detector.
    /// </summary>
    /// <param name="bigChanceThreshold">
    /// xG at or above which a shot that didn't go in is a big chance. It's a parameter so it
    /// can be tuned from configuration later without a code change.
    /// </param>
    /// <param name="teams">
    /// Squads used to put player names in reasons. Defaults to the embedded team profiles.
    /// An unknown player ID appears in the reason as the ID itself.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The threshold isn't between 0 and 1.</exception>
    public KeyMomentDetector(double bigChanceThreshold = DefaultBigChanceThreshold, IEnumerable<TeamProfile>? teams = null)
    {
        if (!double.IsFinite(bigChanceThreshold) || bigChanceThreshold < 0 || bigChanceThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bigChanceThreshold), bigChanceThreshold, "Must be an xG value between 0 and 1.");
        }

        BigChanceThreshold = bigChanceThreshold;
        _players = (teams ?? TeamProfileCatalog.LoadEmbedded().Values)
            .SelectMany(team => team.Squad.Select(player => (Player: player, Team: team)))
            .ToDictionary(entry => entry.Player.Id);
    }

    /// <summary>The xG at or above which a missed shot counts as a big chance.</summary>
    public double BigChanceThreshold { get; }

    /// <summary>
    /// Decides whether an event is a key moment.
    /// </summary>
    /// <param name="matchEvent">The event that has just happened.</param>
    /// <param name="before">
    /// The match state before the event, used for the score in a goal's reason. It doesn't
    /// have to be exactly one event behind: only its score is read.
    /// </param>
    /// <returns>The key moment, or null when the event isn't one.</returns>
    /// <exception cref="ArgumentException">The event contradicts itself (see <see cref="MatchEvent.EnsureValid"/>).</exception>
    public KeyMoment? Detect(MatchEvent matchEvent, MatchState before)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        ArgumentNullException.ThrowIfNull(before);

        // The shot and card rules match on their detail, so the event must be consistent
        // with its type before any rule runs (a pass carrying shot detail is rejected).
        matchEvent.EnsureValid();

        var who = Describe(matchEvent.PlayerId);
        var margin = CloseCallMarginCm(matchEvent);
        return matchEvent switch
        {
            { Type: MatchEventType.Foul } when PitchDimensions.IsInPenaltyArea(matchEvent.Start, matchEvent.Team)
                => Moment(KeyMomentKind.PenaltyAwarded, matchEvent, $"{matchEvent.Time}: Penalty. Foul{On(matchEvent.FouledPlayerId)} by {who} inside their own area."),
            { Shot.Outcome: ShotOutcome.Goal }
                => Moment(KeyMomentKind.Goal, matchEvent, $"{matchEvent.Time}: GOAL! {who} scores{AssistedBy(matchEvent.AssistPlayerId)}. {ScoreAfterGoal(before, matchEvent.Team)}.{GoalLineCall(margin)}", margin),
            { Shot: { Situation: ShotSituation.Penalty, Outcome: ShotOutcome.Saved } shot }
                => Moment(KeyMomentKind.PenaltySaved, matchEvent, $"{matchEvent.Time}: Penalty saved! {who} is denied by the keeper, who {Dive(shot.KeeperDive)}."),
            { Shot: { Situation: ShotSituation.Penalty } shot }
                => Moment(KeyMomentKind.PenaltyMissed, matchEvent, $"{matchEvent.Time}: Penalty missed! {who} {(shot.HitWoodwork ? "hits the woodwork" : "misses the target")}."),
            { Shot.ClearedOffLine: true }
                => Moment(KeyMomentKind.ClearedOffLine, matchEvent, $"{matchEvent.Time}: Cleared off the line! {Describe(matchEvent.BlockedByPlayerId)} denies {who}.{GoalLineCall(margin)}", margin),
            { Shot: { } shot } when shot.ExpectedGoals >= BigChanceThreshold
                => Moment(KeyMomentKind.BigChance, matchEvent, $"{matchEvent.Time}: Big chance for {who}, xG {shot.ExpectedGoals:0.00}, {shot.Outcome}."),
            { Type: MatchEventType.Card, Card: CardColour.Red }
                => Moment(KeyMomentKind.RedCard, matchEvent, $"{matchEvent.Time}: Red card for {who}."),
            _ => null
        };
    }

    // Reasons go into logs and model prompts, so numbers always use a full stop.
    private static KeyMoment Moment(KeyMomentKind kind, MatchEvent trigger, FormattableString reason, int? goalLineMarginCm = null) =>
        new(kind, trigger, reason.ToString(CultureInfo.InvariantCulture), goalLineMarginCm);

    // For a goal or a clearance off the line, how far the whole ball was over (or short of)
    // the line in whole centimetres, when that was within CloseCallCm. Otherwise null.
    private static int? CloseCallMarginCm(MatchEvent matchEvent)
    {
        if (matchEvent is not { Shot: { Outcome: ShotOutcome.Goal } or { ClearedOffLine: true }, End: { } end })
        {
            return null;
        }

        // Rounded away from the line, so a goal is never "over by 0 cm". It's first rounded to
        // the micrometre, so floating-point dust (3.0000001 cm) can't push it up a centimetre.
        var metres = PitchDimensions.OverTheGoalLineBy(end, matchEvent.Team);
        var cm = (int)(Math.Sign(metres) * Math.Ceiling(Math.Round(Math.Abs(metres) * 100, 4)));
        return Math.Abs(cm) <= CloseCallCm ? cm : null;
    }

    private static string GoalLineCall(int? marginCm) => marginCm switch
    {
        null => string.Empty,
        > 0 => string.Create(CultureInfo.InvariantCulture, $" Goal-line technology confirms the whole ball was over, by {marginCm} cm."),
        _ => string.Create(CultureInfo.InvariantCulture, $" Goal-line technology shows it hadn't wholly crossed, by {-marginCm} cm.")
    };

    private static string Dive(DiveDirection? dive) => dive switch
    {
        DiveDirection.Left => "dived to the taker's left",
        DiveDirection.Right => "dived to the taker's right",
        _ => "stayed in the middle"
    };

    private string Describe(string? playerId) =>
        playerId is not null && _players.TryGetValue(playerId, out var entry)
            ? $"{entry.Player.Name} ({entry.Team.ShortName})"
            : playerId ?? "unknown player";

    private string On(string? fouledPlayerId) => fouledPlayerId is null ? string.Empty : $" on {Describe(fouledPlayerId)}";

    private string AssistedBy(string? assistPlayerId) => assistPlayerId is null ? string.Empty : $", assisted by {Describe(assistPlayerId)}";

    // Reads only the score, so it doesn't depend on `before` being exactly one event behind.
    private static string ScoreAfterGoal(MatchState before, TeamSide scorer) => scorer == TeamSide.Home
        ? (before with { Home = before.Home with { Goals = before.Home.Goals + 1 } }).Score
        : (before with { Away = before.Away with { Goals = before.Away.Goals + 1 } }).Score;
}
