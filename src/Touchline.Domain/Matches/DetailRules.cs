namespace Touchline.Domain.Matches;

/// <summary>The checks shared by event details. Each returns the value when it's valid.</summary>
internal static class DetailRules
{
    // Faster than the hardest shot ever measured, so only a unit or typing error exceeds it.
    private const double _maximumSpeedKmh = 250;

    public static double Probability(double value, string parameterName, string label) =>
        Ranges.IsBetween(value, 0, 1)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, $"{label} must be a number between 0 and 1.");

    public static double SpeedKmh(double value, string parameterName) =>
        double.IsFinite(value) && value is > 0 and <= _maximumSpeedKmh
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, $"A speed must be above 0 and at most {_maximumSpeedKmh} km/h.");

    // JSON accepts enum values as numbers too, so "bodyPart": 7 would otherwise load as a body part.
    public static T Defined<T>(T value, string parameterName)
        where T : struct, Enum =>
        Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, $"{value} isn't a valid {typeof(T).Name}.");

    public static string Text(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is missing.", parameterName)
            : value;
}
