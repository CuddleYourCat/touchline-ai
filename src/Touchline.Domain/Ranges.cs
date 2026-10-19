namespace Touchline.Domain;

/// <summary>Range checks shared across the domain.</summary>
internal static class Ranges
{
    // Comparisons with NaN are always false, so the finite check comes first.
    public static bool IsBetween(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
