namespace Touchline.Domain.Viewing;

/// <summary>
/// Language tags in one canonical form, and clamped to the languages the app supports.
/// </summary>
public static class LanguageTags
{
    /// <summary>The language every choice falls back to.</summary>
    public const string Fallback = "en";

    /// <summary>
    /// Puts a language tag in one canonical casing, so <c>ES</c> and <c>es</c> match: the
    /// primary subtag lower-case and a two-letter region upper-case.
    /// </summary>
    /// <param name="language">A tag such as <c>es-mx</c>.</param>
    /// <returns>For example <c>es-MX</c>.</returns>
    public static string Normalise(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var parts = language.Split('-');
        parts[0] = parts[0].ToLowerInvariant();
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 2)
            {
                parts[i] = parts[i].ToUpperInvariant();
            }
        }

        return string.Join('-', parts);
    }

    /// <summary>
    /// The supported language closest to the one asked for: the exact tag, then its base
    /// language (<c>es</c> for <c>es-MX</c>), then <see cref="Fallback"/>. Matching ignores case.
    /// </summary>
    /// <param name="language">The tag asked for, or null.</param>
    /// <param name="supported">The supported tags.</param>
    /// <returns>The chosen tag, in canonical form (<see cref="Normalise"/>).</returns>
    public static string Closest(string? language, IEnumerable<string> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);
        if (string.IsNullOrWhiteSpace(language))
        {
            return Fallback;
        }

        var tags = supported.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(Normalise).ToHashSet(StringComparer.Ordinal);
        var wanted = Normalise(language.Trim());
        if (tags.Contains(wanted))
        {
            return wanted;
        }

        var dash = wanted.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && tags.Contains(wanted[..dash]) ? wanted[..dash] : Fallback;
    }
}
