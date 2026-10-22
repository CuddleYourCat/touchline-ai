using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// What the team builder offers, with display labels (ADR-031).
/// </summary>
internal static class TeamBuilderCatalog
{
    private static readonly (PlayerArchetype Id, string Label, string Description)[] _archetypes =
    [
        (PlayerArchetype.ShotStopper, "Shot-stopper", "Stays on the line, quick reactions, makes the big saves."),
        (PlayerArchetype.SweeperKeeper, "Sweeper keeper", "Plays high, sweeps behind the defence and starts attacks with passes."),
        (PlayerArchetype.Stopper, "Stopper", "Commanding centre-back who wins tackles and headers."),
        (PlayerArchetype.BallPlayingDefender, "Ball-playing defender", "Centre-back who breaks lines with passes from the back."),
        (PlayerArchetype.AttackingFullBack, "Attacking full-back", "Overlaps down the wing and crosses."),
        (PlayerArchetype.BallWinner, "Ball-winner", "Presses, tackles and wins the ball back in midfield."),
        (PlayerArchetype.DeepPlaymaker, "Deep playmaker", "Sits in front of the defence and sets the tempo."),
        (PlayerArchetype.BoxToBox, "Box-to-box", "Covers the whole pitch, defending and arriving in the box."),
        (PlayerArchetype.AttackingMidfielder, "Attacking midfielder", "Creative number ten who plays between the lines."),
        (PlayerArchetype.Winger, "Winger", "Pace on the outside, beats a full-back and crosses."),
        (PlayerArchetype.InsideForward, "Inside forward", "Starts wide and cuts inside to shoot."),
        (PlayerArchetype.TargetForward, "Target forward", "Holds the ball up and wins headers."),
        (PlayerArchetype.Poacher, "Poacher", "Lives in the box and finishes chances.")
    ];

    private static readonly ManagerStyleOption[] _styles =
    [
        new(ManagerStyle.Balanced, "Balanced", "No strong preference: adapts to the game."),
        new(ManagerStyle.HighPress, "High press", "Wins the ball back high and fast."),
        new(ManagerStyle.Possession, "Possession", "Keeps the ball and wears the opponent down."),
        new(ManagerStyle.Counter, "Counter-attack", "Sits deep, then breaks at speed."),
        new(ManagerStyle.Direct, "Direct", "Goes forward quickly, often long.")
    ];

    /// <summary>Everything the builder offers.</summary>
    public static TeamBuilderOptions Options { get; } = new(
        [.. _archetypes.Select(a => new ArchetypeOption(a.Id, Archetypes.PositionOf(a.Id), a.Label, a.Description))],
        _styles,
        Enum.GetValues<CrestShape>(),
        Enum.GetValues<CrestEmblem>(),
        new SquadRulesView(
            SquadRules.MinPlayers,
            SquadRules.MaxPlayers,
            SquadRules.MinShirtNumber,
            SquadRules.MaxShirtNumber,
            [.. SquadRules.MinimumByPosition.Select(m => new PositionMinimum(m.Position, m.Minimum))],
            MatchdaySheetRules.MaxSubstitutes,
            NameRules.MaxLength));
}
