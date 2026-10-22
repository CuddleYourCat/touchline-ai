namespace Touchline.MatchCentre.Api;

/// <summary>
/// Reads an enum from a query value by name, ignoring case. Numbers are refused, unlike
/// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>, so <c>?status=7</c> can't name a
/// value that doesn't exist.
/// </summary>
internal static class EnumNames
{
    /// <summary>Parses a value by name.</summary>
    /// <typeparam name="T">The enum.</typeparam>
    /// <param name="value">The text, or null.</param>
    /// <returns>The value, or null when the text is null or names no member.</returns>
    public static T? Parse<T>(string? value) where T : struct, Enum =>
        value is not null && Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<T>(name)
            : null;
}
