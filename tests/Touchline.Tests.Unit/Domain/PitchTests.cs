using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class PitchTests
{
    [TestCase(-0.1, 34)]
    [TestCase(105.1, 34)]
    [TestCase(50, -0.1)]
    [TestCase(50, 68.1)]
    public void A_position_off_the_pitch_is_rejected(double x, double y)
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
}
