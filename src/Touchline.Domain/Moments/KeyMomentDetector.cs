using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Domain.Moments;

/// <summary>
/// Decides which events are key moments. This is the cost guard of ADR-004: of roughly
/// 1,500 events in a match, only the few dozen it returns ever reach a model.
/// </summary>
/// <remarks>
/// Rules: a goal at any xG; a shot that didn't go in at or above
/// <see cref="BigChanceThreshold"/>; a foul inside the fouling team's own penalty area;
/// a red card. Everything else returns null. Reasons name players and teams, so they
/// read as plain sentences in logs and agent context.
/// </remarks>
public sealed class KeyMomentDetector
{
    /// <summary>The default xG at or above which a missed shot counts as a big chance.</summary>
    public const double DefaultBigChanceThreshold = 0.30;

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
        return matchEvent switch
        {
            { Type: MatchEventType.Foul } when PitchDimensions.IsInPenaltyArea(matchEvent.Start, matchEvent.Team)
                => Moment(KeyMomentKind.PenaltyAwarded, matchEvent, $"{matchEvent.Time}: Penalty. Foul{On(matchEvent.FouledPlayerId)} by {who} inside their own area."),
            { Type: MatchEventType.Shot, Shot: { Outcome: ShotOutcome.Goal } }
                => Moment(KeyMomentKind.Goal, matchEvent, $"{matchEvent.Time}: GOAL! {who} scores{AssistedBy(matchEvent.AssistPlayerId)}. {ScoreAfterGoal(before, matchEvent.Team)}."),
            { Type: MatchEventType.Shot, Shot: { } shot } when shot.ExpectedGoals >= BigChanceThreshold
                => Moment(KeyMomentKind.BigChance, matchEvent, $"{matchEvent.Time}: Big chance for {who}, xG {shot.ExpectedGoals:0.00}, {shot.Outcome}."),
            { Type: MatchEventType.Card, Card: CardColour.Red }
                => Moment(KeyMomentKind.RedCard, matchEvent, $"{matchEvent.Time}: Red card for {who}."),
            _ => null
        };
    }

    // Reasons go into logs and model prompts, so numbers always use a full stop.
    private static KeyMoment Moment(KeyMomentKind kind, MatchEvent trigger, FormattableString reason) =>
        new(kind, trigger, reason.ToString(CultureInfo.InvariantCulture));

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
