using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Domain.Moments;

/// <summary>
/// Spots milestones as a match is played, one event at a time. Unlike
/// <see cref="KeyMomentDetector"/>, it has memory: a brace needs the player's first goal, and a
/// personal best needs their fastest carry so far. So make one detector per match and feed it
/// every event of that match, in sequence order.
/// </summary>
/// <remarks>
/// The rules:
/// <list type="bullet">
/// <item><see cref="MilestoneKind.FirstGoal"/>: the match's first goal, by either side. Only once.</item>
/// <item><see cref="MilestoneKind.Brace"/> and <see cref="MilestoneKind.HatTrick"/>: a player's second and third goals. A fourth goal is no milestone.</item>
/// <item><see cref="MilestoneKind.FiftiethPass"/>: a player's 50th <em>completed</em> pass (<see cref="MatchEvent.Successful"/>). Misplaced passes don't count.</item>
/// <item><see cref="MilestoneKind.CleanSheetAtHalfTime"/>: at the first half's <see cref="MatchEventType.PeriodEnd"/>,
/// one milestone for each side that hasn't conceded, home first. At 0-0 that's two. Its reason names the
/// side by the short name in the match's <see cref="MatchEventType.MatchMetadata"/> event, or as the home or
/// away side if no metadata has been seen.</item>
/// <item><see cref="MilestoneKind.FirstBooking"/>: the match's first card, yellow or red. Only once.</item>
/// <item><see cref="MilestoneKind.PersonalBestSpeed"/>: a carry strictly faster than both the player's profile
/// <see cref="Player.TopSpeedKmh"/> and every earlier carry of theirs this match. A player with no profile never sets one.</item>
/// </list>
/// Event Hubs delivers at least once, so an event whose sequence isn't above the last one seen
/// is a redelivery: it's ignored and returns no milestones.
/// </remarks>
public sealed class MilestoneDetector
{
    /// <summary>The completed pass that counts as a milestone.</summary>
    public const int PassMilestone = 50;

    private static readonly IReadOnlyList<Milestone> _none = [];

    private readonly PlayerNames _names;
    private readonly Dictionary<string, double> _topSpeeds;
    private readonly Dictionary<string, int> _goals = [];
    private readonly Dictionary<string, int> _completedPasses = [];
    private readonly Dictionary<string, double> _fastestCarries = [];
    private readonly Dictionary<TeamSide, int> _conceded = new() { [TeamSide.Home] = 0, [TeamSide.Away] = 0 };
    private readonly Dictionary<TeamSide, string> _shortNames = [];
    private string? _matchId;
    private long _lastSequence = long.MinValue;
    private bool _anyGoal;
    private bool _anyCard;

    /// <summary>
    /// Creates a detector for one match.
    /// </summary>
    /// <param name="teams">
    /// Squads used for player names in reasons and for each player's profile top speed.
    /// Defaults to the embedded team profiles.
    /// </param>
    public MilestoneDetector(IEnumerable<TeamProfile>? teams = null)
    {
        var squads = (teams ?? TeamProfileCatalog.LoadEmbedded().Values).ToList();
        _names = new PlayerNames(squads);
        _topSpeeds = squads
            .SelectMany(team => team.Squad)
            .GroupBy(player => player.Id)
            .ToDictionary(group => group.Key, group => group.First().TopSpeedKmh);
    }

    /// <summary>
    /// Takes the next event of the match and returns the milestones it reached.
    /// </summary>
    /// <param name="matchEvent">The event that has just happened.</param>
    /// <returns>The milestones it reached, in the order the rules list them; usually none.</returns>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    /// <exception cref="ArgumentException">
    /// The event contradicts itself (see <see cref="MatchEvent.EnsureValid"/>), or it belongs to a
    /// different match from the events before it.
    /// </exception>
    public IReadOnlyList<Milestone> Observe(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        matchEvent.EnsureValid();

        _matchId ??= matchEvent.MatchId;
        if (matchEvent.MatchId != _matchId)
        {
            throw new ArgumentException(
                $"Event {matchEvent.Sequence} belongs to match '{matchEvent.MatchId}', but this detector is following match '{_matchId}'.",
                nameof(matchEvent));
        }

        // A redelivery must not touch any state, or a repeated goal would count twice.
        if (matchEvent.Sequence <= _lastSequence)
        {
            return _none;
        }

        _lastSequence = matchEvent.Sequence;
        var who = _names.Describe(matchEvent.PlayerId);
        return matchEvent switch
        {
            { Type: MatchEventType.MatchMetadata, Metadata: { } metadata } => Remember(metadata),
            { IsGoal: true } => Goal(matchEvent, who),
            { Type: MatchEventType.Pass, Successful: true, PlayerId: { } passer } => Pass(matchEvent, passer, who),
            { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf } => CleanSheets(matchEvent),
            { Type: MatchEventType.Card } => Card(matchEvent, who),
            { Type: MatchEventType.Carry, Carry: { } carry, PlayerId: { } carrier } => Carry(matchEvent, carrier, carry.SpeedKmh, who),
            _ => _none
        };
    }

    private IReadOnlyList<Milestone> Remember(MatchMetadata metadata)
    {
        _shortNames[TeamSide.Home] = metadata.Home.ShortName;
        _shortNames[TeamSide.Away] = metadata.Away.ShortName;
        return _none;
    }

    private IReadOnlyList<Milestone> Goal(MatchEvent goal, string who)
    {
        var found = new List<Milestone>();
        _conceded[goal.Team == TeamSide.Home ? TeamSide.Away : TeamSide.Home]++;

        if (!_anyGoal)
        {
            _anyGoal = true;
            found.Add(Reached(MilestoneKind.FirstGoal, goal, $"{goal.Time}: First goal of the match, scored by {who}."));
        }

        if (goal.PlayerId is { } scorer)
        {
            var count = _goals.GetValueOrDefault(scorer) + 1;
            _goals[scorer] = count;
            switch (count)
            {
                case 2:
                    found.Add(Reached(MilestoneKind.Brace, goal, $"{goal.Time}: {who} scores a brace."));
                    break;
                case 3:
                    found.Add(Reached(MilestoneKind.HatTrick, goal, $"{goal.Time}: {who} scores a hat-trick."));
                    break;
            }
        }

        return found;
    }

    private IReadOnlyList<Milestone> Pass(MatchEvent pass, string passer, string who)
    {
        var count = _completedPasses.GetValueOrDefault(passer) + 1;
        _completedPasses[passer] = count;
        return count == PassMilestone
            ? [Reached(MilestoneKind.FiftiethPass, pass, $"{pass.Time}: {who} completes their {Ordinal(PassMilestone)} pass.")]
            : _none;
    }

    private IReadOnlyList<Milestone> CleanSheets(MatchEvent halfTime) =>
        [.. new[] { TeamSide.Home, TeamSide.Away }
            .Where(side => _conceded[side] == 0)
            .Select(side => Reached(
                MilestoneKind.CleanSheetAtHalfTime,
                halfTime,
                $"{halfTime.Time}: {SideName(side)} have kept a clean sheet at half-time.",
                side))];

    private IReadOnlyList<Milestone> Card(MatchEvent card, string who)
    {
        if (_anyCard)
        {
            return _none;
        }

        _anyCard = true;
        return [Reached(MilestoneKind.FirstBooking, card, $"{card.Time}: First card of the match, a {(card.Card == CardColour.Red ? "red" : "yellow")} card for {who}.")];
    }

    private static string Ordinal(int number) => number % 100 is >= 11 and <= 13
        ? $"{number}th"
        : $"{number}{(number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }}";

    private IReadOnlyList<Milestone> Carry(MatchEvent carry, string carrier, double speedKmh, string who)
    {
        // The bar is the profile top speed and every earlier carry. With no profile there is no bar to clear.
        var earlier = _fastestCarries.GetValueOrDefault(carrier);
        var bar = _topSpeeds.TryGetValue(carrier, out var topSpeed) ? Math.Max(topSpeed, earlier) : double.PositiveInfinity;
        _fastestCarries[carrier] = Math.Max(speedKmh, earlier);
        return speedKmh > bar
            ? [Reached(MilestoneKind.PersonalBestSpeed, carry, $"{carry.Time}: {who} sets a personal best, carrying at {speedKmh:0.0} km/h.")]
            : _none;
    }

    // The short name from the match metadata, or "the home side" before any has been seen.
    private string SideName(TeamSide side) =>
        _shortNames.TryGetValue(side, out var shortName) ? shortName : $"the {side.ToString().ToLowerInvariant()} side";

    // Reasons go into logs and model prompts, so numbers always use a full stop. A clean sheet
    // belongs to a side rather than a player.
    private static Milestone Reached(MilestoneKind kind, MatchEvent trigger, FormattableString reason, TeamSide? side = null) =>
        new(kind, trigger, side ?? trigger.Team, side is null ? trigger.PlayerId : null, reason.ToString(CultureInfo.InvariantCulture));
}
