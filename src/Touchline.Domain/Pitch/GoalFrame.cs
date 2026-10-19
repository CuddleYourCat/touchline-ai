namespace Touchline.Domain.Pitch;

/// <summary>
/// Where a shot crossed (or would have crossed) the goal line, in metres, from the shooter's
/// point of view (ADR-020). It's relative to the frame, not clipped to it, so a miss wide or
/// one sent high over the bar is recorded too.
/// </summary>
public readonly record struct GoalMouthPosition
{
    // Far enough to record any realistic miss, near enough to catch a unit or sign mistake.
    private const double _maximumAcross = 40;
    private const double _maximumUp = 30;

    /// <summary>
    /// Creates a goal-mouth position.
    /// </summary>
    /// <param name="across">Metres from the centre of the goal along the goal line. Positive is the shooter's right.</param>
    /// <param name="up">Metres above the ground.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value isn't finite, the height is negative, or either is implausibly far from the goal.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public GoalMouthPosition(double across, double up)
    {
        Across = Ranges.IsBetween(across, -_maximumAcross, _maximumAcross)
            ? across
            : throw new ArgumentOutOfRangeException(nameof(across), across, $"Across must be within {_maximumAcross} m of the goal's centre.");
        Up = Ranges.IsBetween(up, 0, _maximumUp)
            ? up
            : throw new ArgumentOutOfRangeException(nameof(up), up, $"Up must be between 0 and {_maximumUp} m.");
    }

    /// <summary>Metres from the centre of the goal along the goal line. Positive is the shooter's right.</summary>
    public double Across { get; }

    /// <summary>Metres above the ground.</summary>
    public double Up { get; }
}

/// <summary>A sixth of the goal mouth, from the taker's point of view, used for penalties (ADR-020).</summary>
public enum GoalZone
{
    /// <summary>Bottom left.</summary>
    LowLeft,

    /// <summary>Bottom centre.</summary>
    LowCentre,

    /// <summary>Bottom right.</summary>
    LowRight,

    /// <summary>Top left.</summary>
    HighLeft,

    /// <summary>Top centre.</summary>
    HighCentre,

    /// <summary>Top right.</summary>
    HighRight
}

/// <summary>
/// Which way a goalkeeper went for a penalty, from the <b>taker's</b> point of view, the same
/// frame as <see cref="GoalZone"/>: a keeper who "goes to his right" dives to the taker's left.
/// </summary>
public enum DiveDirection
{
    /// <summary>To the taker's left.</summary>
    Left,

    /// <summary>Stayed in the middle.</summary>
    Stay,

    /// <summary>To the taker's right.</summary>
    Right
}

/// <summary>The goal frame's geometry, and which part of it a goal-mouth position is in.</summary>
public static class GoalFrame
{
    /// <summary>Half the width between the posts, in metres (the goal is 7.32 m wide).</summary>
    public const double HalfWidth = 3.66;

    /// <summary>The height of the crossbar, in metres.</summary>
    public const double Height = 2.44;

    /// <summary>The thickness of a post or the bar, in metres.</summary>
    public const double WoodworkThickness = 0.12;

    private const double _halfThickness = WoodworkThickness / 2;

    /// <summary>True when the ball is inside the frame, clear of the posts and bar.</summary>
    /// <param name="at">Where it crossed the goal line.</param>
    /// <returns>Whether it's between the posts' inner edges and under the bar's.</returns>
    public static bool IsInside(GoalMouthPosition at) =>
        Math.Abs(at.Across) < HalfWidth - _halfThickness && at.Up < Height - _halfThickness;

    /// <summary>True when the ball meets a post or the bar.</summary>
    /// <param name="at">Where it reached the goal line.</param>
    /// <returns>Whether it's within the woodwork's thickness of the frame.</returns>
    public static bool IsOnWoodwork(GoalMouthPosition at) =>
        !IsInside(at) && Math.Abs(at.Across) <= HalfWidth + _halfThickness && at.Up <= Height + _halfThickness;

    /// <summary>The sixth of the goal mouth a position is in, from the shooter's point of view.</summary>
    /// <param name="at">Where the ball crossed the goal line.</param>
    /// <returns>The zone, by thirds across and halves up.</returns>
    public static GoalZone ZoneOf(GoalMouthPosition at)
    {
        var column = at.Across < -HalfWidth / 3 ? 0 : at.Across > HalfWidth / 3 ? 2 : 1;
        var high = at.Up >= Height / 2;
        return (GoalZone)(column + (high ? 3 : 0));
    }
}
