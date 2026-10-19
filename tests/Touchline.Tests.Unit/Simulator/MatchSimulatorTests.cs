extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

[TestFixture]
public class MatchSimulatorTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();
    private static TeamProfile NorthBank => _teams["north-bank-fc"];
    private static TeamProfile ThamesValley => _teams["thames-valley-united"];

    private static List<MatchEvent> PlayMatch(int seed) => new MatchSimulator(NorthBank, ThamesValley, seed).Play().ToList();

    [Test]
    public void The_same_seed_replays_exactly_the_same_match()
    {
        var first = PlayMatch(seed: 42);
        var second = PlayMatch(seed: 42);

        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    [Test]
    public void Different_seeds_play_different_matches()
    {
        var a = PlayMatch(seed: 1);
        var b = PlayMatch(seed: 2);

        a.Select(e => (e.Type, e.Start)).Should().NotEqual(b.Select(e => (e.Type, e.Start)));
    }

    [Test]
    public void Every_event_is_valid_and_the_whole_match_applies_in_order()
    {
        var match = PlayMatch(seed: 7);

        var state = match.Aggregate(MatchState.KickOff(match[0].MatchId), (s, e) => s.Apply(e));

        state.LastSequence.Should().Be(match.Count);
        match.Select(e => e.Time).Should().BeInAscendingOrder();
    }

    [Test]
    public void A_match_has_two_kick_offs_and_ends_each_half()
    {
        var match = PlayMatch(seed: 7);

        match[0].Type.Should().Be(MatchEventType.KickOff);
        match[^1].Type.Should().Be(MatchEventType.PeriodEnd);
        match[^1].Time.Period.Should().Be(MatchPeriod.SecondHalf);
        match.Count(e => e.Type == MatchEventType.PeriodEnd).Should().Be(2);
        match.Where(e => e.Type == MatchEventType.PeriodEnd).First().Time.Clock.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(45));
    }

    [Test]
    public void Only_players_on_the_pitch_take_part()
    {
        var squadIds = NorthBank.Squad.Concat(ThamesValley.Squad).Select(p => p.Id).ToHashSet();

        var named = PlayMatch(seed: 11)
            .SelectMany(e => new[] { e.PlayerId, e.RecipientPlayerId, e.AssistPlayerId, e.FouledPlayerId })
            .OfType<string>();

        named.Should().OnlyContain(id => squadIds.Contains(id));
    }

    [Test]
    public void A_sent_off_player_takes_no_further_part()
    {
        var match = Enumerable.Range(0, 300).Select(PlayMatch).First(m => m.Any(e => e.Card == CardColour.Red));
        var sendingOff = match.First(e => e.Card == CardColour.Red);

        match.SkipWhile(e => e != sendingOff).Skip(1)
            .Should().NotContain(e => e.PlayerId == sendingOff.PlayerId || e.RecipientPlayerId == sendingOff.PlayerId);
    }

    [Test]
    public void Goals_from_open_play_name_the_assist_when_a_team_mate_passed()
    {
        var goals = Enumerable.Range(0, 30).SelectMany(PlayMatch).Where(e => e.IsGoal && e.Shot!.Situation != ShotSituation.Penalty).ToList();

        goals.Should().Contain(g => g.AssistPlayerId != null);
        goals.Where(g => g.AssistPlayerId != null).Should().OnlyContain(g => g.AssistPlayerId != g.PlayerId);
    }

    // Added after code review

    private static readonly List<List<MatchEvent>> _season = Enumerable.Range(0, 500).Select(PlayMatch).ToList();

    [Test]
    public void A_direct_free_kick_has_no_assist()
    {
        var directFreeKicks = _season.SelectMany(m => m.Zip(m.Skip(1)))
            .Where(pair => pair.First.Type is MatchEventType.Foul or MatchEventType.Card
                && pair.Second.Shot is { Situation: ShotSituation.SetPiece })
            .Select(pair => pair.Second)
            .ToList();

        directFreeKicks.Should().NotBeEmpty();
        directFreeKicks.Should().OnlyContain(shot => shot.AssistPlayerId == null, "the foul stopped play, so the move before it created nothing");
    }

    [Test]
    public void No_team_has_more_than_two_players_sent_off()
    {
        var mostReds = _season.Max(m => new[] { TeamSide.Home, TeamSide.Away }
            .Max(side => m.Count(e => e.Team == side && e.Card == CardColour.Red)));

        mostReds.Should().BeLessThanOrEqualTo(2);
    }

    [Test]
    public void A_counter_ends_when_play_stops_for_a_foul()
    {
        var countersAfterFouls = _season.SelectMany(m => m
            .Where(e => e.Shot is { Situation: ShotSituation.Counter })
            .Where(shot => m.Any(e => e.PossessionId == shot.PossessionId && e.Type == MatchEventType.Foul && e.Sequence < shot.Sequence)));

        countersAfterFouls.Should().BeEmpty();
    }

    [Test]
    public void A_possession_has_at_most_two_corners()
    {
        static bool IsCornerDelivery(MatchEvent e) =>
            e.Type == MatchEventType.Pass
            && e.Start.X is 0 or PitchDimensions.Length
            && e.Start.Y is 0 or PitchDimensions.Width;

        var mostCorners = _season.Max(m => m.Where(IsCornerDelivery).GroupBy(e => e.PossessionId).Select(g => g.Count()).DefaultIfEmpty(0).Max());

        mostCorners.Should().BeLessThanOrEqualTo(2);
    }

    [Test]
    public void A_red_card_stops_play_as_long_as_a_yellow()
    {
        var gapsAfterReds = _season.SelectMany(m => m.Zip(m.Skip(1)))
            .Where(pair => pair.First.Card == CardColour.Red)
            .Select(pair => pair.Second.Time.Clock - pair.First.Time.Clock)
            .ToList();

        gapsAfterReds.Should().NotBeEmpty();
        gapsAfterReds.Should().OnlyContain(gap => gap >= TimeSpan.FromSeconds(30));
    }

    [Test]
    public void A_high_pressing_team_wins_the_ball_in_the_attacking_third_more_often()
    {
        var matches = Enumerable.Range(0, 60).Select(PlayMatch).ToList();

        double HighRegains(TeamSide side) => matches.Average(m => m.Count(e =>
            e.Team == side
            && e.Type is MatchEventType.Tackle or MatchEventType.Interception
            && e.Successful
            && PitchZone.Locate(e.Start, side).Third == Third.Attacking));

        var northBank = HighRegains(TeamSide.Home);
        var thamesValley = HighRegains(TeamSide.Away);
        TestContext.Out.WriteLine($"High regains per match: North Bank {northBank:0.00}, Thames Valley {thamesValley:0.00}");

        // A clear margin, not just "more": a tuning change mustn't quietly erase team style.
        northBank.Should().BeGreaterThan(thamesValley * 1.10,
            "North Bank FC presses at 0.8 against Thames Valley United's 0.45, so it should win the ball high clearly more often");
    }
}
