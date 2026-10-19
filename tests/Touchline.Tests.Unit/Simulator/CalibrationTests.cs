extern alias simulator;
using FluentAssertions;
using FluentAssertions.Execution;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// Plays 200 seeded matches and checks the per-match averages land inside realistic ranges
/// for a top-flight league (agreed for slice 03, extended by ADR-019). Seeds are fixed, so
/// the result is exact and repeatable: a failure here means a tuning change moved the game,
/// never flakiness.
/// </summary>
[TestFixture]
public class CalibrationTests
{
    private const int _matches = 200;

    private MatchAverages _averages = null!;

    [OneTimeSetUp]
    public void PlayTheSeason()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var games = Enumerable.Range(1000, _matches)
            .Select(seed => new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], seed).Play().ToList())
            .ToList();

        _averages = MatchAverages.From(games);
    }

    [Test]
    public void Averages_land_inside_the_agreed_ranges()
    {
        // Printed so a tuning run shows every number, not just the ones out of range.
        TestContext.Out.WriteLine(_averages);
        using var scope = new AssertionScope();

        _averages.Goals.Should().BeInRange(2.2, 3.4, "goals per match");
        _averages.Shots.Should().BeInRange(20, 30, "shots per match");
        _averages.OnTargetShare.Should().BeInRange(0.28, 0.40, "share of shots on target");
        _averages.ExpectedGoals.Should().BeApproximately(_averages.Goals, 0.4, "total xG should track goals");
        _averages.Passes.Should().BeInRange(750, 1050, "passes per match");
        _averages.PassCompletion.Should().BeInRange(0.77, 0.87, "pass completion");
        _averages.Fouls.Should().BeInRange(16, 26, "fouls per match");
        _averages.YellowCards.Should().BeInRange(2.5, 5, "yellow cards per match");
        _averages.Penalties.Should().BeInRange(0.1, 0.5, "penalties per match");
        _averages.RedCards.Should().BeInRange(0.05, 0.25, "red cards per match, including second yellows");

        // ADR-019
        _averages.WeakFootShareOfFootedShots.Should().BeInRange(0.15, 0.30, "weak-foot shots as a share of footed shots");
        _averages.HeaderShare.Should().BeInRange(0.10, 0.20, "headers as a share of all shots");
        _averages.MeanExpectedCompletion.Should().BeApproximately(_averages.PassCompletion, 0.02, "xPass should track pass completion");

        // ADR-020
        _averages.WoodworkHits.Should().BeInRange(0.5, 1.3, "shots hitting the post or bar per match");
        _averages.Advantages.Should().BeInRange(2, 6, "advantages played per match");
    }

    private sealed record MatchAverages(
        double Goals, double Shots, double OnTargetShare, double ExpectedGoals, double Passes,
        double PassCompletion, double Fouls, double YellowCards, double RedCards, double Penalties, double Events,
        double WeakFootShareOfFootedShots, double HeaderShare, double MeanExpectedCompletion,
        double WoodworkHits, double Advantages)
    {
        public static MatchAverages From(IReadOnlyList<List<MatchEvent>> games)
        {
            var shots = games.SelectMany(g => g).Where(e => e.Type == MatchEventType.Shot).ToList();
            var footed = shots.Where(s => s.Shot!.BodyPart != BodyPart.Head).ToList();
            var passes = games.SelectMany(g => g).Where(e => e.Type == MatchEventType.Pass).ToList();
            double PerMatch(Func<MatchEvent, bool> predicate) => games.Average(g => g.Count(predicate));

            return new MatchAverages(
                Goals: PerMatch(e => e.IsGoal),
                Shots: PerMatch(e => e.Type == MatchEventType.Shot),
                OnTargetShare: shots.Count(s => s.Shot!.OnTarget) / (double)shots.Count,
                ExpectedGoals: games.Average(g => g.Where(e => e.Shot is not null).Sum(e => e.Shot!.ExpectedGoals)),
                Passes: PerMatch(e => e.Type == MatchEventType.Pass),
                PassCompletion: passes.Count(p => p.Successful) / (double)passes.Count,
                Fouls: PerMatch(e => e.Type == MatchEventType.Foul),
                YellowCards: PerMatch(e => e.Card == CardColour.Yellow),
                RedCards: PerMatch(e => e.Card == CardColour.Red),
                Penalties: PerMatch(e => e.Shot is { Situation: ShotSituation.Penalty }),
                Events: games.Average(g => g.Count),
                WeakFootShareOfFootedShots: footed.Count(s => s.Shot!.WeakFoot) / (double)footed.Count,
                HeaderShare: (shots.Count - footed.Count) / (double)shots.Count,
                MeanExpectedCompletion: passes.Average(p => p.Pass!.ExpectedCompletion),
                WoodworkHits: PerMatch(e => e.Shot is { HitWoodwork: true }),
                Advantages: PerMatch(e => e.AdvantagePlayed));
        }
    }
}
