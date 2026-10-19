extern alias simulator;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Simulator;

[TestFixture]
public class ExpectedGoalsModelTests
{
    private static double OpenPlay(double x, double y = 34) =>
        ExpectedGoalsModel.Calculate(new PitchPosition(x, y), TeamSide.Home, ShotSituation.OpenPlay);

    [TestCase(99, 0.50, TestName = "A central shot from 6 m is about 0.50")]
    [TestCase(94, 0.30, TestName = "A central shot from 11 m is about 0.30")]
    [TestCase(85, 0.08, TestName = "A central shot from 20 m is about 0.08")]
    public void Central_shots_follow_the_reference_curve(double x, double expected)
    {
        OpenPlay(x).Should().BeApproximately(expected, 0.02);
    }

    [Test]
    public void A_penalty_is_worth_0_76()
    {
        ExpectedGoalsModel.Calculate(new PitchPosition(94, 34), TeamSide.Home, ShotSituation.Penalty).Should().Be(0.76);
    }

    [Test]
    public void A_wider_angle_is_a_worse_chance_from_the_same_depth()
    {
        OpenPlay(94, 20).Should().BeLessThan(OpenPlay(94, 34));
    }

    [Test]
    public void Both_teams_shoot_at_the_opposite_goal()
    {
        var homeFrom11 = ExpectedGoalsModel.Calculate(new PitchPosition(94, 34), TeamSide.Home, ShotSituation.OpenPlay);
        var awayFrom11 = ExpectedGoalsModel.Calculate(new PitchPosition(11, 34), TeamSide.Away, ShotSituation.OpenPlay);

        awayFrom11.Should().BeApproximately(homeFrom11, 1e-9);
    }

    [Test]
    public void Counters_beat_open_play_which_beats_set_pieces()
    {
        var at = new PitchPosition(90, 34);
        var counter = ExpectedGoalsModel.Calculate(at, TeamSide.Home, ShotSituation.Counter);
        var openPlay = ExpectedGoalsModel.Calculate(at, TeamSide.Home, ShotSituation.OpenPlay);
        var setPiece = ExpectedGoalsModel.Calculate(at, TeamSide.Home, ShotSituation.SetPiece);

        counter.Should().BeGreaterThan(openPlay);
        openPlay.Should().BeGreaterThan(setPiece);
    }
}
