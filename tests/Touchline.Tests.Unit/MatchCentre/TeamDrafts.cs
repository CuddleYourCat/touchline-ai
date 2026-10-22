using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>
/// A valid fan team draft for tests: 16 players, 2 keepers, 5 defenders, 5 midfielders, 4 forwards.
/// </summary>
internal static class TeamDrafts
{
    private static readonly PlayerArchetype[] _archetypes =
    [
        PlayerArchetype.ShotStopper, PlayerArchetype.SweeperKeeper,
        PlayerArchetype.Stopper, PlayerArchetype.Stopper, PlayerArchetype.BallPlayingDefender, PlayerArchetype.AttackingFullBack, PlayerArchetype.AttackingFullBack,
        PlayerArchetype.BallWinner, PlayerArchetype.DeepPlaymaker, PlayerArchetype.BoxToBox, PlayerArchetype.AttackingMidfielder, PlayerArchetype.BoxToBox,
        PlayerArchetype.Winger, PlayerArchetype.InsideForward, PlayerArchetype.Poacher, PlayerArchetype.TargetForward
    ];

    private static readonly string[] _surnames = ["Ashbrook", "Brinmere", "Calford", "Dunholm", "Everwick", "Falgate", "Garstead", "Holvane", "Kelridge", "Lorton", "Morwell", "Norbrook", "Penmere", "Quelford", "Rosgate", "Selwick"];

    public static TeamDraft Valid(string name = "Riverside Rovers") => new(
        name,
        "RIV",
        "Riverside Park",
        new TeamColours("#1e3a8a", "#facc15"),
        new CrestSpec(CrestShape.Shield, CrestEmblem.Oak, "RR", "#1e3a8a", "#facc15"),
        new ManagerSpec("Hal Penmarrow", ManagerStyle.HighPress),
        [.. _archetypes.Select((a, i) => new PlayerDraft($"Arlo {_surnames[i]}", i + 1, a, PreferredFoot.Right))]);
}
