using System.Globalization;

namespace Touchline.Domain.Teams.Builder;

// The parts a fan builds a team from (ADR-031). Archetypes and manager styles stand in for raw
// ratings and tendencies, so a fan-built team stays inside the simulator's calibrated ranges.

/// <summary>
/// A type of player. The archetype sets the position and the ratings; the fan sets only the
/// name, number and foot.
/// </summary>
public enum PlayerArchetype
{
    /// <summary>A goalkeeper who stays on the line and makes saves.</summary>
    ShotStopper,

    /// <summary>A goalkeeper who sweeps behind the defence and passes well.</summary>
    SweeperKeeper,

    /// <summary>A centre-back who wins tackles and headers.</summary>
    Stopper,

    /// <summary>A centre-back who starts attacks with passes.</summary>
    BallPlayingDefender,

    /// <summary>A full-back who overlaps and crosses.</summary>
    AttackingFullBack,

    /// <summary>A midfielder who presses and wins the ball back.</summary>
    BallWinner,

    /// <summary>A midfielder who dictates play from deep.</summary>
    DeepPlaymaker,

    /// <summary>A midfielder who covers every blade of grass, both ways.</summary>
    BoxToBox,

    /// <summary>A creative midfielder who plays between the lines.</summary>
    AttackingMidfielder,

    /// <summary>A wide forward who beats players and crosses.</summary>
    Winger,

    /// <summary>A wide forward who cuts inside to shoot.</summary>
    InsideForward,

    /// <summary>A centre-forward who holds the ball up and wins headers.</summary>
    TargetForward,

    /// <summary>A centre-forward who lives in the box and finishes chances.</summary>
    Poacher
}

/// <summary>
/// How a manager wants the team to play. The style sets the team's tendencies (ADR-014).
/// </summary>
public enum ManagerStyle
{
    /// <summary>No strong preference.</summary>
    Balanced,

    /// <summary>Win the ball back high and fast.</summary>
    HighPress,

    /// <summary>Keep the ball and wear the opponent down.</summary>
    Possession,

    /// <summary>Sit deep, then break quickly.</summary>
    Counter,

    /// <summary>Go forward quickly, often long.</summary>
    Direct
}

/// <summary>The outline of a crest.</summary>
public enum CrestShape
{
    /// <summary>A classic heater shield.</summary>
    Shield,

    /// <summary>A roundel.</summary>
    Round,

    /// <summary>A diamond.</summary>
    Diamond,

    /// <summary>A hanging banner.</summary>
    Banner,

    /// <summary>A hexagon.</summary>
    Hexagon
}

/// <summary>
/// The emblem in a crest. The set avoids motifs closely tied to real clubs (ADR-031).
/// </summary>
public enum CrestEmblem
{
    /// <summary>No emblem: initials only.</summary>
    None,

    /// <summary>A five-pointed star.</summary>
    Star,

    /// <summary>A football.</summary>
    Ball,

    /// <summary>An oak tree.</summary>
    Oak,

    /// <summary>An anchor.</summary>
    Anchor,

    /// <summary>A crown.</summary>
    Crown,

    /// <summary>A lightning bolt.</summary>
    Bolt,

    /// <summary>A wave.</summary>
    Wave,

    /// <summary>A tower.</summary>
    Tower,

    /// <summary>A wing.</summary>
    Wing,

    /// <summary>A key.</summary>
    Key,

    /// <summary>A bridge.</summary>
    Bridge
}

/// <summary>
/// Facts about archetypes.
/// </summary>
public static class Archetypes
{
    /// <summary>The position an archetype plays.</summary>
    /// <param name="archetype">The archetype.</param>
    /// <returns>Its position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The archetype isn't defined.</exception>
    public static PlayerPosition PositionOf(PlayerArchetype archetype) => archetype switch
    {
        PlayerArchetype.ShotStopper or PlayerArchetype.SweeperKeeper => PlayerPosition.Goalkeeper,
        PlayerArchetype.Stopper or PlayerArchetype.BallPlayingDefender or PlayerArchetype.AttackingFullBack => PlayerPosition.Defender,
        PlayerArchetype.BallWinner or PlayerArchetype.DeepPlaymaker or PlayerArchetype.BoxToBox or PlayerArchetype.AttackingMidfielder => PlayerPosition.Midfielder,
        PlayerArchetype.Winger or PlayerArchetype.InsideForward or PlayerArchetype.TargetForward or PlayerArchetype.Poacher => PlayerPosition.Forward,
        _ => throw new ArgumentOutOfRangeException(nameof(archetype), archetype, "Unknown archetype.")
    };
}

/// <summary>
/// The rules a fan-built squad must meet (ADR-031).
/// </summary>
public static class SquadRules
{
    /// <summary>The fewest players: a starting eleven and three substitutes.</summary>
    public const int MinPlayers = 14;

    /// <summary>The most players.</summary>
    public const int MaxPlayers = 25;

    /// <summary>The lowest shirt number.</summary>
    public const int MinShirtNumber = 1;

    /// <summary>The highest shirt number.</summary>
    public const int MaxShirtNumber = 99;

    /// <summary>
    /// The fewest players in each position, so the engine's default eleven (one goalkeeper and
    /// a 4-3-3) can always be picked, with a spare goalkeeper.
    /// </summary>
    public static IReadOnlyList<(PlayerPosition Position, int Minimum)> MinimumByPosition { get; } =
    [
        (PlayerPosition.Goalkeeper, 2),
        (PlayerPosition.Defender, 4),
        (PlayerPosition.Midfielder, 3),
        (PlayerPosition.Forward, 3)
    ];

    /// <summary>Every fan team's ID starts with this, so it never collides with a built-in team.</summary>
    public const string FanIdPrefix = "fan-";

    /// <summary>
    /// Finds everything wrong with a squad's size, shirt numbers and positions. Names, IDs and
    /// archetypes are checked elsewhere.
    /// </summary>
    /// <param name="squad">Each player's shirt number and position.</param>
    /// <returns>The problems, in plain words; empty when the squad meets the rules.</returns>
    public static IReadOnlyList<string> Problems(IReadOnlyList<(int ShirtNumber, PlayerPosition Position)> squad)
    {
        ArgumentNullException.ThrowIfNull(squad);
        var problems = new List<string>();

        if (squad.Count is < MinPlayers or > MaxPlayers)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"A squad has {MinPlayers} to {MaxPlayers} players; this one has {squad.Count}."));
        }

        problems.AddRange(squad
            .Where(p => p.ShirtNumber is < MinShirtNumber or > MaxShirtNumber)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"Shirt number {p.ShirtNumber} isn't {MinShirtNumber} to {MaxShirtNumber}.")));
        problems.AddRange(squad
            .GroupBy(p => p.ShirtNumber)
            .Where(g => g.Count() > 1)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"Shirt number {g.Key} is used more than once.")));

        foreach (var (position, minimum) in MinimumByPosition)
        {
            var count = squad.Count(p => p.Position == position);
            if (count < minimum)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"A squad needs at least {minimum} of position {position}; this one has {count}."));
            }
        }

        return problems;
    }
}
