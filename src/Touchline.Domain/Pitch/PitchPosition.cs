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
    /// Creates a position, rejecting any point off the pitch.
    /// </summary>
    /// <param name="x">Metres along the length, 0 to 105.</param>
    /// <param name="y">Metres across the width, 0 to 68.</param>
    /// <exception cref="ArgumentOutOfRangeException">The point is off the pitch or not a number.</exception>
    // [JsonConstructor]: without it, System.Text.Json builds a struct through its implicit
    // parameterless constructor and every deserialized position becomes (0, 0).
    [JsonConstructor]
    public PitchPosition(double x, double y)
    {
        X = OnPitch(x, PitchDimensions.Length, nameof(x));
        Y = OnPitch(y, PitchDimensions.Width, nameof(y));
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

    private static double OnPitch(double value, double max, string name)
    {
        // Comparisons with NaN are always false, so range checks alone would let it through.
        if (!double.IsFinite(value) || value < 0 || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Must be a number between 0 and {max} metres.");
        }

        return value;
    }
}
