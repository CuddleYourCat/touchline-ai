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
    /// How far beyond every line a position may be recorded, in metres (ADR-022): deep
    /// enough for the back of the net and the run-off around the pitch.
    /// </summary>
    public const double Margin = 5;

    /// <summary>The radius of a size 5 ball, in metres.</summary>
    public const double BallRadius = 0.11;

    /// <summary>
    /// Whether the ball is in play, by the Laws: it's out only when the <b>whole</b> ball is
    /// over a line, so its centre must be more than <see cref="BallRadius"/> beyond (ADR-022).
    /// </summary>
    /// <param name="at">Where the centre of the ball is.</param>
    /// <returns>True while any part of the ball is on or inside the lines.</returns>
    public static bool IsInPlay(PitchPosition at) =>
        at.X >= -BallRadius && at.X <= Length + BallRadius && at.Y >= -BallRadius && at.Y <= Width + BallRadius;

    /// <summary>
    /// How far the whole ball is over the goal line a team attacks, in metres: positive for a
    /// goal, negative when it hasn't fully crossed. Goal-line technology's question (ADR-022).
    /// </summary>
    /// <param name="at">Where the centre of the ball is.</param>
    /// <param name="attacking">The team attacking that goal.</param>
    /// <returns>Metres between the ball's trailing edge and the line.</returns>
    public static double OverTheGoalLineBy(PitchPosition at, TeamSide attacking) =>
        (attacking == TeamSide.Home ? at.X - Length : -at.X) - BallRadius;

    /// <summary>
    /// Whether a position is inside the penalty area a team defends, edges and lines included.
    /// The one definition the simulator (awarding penalties) and the key-moment detector
    /// (flagging them) both use, so they can never disagree.
    /// </summary>
    /// <param name="at">The position to test.</param>
    /// <param name="defending">The team whose own penalty area is meant.</param>
    /// <returns>True when the position is inside that area, and not beyond the goal line.</returns>
    public static bool IsInPenaltyArea(PitchPosition at, TeamSide defending)
    {
        var fromOwnGoalLine = defending == TeamSide.Home ? at.X : Length - at.X;
        return Math.Abs(at.Y - Width / 2) <= PenaltyAreaWidth / 2 && fromOwnGoalLine is >= -BallRadius and <= PenaltyAreaDepth;
    }

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
