using System.Globalization;
using System.Text.Json.Serialization;
using Touchline.Domain.Matches;

namespace Touchline.Domain.Pitch;

/// <summary>
/// A point on the pitch in metres. See <see cref="PitchDimensions"/> for the axes.
/// </summary>
public readonly record struct PitchPosition
{
    /// <summary>
    /// Creates a position on the pitch or within <see cref="PitchDimensions.Margin"/> of it
    /// (ADR-022), so a ball in the net or over the touchline can be recorded. Whether it's in
    /// play is <see cref="PitchDimensions.IsInPlay"/>'s decision, not this type's.
    /// </summary>
    /// <param name="x">Metres along the length: 0 to 105 on the pitch, −5 to 110 in all.</param>
    /// <param name="y">Metres across the width: 0 to 68 on the pitch, −5 to 73 in all.</param>
    /// <exception cref="ArgumentOutOfRangeException">The point is beyond the margin or not a number.</exception>
    // [JsonConstructor]: without it, System.Text.Json builds a struct through its implicit
    // parameterless constructor and every deserialized position becomes (0, 0).
    [JsonConstructor]
    public PitchPosition(double x, double y)
    {
        X = WithinMargin(x, PitchDimensions.Length, nameof(x));
        Y = WithinMargin(y, PitchDimensions.Width, nameof(y));
    }

    /// <summary>Metres along the length of the pitch.</summary>
    public double X { get; }

    /// <summary>Metres across the width of the pitch.</summary>
    public double Y { get; }

    /// <summary>
    /// Straight-line distance to another position, in metres.
    /// </summary>
    /// <param name="other">The other position.</param>
    /// <returns>The distance in metres.</returns>
    public double DistanceTo(PitchPosition other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Distance from this position to the centre of the goal a team is attacking.
    /// </summary>
    /// <param name="attacking">The team in possession.</param>
    /// <returns>The distance in metres.</returns>
    public double DistanceToGoal(TeamSide attacking) => DistanceTo(PitchDimensions.AttackingGoal(attacking));

    /// <summary>The position as <c>(x, y)</c>, always with a full stop as the decimal separator.</summary>
    /// <returns>The formatted position.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.#}, {Y:0.#})");

    private static double WithinMargin(double value, double max, string name) =>
        Ranges.IsBetween(value, -PitchDimensions.Margin, max + PitchDimensions.Margin)
            ? value
            : throw new ArgumentOutOfRangeException(
                name, value, $"Must be a number between {-PitchDimensions.Margin} and {max + PitchDimensions.Margin} metres.");
}
