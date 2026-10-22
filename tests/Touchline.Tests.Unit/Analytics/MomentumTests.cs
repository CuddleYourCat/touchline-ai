extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Analytics;

/// <summary>Skill shot 2: the momentum meter. Make these pass in Touchline.Domain/Analytics/Momentum.cs.</summary>
[TestFixture]
public class MomentumTests
{
    private const string _matchId = "nbk-tvu-42";

    // Threat: one event at a time

    [Test]
    public void A_shot_is_worth_one_plus_three_times_its_xg()
    {
        Momentum.ThreatOf(Shot(TeamSide.Home, xg: 0.2)).Should().BeApproximately(1.6, 1e-9);
        Momentum.ThreatOf(Shot(TeamSide.Away, xg: 0.05)).Should().BeApproximately(1.15, 1e-9, "a shot from anywhere still counts");
    }

    [Test]
    public void A_completed_pass_into_the_attacking_third_is_worth_a_quarter()
    {
        Momentum.ThreatOf(Pass(TeamSide.Home, endX: 80)).Should().Be(0.25);
    }

    [Test]
    public void The_attacking_third_depends_on_which_way_the_team_attacks()
    {
        // Home attacks towards x = 105, away towards x = 0.
        Momentum.ThreatOf(Pass(TeamSide.Away, endX: 20)).Should().Be(0.25, "x = 20 is the away side's attacking third");
        Momentum.ThreatOf(Pass(TeamSide.Home, endX: 20)).Should().Be(0, "for the home side, x = 20 is its own defensive third");
    }

    [Test]
    public void A_pass_that_stays_in_midfield_or_fails_is_worth_nothing()
    {
        Momentum.ThreatOf(Pass(TeamSide.Home, endX: 60)).Should().Be(0);
        Momentum.ThreatOf(Pass(TeamSide.Home, endX: 90, successful: false)).Should().Be(0, "a pass that doesn't reach a team-mate creates no danger");
    }

    [Test]
    public void A_carry_that_fails_is_worth_nothing()
    {
        Momentum.ThreatOf(Carry(TeamSide.Home, endX: 80, successful: false)).Should().Be(0, "a carry that loses the ball creates no danger");
    }

    [Test]
    public void A_carry_into_the_attacking_third_counts_like_a_pass()
    {
        Momentum.ThreatOf(Carry(TeamSide.Home, endX: 75)).Should().Be(0.25);
        Momentum.ThreatOf(Event(MatchEventType.Foul, TeamSide.Home, minute: 10)).Should().Be(0, "fouls, cards and restarts carry no threat");
    }

    // Reading a match

    [Test]
    public void A_match_with_no_events_has_no_readings()
    {
        Momentum.Read([]).Should().BeEmpty();
    }

    [Test]
    public void A_window_where_only_one_side_threatened_reads_one_or_minus_one()
    {
        var homeOnly = Momentum.Read([Shot(TeamSide.Home, xg: 0.1, minute: 2), Pass(TeamSide.Home, endX: 85, minute: 3)]);
        var awayOnly = Momentum.Read([Pass(TeamSide.Away, endX: 15, minute: 12)]);

        homeOnly.Should().ContainSingle().Which.Value.Should().Be(1);
        awayOnly.Should().ContainSingle().Which.Value.Should().Be(-1);
    }

    [Test]
    public void A_window_is_scored_as_the_difference_over_the_total()
    {
        // Home: four passes into the final third (1.0). Away: one shot at 0.3 xG (1.9).
        var reading = Momentum.Read(
        [
            Pass(TeamSide.Home, endX: 80, minute: 21), Pass(TeamSide.Home, endX: 82, minute: 21),
            Pass(TeamSide.Home, endX: 84, minute: 22), Pass(TeamSide.Home, endX: 86, minute: 23),
            Shot(TeamSide.Away, xg: 0.3, minute: 24)
        ]).Single();

        reading.Value.Should().BeApproximately((1.0 - 1.9) / 2.9, 1e-9, "one good chance outweighs four passes that went nowhere");
        reading.Should().BeEquivalentTo(new { Period = MatchPeriod.FirstHalf, FromMinute = 20, ToMinute = 25 });
    }

    [Test]
    public void A_window_with_events_but_no_threat_reads_zero_not_nan()
    {
        var reading = Momentum.Read([Pass(TeamSide.Home, endX: 50, minute: 31), Pass(TeamSide.Away, endX: 55, minute: 33)]).Single();

        reading.Value.Should().Be(0);
    }

    [Test]
    public void Windows_follow_the_clock_in_each_half_with_added_time_as_its_own_window_in_both_halves()
    {
        var readings = Momentum.Read(
        [
            // Deliberately out of order: the reader sorts by match time.
            Pass(TeamSide.Home, endX: 50, minute: 92, period: MatchPeriod.SecondHalf),
            Pass(TeamSide.Home, endX: 50, minute: 46, period: MatchPeriod.SecondHalf),
            Pass(TeamSide.Home, endX: 50, minute: 3),
            Pass(TeamSide.Home, endX: 50, minute: 47, period: MatchPeriod.FirstHalf),
            Pass(TeamSide.Home, endX: 50, minute: 7),
            Pass(TeamSide.Home, endX: 50, minute: 44)
        ]);

        readings.Select(r => (r.Period, r.FromMinute, r.ToMinute)).Should().Equal(
            (MatchPeriod.FirstHalf, 0, 5),
            (MatchPeriod.FirstHalf, 5, 10),
            (MatchPeriod.FirstHalf, 40, 45),
            (MatchPeriod.FirstHalf, 45, 50),
            (MatchPeriod.SecondHalf, 45, 50),
            (MatchPeriod.SecondHalf, 90, 95));
    }

    [Test]
    public void A_whole_match_swings_both_ways_and_stays_in_range()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var match = new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], seed: 42).Play();

        var readings = Momentum.Read(match);

        readings.Should().HaveCountGreaterThanOrEqualTo(18, "two halves of nine five-minute windows, plus any added time");
        readings.Should().OnlyContain(r => r.Value >= -1 && r.Value <= 1);
        readings.Should().Contain(r => r.Value > 0).And.Contain(r => r.Value < 0, "a real match tilts both ways");
    }

    private static MatchEvent Event(MatchEventType type, TeamSide team, int minute, MatchPeriod? period = null, double startX = 52.5) => new MatchEvent
    {
        Sequence = minute + 1,
        MatchId = _matchId,
        PossessionId = 1,
        Time = MatchTime.At(period ?? (minute < 45 ? MatchPeriod.FirstHalf : MatchPeriod.SecondHalf), minute),
        Type = type,
        Team = team,
        PlayerId = team == TeamSide.Home ? "nbfc-07" : "tvu-09",
        Start = new PitchPosition(startX, 34)
    }.WithRequiredDetail();

    private static MatchEvent Pass(TeamSide team, double endX, bool successful = true, int minute = 10, MatchPeriod? period = null) =>
        Event(MatchEventType.Pass, team, minute, period) with { End = new PitchPosition(endX, 30), Successful = successful };

    private static MatchEvent Carry(TeamSide team, double endX, bool successful = true, int minute = 10) =>
        Event(MatchEventType.Carry, team, minute) with { End = new PitchPosition(endX, 30), Successful = successful };

    private static MatchEvent Shot(TeamSide team, double xg, int minute = 10)
    {
        var detail = TestEvents.Shot(xg, ShotOutcome.Saved);
        var at = team == TeamSide.Home ? 92.0 : 13.0;
        return Event(MatchEventType.Shot, team, minute, startX: at) with { Shot = detail, Successful = true };
    }
}
