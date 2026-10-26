using Touchline.Domain.Matches;

namespace Touchline.Simulator.Calibration;

/// <summary>
/// The per-match averages of a set of simulated matches, the numbers the calibration ranges
/// check (ADR-019, ADR-020, ADR-022). Shared by the calibration tests and the dataset export
/// (ADR-063), so both measure the same thing the same way.
/// </summary>
public sealed record SeasonAverages(
    double Goals, double Shots, double OnTargetShare, double ExpectedGoals, double Passes,
    double PassCompletion, double Fouls, double YellowCards, double RedCards, double Penalties, double Events,
    double WeakFootShareOfFootedShots, double HeaderShare, double MeanExpectedCompletion,
    double WoodworkHits, double Advantages,
    double ThrowIns, double GoalKicks, double Corners, double ClearedOffLine)
{
    /// <summary>Averages the matches, each given as its full list of events.</summary>
    /// <param name="matches">One list of events per match. Must not be empty.</param>
    /// <returns>The averages across every match.</returns>
    public static SeasonAverages From(IReadOnlyList<IReadOnlyList<MatchEvent>> matches)
    {
        var shots = matches.SelectMany(g => g).Where(e => e.Type == MatchEventType.Shot).ToList();
        var footed = shots.Where(s => s.Shot!.BodyPart != BodyPart.Head).ToList();
        var passes = matches.SelectMany(g => g).Where(e => e.Type == MatchEventType.Pass).ToList();
        double PerMatch(Func<MatchEvent, bool> predicate) => matches.Average(g => g.Count(predicate));

        return new SeasonAverages(
            Goals: PerMatch(e => e.IsGoal),
            Shots: PerMatch(e => e.Type == MatchEventType.Shot),
            OnTargetShare: shots.Count(s => s.Shot!.OnTarget) / (double)shots.Count,
            ExpectedGoals: matches.Average(g => g.Where(e => e.Shot is not null).Sum(e => e.Shot!.ExpectedGoals)),
            Passes: PerMatch(e => e.Type == MatchEventType.Pass),
            PassCompletion: passes.Count(p => p.Successful) / (double)passes.Count,
            Fouls: PerMatch(e => e.Type == MatchEventType.Foul),
            YellowCards: PerMatch(e => e.Card == CardColour.Yellow),
            RedCards: PerMatch(e => e.Card == CardColour.Red),
            Penalties: PerMatch(e => e.Shot is { Situation: ShotSituation.Penalty }),
            Events: matches.Average(g => g.Count),
            WeakFootShareOfFootedShots: footed.Count(s => s.Shot!.WeakFoot) / (double)footed.Count,
            HeaderShare: (shots.Count - footed.Count) / (double)shots.Count,
            MeanExpectedCompletion: passes.Average(p => p.Pass!.ExpectedCompletion),
            WoodworkHits: PerMatch(e => e.Shot is { HitWoodwork: true }),
            Advantages: PerMatch(e => e.AdvantagePlayed),
            ThrowIns: PerMatch(e => e.Restart == RestartKind.ThrowIn),
            GoalKicks: PerMatch(e => e.Restart == RestartKind.GoalKick),
            Corners: PerMatch(e => e.Restart == RestartKind.Corner),
            ClearedOffLine: PerMatch(e => e.Shot is { ClearedOffLine: true }));
    }
}
