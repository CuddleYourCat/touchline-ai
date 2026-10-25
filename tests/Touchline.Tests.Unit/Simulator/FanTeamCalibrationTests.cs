extern alias simulator;
using FluentAssertions;
using FluentAssertions.Execution;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// The archetype and style tables keep even an extreme fan team inside the calibrated game
/// (ADR-050): goals and xG per match stay inside the bands of <see cref="CalibrationTests"/>.
/// The fan team plays a built-in side, at home and away in turn, over fixed seeds, so the
/// result is exact and repeatable.
/// </summary>
[TestFixture]
public class FanTeamCalibrationTests
{
    // As many as the league calibration plays; each team's season takes about a second.
    private const int _matches = 200;

    private static IEnumerable<TestCaseData> ExtremeTeams()
    {
        yield return new TestCaseData(TestFanTeams.AllOutAttack()).SetArgDisplayNames("all poachers, high press");
        yield return new TestCaseData(TestFanTeams.ParkTheBus()).SetArgDisplayNames("all defenders, counter");
    }

    [TestCaseSource(nameof(ExtremeTeams))]
    public void An_extreme_fan_team_stays_inside_the_goal_and_xG_bands((TeamProfile Team, MatchdaySheet Sheet) fan)
    {
        var opponent = TeamProfileCatalog.LoadEmbedded()["north-bank-fc"];
        var games = Enumerable.Range(3000, _matches)
            .Select(seed => seed % 2 == 0
                ? new MatchSimulator(fan.Team, opponent, seed, new MatchOptions { HomeSheet = fan.Sheet })
                : new MatchSimulator(opponent, fan.Team, seed, new MatchOptions { AwaySheet = fan.Sheet }))
            .Select(match => match.Play().Where(e => e.Type == MatchEventType.Shot).ToList())
            .ToList();

        var goals = games.Average(g => g.Count(e => e.IsGoal));
        var expectedGoals = games.Average(g => g.Sum(e => e.Shot!.ExpectedGoals));

        // The fan side's own share, printed so a tuning run shows which way the team leans.
        var fanGoals = games.Select((g, i) => g.Count(e => e.IsGoal && e.Team == (i % 2 == 0 ? TeamSide.Home : TeamSide.Away))).Average();
        TestContext.Out.WriteLine($"goals {goals:F2}, xG {expectedGoals:F2}, fan team's goals {fanGoals:F2}");

        using var scope = new AssertionScope();
        goals.Should().BeInRange(2.2, 3.4, "goals per match");
        expectedGoals.Should().BeInRange(2.2, 3.4, "xG per match");
        expectedGoals.Should().BeApproximately(goals, 0.4, "total xG should track goals");
    }
}
