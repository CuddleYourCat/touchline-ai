extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// Checks that player traits change outcomes, not expectations (ADR-019). Goals are rare,
/// so these comparisons need a long season: on 200 matches the noise in a group's goals
/// per xG (about ±10%) is bigger than the effect being tested, and even on 1,000 the
/// finishing comparison sat about 2 standard deviations from its threshold.
/// </summary>
[TestFixture]
public class PlayerTraitTests
{
    private const int _matches = 2000;

    private List<MatchEvent> _events = null!;
    private Dictionary<string, Player> _players = null!;

    [OneTimeSetUp]
    public void PlayALongSeason()
    {
        // Penalty tendencies are reset to the league average, so this one season also checks
        // the average duel. Every other check here ignores penalties, so nothing else changes.
        static TeamProfile AveragePenalties(TeamProfile team) => team with
        {
            Squad = [.. team.Squad.Select(p => p with { PenaltyAim = null, Dive = null })]
        };
        var teams = TeamProfileCatalog.LoadEmbedded();
        _events = Enumerable.Range(5000, _matches)
            .SelectMany(seed => new MatchSimulator(AveragePenalties(teams["north-bank-fc"]), AveragePenalties(teams["thames-valley-united"]), seed).Play())
            .Where(e => e.Type is MatchEventType.Shot or MatchEventType.Pass)
            .ToList();
        _players = teams.Values.SelectMany(t => t.Squad).ToDictionary(p => p.Id);
    }

    [Test]
    public void Strong_finishers_score_more_per_xg_than_weak_finishers()
    {
        var strong = Shots(s => _players[s.PlayerId!].Finishing >= 0.55);
        var weak = Shots(s => _players[s.PlayerId!].Finishing <= 0.25);
        TestContext.Out.WriteLine($"Goals per xG: strong finishers {GoalsPerXg(strong):0.00} ({strong.Count} shots), weak {GoalsPerXg(weak):0.00} ({weak.Count} shots)");

        GoalsPerXg(strong).Should().BeGreaterThan(1.0, "a strong finisher beats an average player's xG");
        GoalsPerXg(weak).Should().BeLessThan(1.0, "a weak finisher falls short of it");
        GoalsPerXg(strong).Should().BeGreaterThan(GoalsPerXg(weak) * 1.10, "the factors differ by about 1.26, and noise is about 6%");
    }

    // Added in the officials, temperament and height slice (ADR-020)

    [Test]
    public void The_average_shot_is_taken_at_average_finishing()
    {
        // xG models learn from the players who shoot, mostly forwards, so 0.5 means the
        // average shot, not the average squad member. Otherwise goals run ahead of xG.
        var shooters = Shots(_ => true).Select(s => _players[s.PlayerId!].Finishing).ToList();
        TestContext.Out.WriteLine($"Finishing per shot: {shooters.Average():0.000}");

        shooters.Average().Should().BeInRange(0.45, 0.55);
    }

    [Test]
    public void Average_takers_against_average_keepers_convert_at_the_penalty_xg()
    {
        // xG 0.76 describes an average penalty, so the duel is checked with every taker and
        // keeper at the league-average tendencies, as this fixture's season is played. The
        // real squads differ on purpose: both keepers happen to read the opposing taker.
        var penalties = _events.Where(e => e.Shot is { Situation: ShotSituation.Penalty }).ToList();
        var conversion = penalties.Count(p => p.IsGoal) / (double)penalties.Count;
        TestContext.Out.WriteLine($"Penalties with average tendencies: {penalties.Count}, converted {conversion:P1}");

        conversion.Should().BeInRange(0.70, 0.82, "an average penalty is worth 0.76");
    }

    [Test]
    public void A_keeper_who_reads_the_taker_saves_far_more_than_one_who_guesses_wrong()
    {
        // A controlled duel: Reggie Ashworth always aims low left, and Marcus Holt either always
        // guesses that way or always dives the other. Expected conversion is about 44% against
        // 97%, a gap no sampling noise can close. The real squads' tendencies are milder.
        var teams = TeamProfileCatalog.LoadEmbedded();
        var lowLeft = new PenaltyAim(1, 0, 0, 0, 0, 0);
        var northBank = teams["north-bank-fc"] with
        {
            Squad = [.. teams["north-bank-fc"].Squad.Select(p => p.Id == "nbfc-07" ? p with { PenaltyAim = lowLeft } : p)]
        };
        TeamProfile ThamesValleyWithHoltDiving(DiveBias dive) => teams["thames-valley-united"] with
        {
            Squad = [.. teams["thames-valley-united"].Squad.Select(p => p.Id == "tvu-01" ? p with { Dive = dive } : p)]
        };
        double Conversion(DiveBias dive)
        {
            var penalties = Enumerable.Range(11000, 800)
                .SelectMany(seed => new MatchSimulator(northBank, ThamesValleyWithHoltDiving(dive), seed).Play())
                .Where(e => e.Shot is { Situation: ShotSituation.Penalty } && e.PlayerId == "nbfc-07")
                .ToList();
            return penalties.Count(p => p.IsGoal) / (double)penalties.Count;
        }

        var read = Conversion(new DiveBias(1, 0, 0));
        var fooled = Conversion(new DiveBias(0, 0, 1));
        TestContext.Out.WriteLine($"Ashworth's penalties converted: keeper reads him {read:P0}, keeper guesses wrong {fooled:P0}");

        read.Should().BeLessThan(0.62);
        fooled.Should().BeGreaterThan(0.9);
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
