using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Domain.Moments;

/// <summary>
/// Decides which events are notable moments (ADR-046): the second tier below
/// <see cref="KeyMomentDetector"/>. Nothing it finds wakes a model.
/// </summary>
/// <remarks>
/// Rules: a yellow card; a corner; a shot off the woodwork; a substitution (ADR-064); and,
/// flagged as minor, a throw-in or a goal kick. A key moment beats a notable one, so an event the key detector reports (a goal
/// that came off the post, a penalty off the bar) is not notable, and one event is never two
/// moments.
/// </remarks>
public sealed class NotableMomentDetector
{
    private readonly KeyMomentDetector _key;
    private readonly PlayerNames _names;

    /// <summary>Creates a detector.</summary>
    /// <param name="key">The key-moment detector, asked first so that a key moment is never also notable.</param>
    /// <param name="teams">Squads used to name players. Defaults to the embedded team profiles.</param>
    public NotableMomentDetector(KeyMomentDetector key, IEnumerable<TeamProfile>? teams = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
        _names = new PlayerNames(teams);
    }

    /// <summary>
    /// A detector that names players from other squads, the teams of one match (ADR-050), over a
    /// key detector that does the same, so a key moment and a notable one name players alike.
    /// </summary>
    /// <param name="teams">The squads to name players from.</param>
    /// <returns>The new detector.</returns>
    public NotableMomentDetector WithTeams(IEnumerable<TeamProfile> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        IReadOnlyCollection<TeamProfile> squads = [.. teams];
        return new NotableMomentDetector(_key.WithTeams(squads), squads);
    }

    /// <summary>
    /// <see cref="WithTeams"/> for one match's profiles, built once per profiles instance and
    /// reused (see <see cref="KeyMomentDetector.ForTeams"/>).
    /// </summary>
    /// <param name="teams">One match's profiles, by team ID.</param>
    /// <returns>The detector for those teams.</returns>
    public NotableMomentDetector ForTeams(IReadOnlyDictionary<string, TeamProfile> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        return _forTeams.GetValue(teams, t => new NotableMomentDetector(_key.ForTeams(t), t.Values));
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<string, TeamProfile>, NotableMomentDetector> _forTeams = new();

    /// <summary>
    /// Decides whether an event is a notable moment.
    /// </summary>
    /// <param name="matchEvent">The event that has just happened.</param>
    /// <param name="before">
    /// The match state before the event. It's passed to the key detector, and keeps this method
    /// the same shape as <see cref="KeyMomentDetector.Detect"/> for rules that need the score.
    /// </param>
    /// <returns>The notable moment, or null when the event isn't one or is a key moment.</returns>
    /// <exception cref="ArgumentException">The event contradicts itself (see <see cref="MatchEvent.EnsureValid"/>).</exception>
    public NotableMoment? Detect(MatchEvent matchEvent, MatchState before)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        ArgumentNullException.ThrowIfNull(before);

        // The key detector validates the event, and its answer settles key-beats-notable.
        return _key.Detect(matchEvent, before) is null ? DetectAfterKey(matchEvent, before) : null;
    }

    /// <summary>
    /// Decides whether an event is a notable moment, for a caller that has already run the key
    /// detector on it and found no key moment. It doesn't ask the key detector again, and it
    /// doesn't validate the event: call it only for an event <see cref="KeyMomentDetector.Detect"/>
    /// has accepted and returned null for. On a key moment (a goal off the post) it would
    /// wrongly report woodwork; use <see cref="Detect"/> when in doubt.
    /// </summary>
    /// <param name="matchEvent">The event, already checked by the key detector.</param>
    /// <param name="before">The match state before the event.</param>
    /// <returns>The notable moment, or null when the event isn't one.</returns>
    public NotableMoment? DetectAfterKey(MatchEvent matchEvent, MatchState before)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        ArgumentNullException.ThrowIfNull(before);

        var who = _names.Describe(matchEvent.PlayerId);
        var other = Other(matchEvent.Team);
        return matchEvent switch
        {
            { Type: MatchEventType.Card, Card: CardColour.Yellow }
                => Moment(NotableMomentKind.YellowCard, matchEvent, matchEvent.Team, $"{matchEvent.Time}: Yellow card for {who}."),
            { Restart: RestartKind.Corner }
                => Moment(NotableMomentKind.Corner, matchEvent, other, $"{matchEvent.Time}: Corner to the {Side(other)} team, last touched by {who}."),
            { Shot.HitWoodwork: true }
                => Moment(NotableMomentKind.Woodwork, matchEvent, matchEvent.Team, $"{matchEvent.Time}: {who} hits the woodwork."),
            { Restart: RestartKind.ThrowIn }
                => Moment(NotableMomentKind.ThrowIn, matchEvent, other, $"{matchEvent.Time}: Throw-in to the {Side(other)} team, last touched by {who}."),
            { Restart: RestartKind.GoalKick }
                => Moment(NotableMomentKind.GoalKick, matchEvent, other, $"{matchEvent.Time}: Goal kick to the {Side(other)} team, last touched by {who}."),
            { Type: MatchEventType.Substitution }
                => Moment(NotableMomentKind.Substitution, matchEvent, matchEvent.Team,
                    $"{matchEvent.Time}: Substitution for the {Side(matchEvent.Team)} team: {_names.Describe(matchEvent.ReplacementPlayerId)} comes on for {who}."),
            _ => null
        };
    }

    private static TeamSide Other(TeamSide side) => side == TeamSide.Home ? TeamSide.Away : TeamSide.Home;

    private static string Side(TeamSide side) => side == TeamSide.Home ? "home" : "away";

    private NotableMoment Moment(NotableMomentKind kind, MatchEvent trigger, TeamSide team, FormattableString reason)
    {
        var found = _names.Find(trigger.PlayerId);
        return new NotableMoment(
            kind,
            trigger,
            reason.ToString(CultureInfo.InvariantCulture),
            team,
            found?.Name,
            found?.Team,
            trigger.ReplacementPlayerId is { } on ? _names.Find(on)?.Name : null);
    }
}
