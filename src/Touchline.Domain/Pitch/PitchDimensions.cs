using Touchline.Domain.Matches;

namespace Touchline.Domain.Pitch;

/// <summary>
/// The fixed size of the pitch, in metres (ADR-014). X runs along the length, from the
/// home team's goal line (0) to the away team's goal line (105). Y runs across the width,
/// from the bottom touchline (0) to the top touchline (68), as seen on a TV broadcast
/// with the home team attacking left to right.
/// </summary>
public static class PitchDimensions
{
    /// <summary>Length of the pitch in metres, goal line to goal line.</summary>
    public const double Length = 105;

    /// <summary>Width of the pitch in metres, touchline to touchline.</summary>
    public const double Width = 68;

    /// <summary>How far each penalty area reaches out from its goal line, in metres.</summary>
    public const double PenaltyAreaDepth = 16.5;

    /// <summary>Width of each penalty area, centred on the goal, in metres.</summary>
    public const double PenaltyAreaWidth = 40.32;

    /// <summary>
    /// The centre of the goal a team is attacking.
    /// </summary>
    /// <param name="attacking">The team taking the shot.</param>
    /// <returns>The midpoint of the goal mouth the team attacks.</returns>
    public static PitchPosition AttackingGoal(TeamSide attacking) => attacking switch
    {
        TeamSide.Home => new PitchPosition(Length, Width / 2),
        TeamSide.Away => new PitchPosition(0, Width / 2),
        _ => throw new ArgumentOutOfRangeException(nameof(attacking), attacking, "Unknown team side.")
    };
}
