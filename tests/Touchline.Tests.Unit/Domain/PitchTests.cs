using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class PitchTests
{
    [TestCase(-5.1, 34)]
    [TestCase(110.1, 34)]
    [TestCase(50, -5.1)]
    [TestCase(50, 73.1)]
    public void A_position_beyond_the_margin_is_rejected(double x, double y)
    {
        var create = () => new PitchPosition(x, y);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(double.NaN, 34)]
    [TestCase(50, double.NaN)]
    [TestCase(double.PositiveInfinity, 34)]
    public void A_position_that_is_not_a_number_is_rejected(double x, double y)
    {
        var create = () => new PitchPosition(x, y);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    [SetCulture("de-DE")]
    public void A_position_always_formats_with_a_full_stop()
    {
        new PitchPosition(94.5, 30).ToString().Should().Be("(94.5, 30)");
    }

    [TestCase(10, 34, TeamSide.Home, true, TestName = "The home penalty area is near X = 0")]
    [TestCase(95, 34, TeamSide.Away, true, TestName = "The away penalty area is near X = 105")]
    [TestCase(95, 34, TeamSide.Home, false, TestName = "The opposition's area isn't the team's own")]
    [TestCase(16.5, 54.16, TeamSide.Home, true, TestName = "The area's edges are inside it")]
    [TestCase(16.6, 34, TeamSide.Home, false, TestName = "Just beyond the area's depth is outside it")]
    [TestCase(10, 13.8, TeamSide.Home, false, TestName = "Wide of the area is outside it")]
    public void Penalty_area_membership(double x, double y, TeamSide defending, bool expected)
    {
        PitchDimensions.IsInPenaltyArea(new PitchPosition(x, y), defending).Should().Be(expected);
    }

    [Test]
    public void The_corners_of_the_pitch_are_valid_positions()
    {
        var create = () => (new PitchPosition(0, 0), new PitchPosition(105, 68));

        create.Should().NotThrow();
    }

    [Test]
    public void Distance_is_measured_in_metres()
    {
        var penaltySpot = new PitchPosition(94, 34);

        penaltySpot.DistanceToGoal(TeamSide.Home).Should().BeApproximately(11, 0.001);
    }

    [Test]
    public void Each_team_attacks_the_opposite_goal()
    {
        PitchDimensions.AttackingGoal(TeamSide.Home).Should().Be(new PitchPosition(105, 34));
        PitchDimensions.AttackingGoal(TeamSide.Away).Should().Be(new PitchPosition(0, 34));
    }

    [Test]
    public void The_same_spot_is_attacking_for_one_team_and_defensive_for_the_other()
    {
        var nearAwayGoal = new PitchPosition(100, 34);

        PitchZone.Locate(nearAwayGoal, TeamSide.Home).Should().Be(new PitchZone(Third.Attacking, Channel.Centre));
        PitchZone.Locate(nearAwayGoal, TeamSide.Away).Should().Be(new PitchZone(Third.Defensive, Channel.Centre));
    }

    [Test]
    public void Channels_are_named_from_the_attacking_team_s_point_of_view()
    {
        var nearTopTouchline = new PitchPosition(52.5, 65);

        PitchZone.Locate(nearTopTouchline, TeamSide.Home).Channel.Should().Be(Channel.Left);
        PitchZone.Locate(nearTopTouchline, TeamSide.Away).Channel.Should().Be(Channel.Right);
    }

    // Added in the lines and key moments slice (ADR-022)

    [TestCase(105.11, 34, true, TestName = "A ball touching the goal line from beyond is in play")]
    [TestCase(105.12, 34, false, TestName = "A ball wholly over the goal line is out")]
    [TestCase(-0.11, 34, true, TestName = "A ball touching the home goal line is in play")]
    [TestCase(50, 68.11, true, TestName = "A ball touching the touchline is in play")]
    [TestCase(50, -0.12, false, TestName = "A ball wholly over the touchline is out")]
    [TestCase(107.5, 34, false, TestName = "A ball in the back of the net is out")]
    public void In_play_means_any_part_of_the_ball_is_on_or_inside_the_lines(double x, double y, bool inPlay)
    {
        PitchDimensions.IsInPlay(new PitchPosition(x, y)).Should().Be(inPlay);
    }

    [TestCase(105.13, TeamSide.Home, 0.02)]
    [TestCase(105.11, TeamSide.Home, 0)]
    [TestCase(105.0, TeamSide.Home, -0.11)]
    [TestCase(-0.14, TeamSide.Away, 0.03)]
    public void Over_the_goal_line_is_measured_from_the_ball_s_trailing_edge(double x, TeamSide attacking, double expected)
    {
        PitchDimensions.OverTheGoalLineBy(new PitchPosition(x, 34), attacking).Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void Positions_up_to_five_metres_beyond_the_lines_are_valid()
    {
        var create = () => (new PitchPosition(-5, -5), new PitchPosition(110, 73));

        create.Should().NotThrow("the net and the run-off must be recordable");
    }

    [Test]
    public void The_penalty_area_stops_at_the_goal_line()
    {
        PitchDimensions.IsInPenaltyArea(new PitchPosition(105, 34), TeamSide.Away).Should().BeTrue("the line is part of the area");
        PitchDimensions.IsInPenaltyArea(new PitchPosition(106, 34), TeamSide.Away).Should().BeFalse("the net isn't");
    }
}
