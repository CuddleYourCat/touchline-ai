namespace Touchline.Domain.Matches;

/// <summary>
/// One team's running totals. Immutable: applying an event produces new totals.
/// </summary>
/// <param name="Goals">Goals scored.</param>
/// <param name="Shots">Shots taken, including blocked ones.</param>
/// <param name="ShotsOnTarget">Goals plus saved shots.</param>
/// <param name="ExpectedGoals">Sum of the xG of every shot.</param>
/// <param name="Passes">Passes attempted.</param>
/// <param name="PassesCompleted">Passes that reached a team-mate.</param>
/// <param name="Fouls">Fouls committed.</param>
/// <param name="YellowCards">Yellow cards shown.</param>
/// <param name="RedCards">Red cards shown.</param>
public sealed record TeamStats(
    int Goals,
    int Shots,
    int ShotsOnTarget,
    double ExpectedGoals,
    int Passes,
    int PassesCompleted,
    int Fouls,
    int YellowCards,
    int RedCards)
{
    /// <summary>Totals at kick-off.</summary>
    public static TeamStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Share of passes completed, from 0 to 1, or 0 before the first pass.</summary>
    public double PassAccuracy => Passes == 0 ? 0 : (double)PassesCompleted / Passes;
}

/// <summary>
/// The live state of a match: score, clock and each team's totals. The Event Processor
/// rebuilds it by applying events in order, and the agents read it as context.
/// </summary>
/// <param name="MatchId">The match.</param>
/// <param name="Time">The time of the last event applied.</param>
/// <param name="Home">The home team's totals.</param>
/// <param name="Away">The away team's totals.</param>
/// <param name="LastSequence">Sequence number of the last event applied, or 0 before any.</param>
public sealed record MatchState(string MatchId, MatchTime Time, TeamStats Home, TeamStats Away, long LastSequence)
{
    /// <summary>
    /// The state at kick-off.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <returns>A state with no events applied.</returns>
    public static MatchState KickOff(string matchId) =>
        new(matchId, MatchTime.At(MatchPeriod.FirstHalf, 0), TeamStats.Empty, TeamStats.Empty, 0);

    /// <summary>
    /// True once the second half has ended. The season projection (ADR-025) only acts on a
    /// finished fixture, and this is how it knows.
    /// </summary>
    public bool FullTime { get; init; }

    /// <summary>
    /// True from the first half's <c>PeriodEnd</c> until the next event in the second half (ADR-066).
    /// </summary>
    public bool HalfTime { get; init; }

    /// <summary>The score as shown on screen, for example <c>1–0</c>.</summary>
    public string Score => $"{Home.Goals}–{Away.Goals}";

    /// <summary>
    /// Returns the state after one more event. The current instance is unchanged.
    /// </summary>
    /// <param name="matchEvent">The next event, in sequence order.</param>
    /// <returns>The new state.</returns>
    /// <exception cref="ArgumentException">The event belongs to a different match, or contradicts itself (see <see cref="MatchEvent.EnsureValid"/>).</exception>
    /// <exception cref="InvalidOperationException">The event is out of sequence: already applied, or with a gap before it.</exception>
    public MatchState Apply(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        matchEvent.EnsureValid();

        if (matchEvent.MatchId != MatchId)
        {
            throw new ArgumentException($"Event {matchEvent.Sequence} belongs to match '{matchEvent.MatchId}', not '{MatchId}'.", nameof(matchEvent));
        }

        if (matchEvent.Sequence != LastSequence + 1)
        {
            throw new InvalidOperationException($"Expected event {LastSequence + 1} but got {matchEvent.Sequence}. Events must be applied once, in order.");
        }

        var team = matchEvent.Team == TeamSide.Home ? Home : Away;
        var updated = Tally(team, matchEvent);
        var fullTime = FullTime || matchEvent is { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.SecondHalf };
        var halfTime = matchEvent switch
        {
            { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf } => true,
            { Time.Period: MatchPeriod.SecondHalf } => false,
            _ => HalfTime
        };

        return matchEvent.Team == TeamSide.Home
            ? this with { Home = updated, Time = matchEvent.Time, LastSequence = matchEvent.Sequence, FullTime = fullTime, HalfTime = halfTime }
            : this with { Away = updated, Time = matchEvent.Time, LastSequence = matchEvent.Sequence, FullTime = fullTime, HalfTime = halfTime };
    }

    /// <summary>
    /// Records that the next event was received but not applied, because a tolerant reader
    /// skipped it (ADR-023): an unknown type, a newer schema, or an invalid event. The totals
    /// don't change, but the sequence moves on, so the events after it still apply in order.
    /// </summary>
    /// <param name="sequence">The skipped event's sequence. It must be the next one expected.</param>
    /// <returns>The new state.</returns>
    /// <exception cref="InvalidOperationException">The sequence isn't the next one expected.</exception>
    public MatchState Skip(long sequence) =>
        sequence == LastSequence + 1
            ? this with { LastSequence = sequence }
            : throw new InvalidOperationException($"Expected event {LastSequence + 1} but skipped {sequence}. Events must be skipped in order too.");

    private static TeamStats Tally(TeamStats stats, MatchEvent matchEvent) => matchEvent switch
    {
        { Type: MatchEventType.Pass } => stats with
        {
            Passes = stats.Passes + 1,
            PassesCompleted = stats.PassesCompleted + (matchEvent.Successful ? 1 : 0)
        },
        { Type: MatchEventType.Shot, Shot: { } shot } => stats with
        {
            Shots = stats.Shots + 1,
            ShotsOnTarget = stats.ShotsOnTarget + (shot.OnTarget ? 1 : 0),
            Goals = stats.Goals + (shot.Outcome == ShotOutcome.Goal ? 1 : 0),
            ExpectedGoals = stats.ExpectedGoals + shot.ExpectedGoals
        },
        { Type: MatchEventType.Foul } => stats with { Fouls = stats.Fouls + 1 },
        { Type: MatchEventType.Card, Card: CardColour.Yellow } => stats with { YellowCards = stats.YellowCards + 1 },
        { Type: MatchEventType.Card, Card: CardColour.Red } => stats with { RedCards = stats.RedCards + 1 },
        _ => stats
    };
}
