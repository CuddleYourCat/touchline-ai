extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// Checks that player traits change outcomes, not expectations (ADR-019). Goals are rare,
/// so these comparisons need a long season: on 200 matches the noise in a group's goals
/// per xG (about ±10%) is bigger than the effect being tested.
/// </summary>
[TestFixture]
public class PlayerTraitTests
{
    private const int _matches = 1000;

    private List<MatchEvent> _events = null!;
    private Dictionary<string, Player> _players = null!;

    [OneTimeSetUp]
    public void PlayALongSeason()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        _events = Enumerable.Range(5000, _matches)
            .SelectMany(seed => new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], seed).Play())
            .Where(e => e.Type is MatchEventType.Shot or MatchEventType.Pass)
            .ToList();
        _players = teams.Values.SelectMany(t => t.Squad).ToDictionary(p => p.Id);
    }

    [Test]
    public void Strong_finishers_score_more_per_xg_than_weak_finishers()
    {
        var strong = Shots(s => _players[s.PlayerId!].Finishing >= 0.65);
        var weak = Shots(s => _players[s.PlayerId!].Finishing <= 0.35);
        TestContext.Out.WriteLine($"Goals per xG: strong finishers {GoalsPerXg(strong):0.00} ({strong.Count} shots), weak {GoalsPerXg(weak):0.00} ({weak.Count} shots)");

        GoalsPerXg(strong).Should().BeGreaterThan(1.0, "a strong finisher beats an average player's xG");
        GoalsPerXg(weak).Should().BeLessThan(1.0, "a weak finisher falls short of it");
        GoalsPerXg(strong).Should().BeGreaterThan(GoalsPerXg(weak) * 1.15);
    }

    [Test]
    public void Weak_foot_shots_score_less_per_xg_than_strong_foot_shots()
    {
        var weakFoot = Shots(s => s.Shot!.WeakFoot);
        var strongFoot = Shots(s => s.Shot is { WeakFoot: false, BodyPart: not BodyPart.Head });
        TestContext.Out.WriteLine($"Goals per xG: weak foot {GoalsPerXg(weakFoot):0.00} ({weakFoot.Count} shots), strong foot {GoalsPerXg(strongFoot):0.00}");

        GoalsPerXg(weakFoot).Should().BeLessThan(GoalsPerXg(strongFoot) * 0.9);
    }

    [Test]
    public void Good_passers_complete_more_of_their_xpass_than_poor_passers()
    {
        // Every pass, both feet: the feet are normalised, so a player's usual mix is what xPass measures.
        var passes = _events.Where(e => e.Pass is not null).ToList();
        double CompletionPerXPass(Func<Player, bool> who)
        {
            var group = passes.Where(p => who(_players[p.PlayerId!])).ToList();
            return group.Count(p => p.Successful) / group.Sum(p => p.Pass!.ExpectedCompletion);
        }

        var good = CompletionPerXPass(p => p.Passing >= 0.65);
        var poor = CompletionPerXPass(p => p.Passing <= 0.42);
        TestContext.Out.WriteLine($"Completed per xPass: good passers {good:0.000}, poor {poor:0.000}");

        good.Should().BeGreaterThan(1.0, "a good passer beats an average player's xPass");
        poor.Should().BeLessThan(1.0, "a poor passer falls short of it");
        good.Should().BeGreaterThan(poor + 0.03);
    }

    private List<MatchEvent> Shots(Func<MatchEvent, bool> predicate) =>
        _events.Where(e => e.Shot is { Situation: not ShotSituation.Penalty }).Where(predicate).ToList();

    private static double GoalsPerXg(IReadOnlyCollection<MatchEvent> shots) =>
        shots.Count(s => s.IsGoal) / shots.Sum(s => s.Shot!.ExpectedGoals);
}
