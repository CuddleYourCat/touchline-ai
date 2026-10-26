extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// The engine's side of ADR-022: the ball going out and play restarting, goals in the net,
/// clearances off the line, and the new key moments appearing in real matches.
/// </summary>
[TestFixture]
public class LinesAndMomentsTests
{
    private const int _matches = 400;

    private List<List<MatchEvent>> _season = null!;

    [OneTimeSetUp]
    public void PlayTheSeason()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        _season = Enumerable.Range(7000, _matches)
            .Select(seed => new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], seed).Play().ToList())
            .ToList();
    }

    private IEnumerable<(List<MatchEvent> Match, MatchEvent Out, MatchEvent Restart)> Restarts() =>
        _season.SelectMany(m => m.Select((e, i) => (Match: m, Event: e, Index: i))
            .Where(x => x.Event.Type == MatchEventType.BallOutOfPlay)
            .Select(x => (x.Match, x.Event, x.Match.Skip(x.Index + 1).First(e => e.Type is not (MatchEventType.Card or MatchEventType.Substitution)))));

    [Test]
    public void A_throw_in_goes_to_the_other_team_from_the_touchline_where_the_ball_went_out()
    {
        var throwIns = Restarts().Where(x => x.Out.Restart == RestartKind.ThrowIn).ToList();

        throwIns.Should().NotBeEmpty();
        foreach (var (_, ballOut, restart) in throwIns)
        {
            restart.Type.Should().Be(MatchEventType.Pass);
            restart.Team.Should().NotBe(ballOut.Team, "the team that didn't touch it last throws it in");
            restart.Start.Y.Should().Be(ballOut.Start.Y < 0 ? 0 : PitchDimensions.Width);
            restart.Start.X.Should().BeApproximately(ballOut.Start.X, 1e-9);
            restart.Pass!.Height.Should().Be(PassHeight.Low);
        }
    }

    [Test]
    public void A_corner_is_taken_from_the_flag_by_the_attacking_team()
    {
        var corners = Restarts().Where(x => x.Out.Restart == RestartKind.Corner).ToList();

        corners.Should().NotBeEmpty();
        corners.Should().OnlyContain(x => x.Restart.Type == MatchEventType.Pass
            && x.Restart.Team != x.Out.Team
            && (x.Restart.Start.X == 0 || x.Restart.Start.X == PitchDimensions.Length)
            && (x.Restart.Start.Y == 0 || x.Restart.Start.Y == PitchDimensions.Width));
    }

    [Test]
    public void A_goal_kick_is_taken_by_the_defending_team_from_its_six_yard_box()
    {
        var goalKicks = Restarts().Where(x => x.Out.Restart == RestartKind.GoalKick).ToList();

        goalKicks.Should().NotBeEmpty();
        goalKicks.Should().OnlyContain(x => x.Restart.Team != x.Out.Team
            && Math.Abs(x.Restart.Start.X - (x.Out.Start.X > PitchDimensions.Length ? PitchDimensions.Length - 5.5 : 5.5)) < 1e-9);
    }

    [Test]
    public void Every_goal_ends_in_the_net_and_a_few_are_close_calls()
    {
        var goals = _season.SelectMany(m => m).Where(e => e.IsGoal).ToList();
        var overBy = goals.Select(g => PitchDimensions.OverTheGoalLineBy(g.End!.Value, g.Team)).ToList();
        TestContext.Out.WriteLine($"{goals.Count} goals, {overBy.Count(o => o < 0.05)} over by under 5 cm");

        overBy.Should().OnlyContain(o => o > 0);
        overBy.Should().Contain(o => o < 0.05, "about 1 goal in 100 is a goal-line call");
    }

    [Test]
    public void Clearances_off_the_line_name_the_defender_and_never_wholly_cross()
    {
        var clearances = _season.SelectMany(m => m).Where(e => e.Shot is { ClearedOffLine: true }).ToList();

        clearances.Should().NotBeEmpty();
        clearances.Should().OnlyContain(c => c.BlockedByPlayerId != null && PitchDimensions.OverTheGoalLineBy(c.End!.Value, c.Team) <= 0);
    }

    // Added after code review

    [Test]
    public void A_goalkeeper_never_blocks_or_clears_off_the_line()
    {
        var keepers = TeamProfileCatalog.LoadEmbedded().Values.SelectMany(t => t.Squad)
            .Where(p => p.Position == PlayerPosition.Goalkeeper).Select(p => p.Id).ToHashSet();

        var blockers = _season.SelectMany(m => m).Where(e => e.BlockedByPlayerId != null).Select(e => e.BlockedByPlayerId!).ToList();

        blockers.Should().NotBeEmpty();
        blockers.Should().NotContain(id => keepers.Contains(id), "a keeper stopping a shot is a save");
    }

    [Test]
    public void A_miss_ends_beyond_the_goal_line_and_goes_out_there_while_a_shot_off_the_woodwork_stays_in_play()
    {
        var shotsAndNext = _season.SelectMany(m => m.Zip(m.Skip(1))).Where(pair => pair.First.Shot is { Outcome: ShotOutcome.OffTarget }).ToList();
        var misses = shotsAndNext.Where(pair => !pair.First.Shot!.HitWoodwork).ToList();
        var woodwork = shotsAndNext.Where(pair => pair.First.Shot!.HitWoodwork).ToList();

        misses.Should().NotBeEmpty();
        misses.Should().OnlyContain(pair => PitchDimensions.OverTheGoalLineBy(pair.First.End!.Value, pair.First.Team) > 0);
        misses.Should().OnlyContain(pair => pair.Second.Type == MatchEventType.BallOutOfPlay
            && pair.Second.Restart == RestartKind.GoalKick
            && pair.Second.Start == pair.First.End);
        woodwork.Should().NotBeEmpty();
        woodwork.Should().OnlyContain(pair => PitchDimensions.IsInPlay(pair.First.End!.Value) && pair.Second.Type != MatchEventType.BallOutOfPlay);
    }

    [Test]
    public void The_new_key_moments_happen_in_real_matches_and_one_event_is_never_two_moments()
    {
        var detector = new KeyMomentDetector();
        var moments = new List<KeyMoment>();
        foreach (var match in _season)
        {
            var state = MatchState.KickOff(match[0].MatchId);
            foreach (var e in match)
            {
                if (detector.Detect(e, state) is { } moment)
                {
                    moments.Add(moment);
                }

                state = state.Apply(e);
            }
        }

        var byKind = moments.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count());
        TestContext.Out.WriteLine(string.Join(", ", byKind.Select(k => $"{k.Key} {k.Value / (double)_matches:0.000}")));

        byKind.Keys.Should().Contain([KeyMomentKind.PenaltySaved, KeyMomentKind.PenaltyMissed, KeyMomentKind.ClearedOffLine]);
        moments.Should().Contain(m => m.GoalLineMarginCm != null, "close calls happen over a season");
        moments.Select(m => (m.Trigger.MatchId, m.Trigger.Sequence)).Should().OnlyHaveUniqueItems();
        moments.Where(m => m.Trigger.Shot is { Situation: ShotSituation.Penalty })
            .Should().NotContain(m => m.Kind == KeyMomentKind.BigChance, "a penalty that didn't go in is saved or missed, never just a big chance");
    }
}
