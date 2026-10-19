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

        match[1].Type.Should().Be(MatchEventType.KickOff, "the metadata comes first, then play starts");
        match[^1].Type.Should().Be(MatchEventType.PeriodEnd);
        match[^1].Time.Period.Should().Be(MatchPeriod.SecondHalf);
        match.Count(e => e.Type == MatchEventType.PeriodEnd).Should().Be(2);
        match.Where(e => e.Type == MatchEventType.PeriodEnd).First().Time.Clock.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(45));
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
        // A foul where advantage was played doesn't stop play, so the counter rightly carries on (ADR-020).
        var countersAfterFouls = _season.SelectMany(m => m
            .Where(e => e.Shot is { Situation: ShotSituation.Counter })
            .Where(shot => m.Any(e => e.PossessionId == shot.PossessionId && e is { Type: MatchEventType.Foul, AdvantagePlayed: false } && e.Sequence < shot.Sequence)));

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

    // Added in the richer-events slice (ADR-019)

    private static readonly Dictionary<string, Player> _players =
        NorthBank.Squad.Concat(ThamesValley.Squad).ToDictionary(p => p.Id);

    [Test]
    public void The_first_event_is_the_match_metadata_and_it_is_the_only_one()
    {
        var kickOff = new DateTimeOffset(2026, 10, 25, 16, 30, 0, TimeSpan.Zero);
        var match = new MatchSimulator(NorthBank, ThamesValley, seed: 7, new MatchOptions { KickOffUtc = kickOff }).Play().ToList();

        var metadata = match[0].Metadata;
        match[0].Type.Should().Be(MatchEventType.MatchMetadata);
        match[0].Sequence.Should().Be(1);
        match.Should().ContainSingle(e => e.Type == MatchEventType.MatchMetadata);

        metadata!.Venue.Should().Be(NorthBank.Venue, "the match is played at the home ground");
        metadata.KickOffUtc.Should().Be(kickOff);
        metadata.Seed.Should().Be(7);
        metadata.Home.ShortName.Should().Be("NBK");
        metadata.Away.ShortName.Should().Be("TVU");
        metadata.Home.StartingEleven.Should().HaveCount(11).And.OnlyHaveUniqueItems();
        metadata.Away.StartingEleven.Should().HaveCount(11).And.OnlyHaveUniqueItems();
        _players[metadata.Home.StartingEleven[0]].Position.Should().Be(PlayerPosition.Goalkeeper);
    }

    [Test]
    public void Only_the_starting_elevens_named_in_the_metadata_take_part()
    {
        var match = PlayMatch(seed: 11);
        var starters = match[0].Metadata!.Home.StartingEleven.Concat(match[0].Metadata!.Away.StartingEleven).ToHashSet();

        match.SelectMany(e => new[] { e.PlayerId, e.RecipientPlayerId, e.AssistPlayerId, e.FouledPlayerId })
            .OfType<string>()
            .Should().OnlyContain(id => starters.Contains(id), "there are no substitutions yet");
    }

    [Test]
    public void Passes_carry_xpass_and_both_pressured_and_unpressured_actions_appear()
    {
        var events = _season.Take(20).SelectMany(m => m).ToList();
        var passes = events.Where(e => e.Type == MatchEventType.Pass).ToList();

        passes.Should().OnlyContain(p => p.Pass!.ExpectedCompletion > 0 && p.Pass.ExpectedCompletion < 1);
        passes.Select(p => p.Pass!.ExpectedCompletion).Distinct().Should().HaveCountGreaterThan(3, "distance, pressure and the target's third all change xPass");
        foreach (var type in new[] { MatchEventType.Pass, MatchEventType.Carry, MatchEventType.Shot })
        {
            events.Where(e => e.Type == type).Should().Contain(e => e.UnderPressure, type.ToString())
                .And.Contain(e => !e.UnderPressure, type.ToString());
        }
    }

    [Test]
    public void Speeds_are_realistic_for_the_ball_and_the_player()
    {
        var events = _season.Take(50).SelectMany(m => m).ToList();

        events.Where(e => e.Pass is not null).Should().OnlyContain(e => e.Pass!.SpeedKmh >= 30 && e.Pass.SpeedKmh <= 90);
        events.Where(e => e.Shot is { BodyPart: BodyPart.Head }).Should().NotBeEmpty()
            .And.OnlyContain(e => e.Shot!.SpeedKmh >= 40 && e.Shot.SpeedKmh <= 70);
        events.Where(e => e.Shot is { BodyPart: not BodyPart.Head }).Should().OnlyContain(e => e.Shot!.SpeedKmh >= 54 && e.Shot.SpeedKmh <= 125);
        events.Where(e => e.Carry is not null).Should().OnlyContain(e => e.Carry!.SpeedKmh >= 0.7 * _players[e.PlayerId!].TopSpeedKmh - 0.05);
    }

    [Test]
    public void A_carry_beats_the_player_s_recorded_top_speed_only_rarely_and_only_just()
    {
        var carries = _season.SelectMany(m => m).Where(e => e.Carry is not null).ToList();
        var faster = carries.Where(e => e.Carry!.SpeedKmh > _players[e.PlayerId!].TopSpeedKmh).ToList();
        TestContext.Out.WriteLine($"Carries faster than the recorded best: {faster.Count} in {_season.Count} matches");

        faster.Should().NotBeEmpty("records do get broken, and the milestones detector needs them");
        faster.Count.Should().BeLessThan(_season.Count, "fewer than one a match");
        faster.Should().OnlyContain(e => e.Carry!.SpeedKmh <= _players[e.PlayerId!].TopSpeedKmh + 0.5);
        carries.Should().NotContain(e => e.Carry!.SpeedKmh == _players[e.PlayerId!].TopSpeedKmh,
            "a record-breaking carry must not round back down to exactly the record");
    }

    [Test]
    public void The_kick_off_time_is_recorded_in_utc_whatever_offset_it_is_given_in()
    {
        var britishSummerTime = new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.FromHours(1));

        var metadata = new MatchSimulator(NorthBank, ThamesValley, seed: 7, new MatchOptions { KickOffUtc = britishSummerTime }).Play().First().Metadata!;

        metadata.KickOffUtc.Offset.Should().Be(TimeSpan.Zero);
        metadata.KickOffUtc.Should().Be(britishSummerTime, "it's the same instant");
        metadata.KickOffUtc.Hour.Should().Be(14);
    }

    [Test]
    public void The_weaker_foot_is_never_used_by_two_footed_players_or_from_set_pieces()
    {
        var shots = _season.SelectMany(m => m).Where(e => e.Shot is not null).ToList();

        shots.Where(e => e.Shot!.WeakFoot).Should().NotBeEmpty()
            .And.OnlyContain(e => _players[e.PlayerId!].PreferredFoot != PreferredFoot.Both)
            .And.OnlyContain(e => e.Shot!.Situation == ShotSituation.OpenPlay || e.Shot.Situation == ShotSituation.Counter);
        _season.SelectMany(m => m).Where(e => e.Pass is { WeakFoot: true })
            .Should().OnlyContain(e => _players[e.PlayerId!].PreferredFoot != PreferredFoot.Both);
    }

    [Test]
    public void A_footed_shot_uses_the_foot_that_matches_the_weak_foot_flag()
    {
        var footed = _season.SelectMany(m => m).Where(e => e.Shot is { BodyPart: not BodyPart.Head }).ToList();

        footed.Where(e => _players[e.PlayerId!].PreferredFoot != PreferredFoot.Both)
            .Should().OnlyContain(e => (e.Shot!.BodyPart == StrongerFoot(_players[e.PlayerId!].PreferredFoot)) != e.Shot.WeakFoot);
    }

    private static BodyPart StrongerFoot(PreferredFoot foot) => foot == PreferredFoot.Left ? BodyPart.LeftFoot : BodyPart.RightFoot;
}
