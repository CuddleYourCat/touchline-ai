using FluentAssertions;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class GoalFrameTests
{
    [TestCase(0, 1, true, false, TestName = "The middle of the goal is inside")]
    [TestCase(3.59, 1, true, false, TestName = "Just inside the post is inside")]
    [TestCase(-3.6, 1, false, true, TestName = "The inner edge of the left post is woodwork")]
    [TestCase(3.72, 2.5, false, true, TestName = "The outer corner of the frame is woodwork")]
    [TestCase(0, 2.38, false, true, TestName = "The underside of the bar is woodwork")]
    [TestCase(3.73, 1, false, false, TestName = "Just outside the post is a miss")]
    [TestCase(0, 2.51, false, false, TestName = "Just over the bar is a miss")]
    [TestCase(0, 7.5, false, false, TestName = "A rugby kick is a miss")]
    public void A_position_is_inside_on_the_woodwork_or_neither(double across, double up, bool inside, bool woodwork)
    {
        var at = new GoalMouthPosition(across, up);

        GoalFrame.IsInside(at).Should().Be(inside);
        GoalFrame.IsOnWoodwork(at).Should().Be(woodwork);
    }

    [TestCase(-2, 0.5, GoalZone.LowLeft)]
    [TestCase(0, 0.2, GoalZone.LowCentre)]
    [TestCase(0, 2, GoalZone.HighCentre)]
    [TestCase(2, 1.3, GoalZone.HighRight)]
    public void A_position_falls_in_one_sixth_of_the_goal(double across, double up, GoalZone expected)
    {
        GoalFrame.ZoneOf(new GoalMouthPosition(across, up)).Should().Be(expected);
    }

    [TestCase(double.NaN, 1)]
    [TestCase(0, -0.1)]
    [TestCase(0, 31)]
    [TestCase(45, 1)]
    public void An_impossible_goal_mouth_position_is_rejected(double across, double up)
    {
        var create = () => new GoalMouthPosition(across, up);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
