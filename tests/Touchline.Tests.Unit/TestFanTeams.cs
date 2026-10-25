using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit;

/// <summary>
/// Fan teams for tests (ADR-050). Every name is made up and plainly a test name, so none can
/// be a real player's.
/// </summary>
internal static class TestFanTeams
{
    /// <summary>A spec with one player per archetype given, numbered from 1 in that order.</summary>
    public static FanTeamSpec Spec(string id, ManagerStyle style, params PlayerArchetype[] archetypes) => new(
        id,
        "Test Rovers",
        "TRV",
        "Test Park",
        new TeamColours("#123456", "#ffffff"),
        new FanManagerSpec("Test Manager", style),
        [.. archetypes.Select((a, i) => new FanPlayerSpec($"{id}-p{i + 1}", $"Test Player {i + 1}", i + 1, i % 3 == 0 ? PreferredFoot.Left : PreferredFoot.Right, a))]);

    /// <summary>
    /// The smallest squad <see cref="SquadRules"/> allows, in a 4-3-3 and a style of the
    /// caller's choosing: two keepers, four defenders, four midfielders and four forwards.
    /// </summary>
    public static FanTeamSpec Standard(string id, ManagerStyle style) => Spec(
        id,
        style,
        PlayerArchetype.SweeperKeeper,
        PlayerArchetype.Stopper,
        PlayerArchetype.BallPlayingDefender,
        PlayerArchetype.AttackingFullBack,
        PlayerArchetype.AttackingFullBack,
        PlayerArchetype.BallWinner,
        PlayerArchetype.DeepPlaymaker,
        PlayerArchetype.BoxToBox,
        PlayerArchetype.Winger,
        PlayerArchetype.Poacher,
        PlayerArchetype.InsideForward,
        PlayerArchetype.ShotStopper,
        PlayerArchetype.AttackingMidfielder,
        PlayerArchetype.TargetForward);

    /// <summary>
    /// High press, and a sheet of one keeper, three defenders, two midfielders and five
    /// poachers: as attacking as the builder allows.
    /// </summary>
    public static (TeamProfile Team, MatchdaySheet Sheet) AllOutAttack()
    {
        var team = FanTeamProfiles.Build(Spec(
            "fan-attack",
            ManagerStyle.HighPress,
            PlayerArchetype.SweeperKeeper,
            PlayerArchetype.Stopper,
            PlayerArchetype.BallPlayingDefender,
            PlayerArchetype.AttackingFullBack,
            PlayerArchetype.AttackingMidfielder,
            PlayerArchetype.BoxToBox,
            PlayerArchetype.Poacher,
            PlayerArchetype.Poacher,
            PlayerArchetype.Poacher,
            PlayerArchetype.Poacher,
            PlayerArchetype.Poacher,
            PlayerArchetype.ShotStopper,
            PlayerArchetype.AttackingFullBack,
            PlayerArchetype.DeepPlaymaker));

        return (team, new MatchdaySheet([.. team.Squad.Take(11).Select(p => p.Id)], [.. team.Squad.Skip(11).Select(p => p.Id)]));
    }

    /// <summary>
    /// Counter-attacking, and a sheet of one keeper, six stoppers and four ball winners: no
    /// forwards at all.
    /// </summary>
    public static (TeamProfile Team, MatchdaySheet Sheet) ParkTheBus()
    {
        var team = FanTeamProfiles.Build(Spec(
            "fan-defend",
            ManagerStyle.Counter,
            PlayerArchetype.ShotStopper,
            PlayerArchetype.Stopper,
            PlayerArchetype.Stopper,
            PlayerArchetype.Stopper,
            PlayerArchetype.Stopper,
            PlayerArchetype.Stopper,
            PlayerArchetype.Stopper,
            PlayerArchetype.BallWinner,
            PlayerArchetype.BallWinner,
            PlayerArchetype.BallWinner,
            PlayerArchetype.BallWinner,
            PlayerArchetype.ShotStopper,
            PlayerArchetype.TargetForward,
            PlayerArchetype.TargetForward,
            PlayerArchetype.Poacher));

        return (team, new MatchdaySheet([.. team.Squad.Take(11).Select(p => p.Id)], []));
    }
}
