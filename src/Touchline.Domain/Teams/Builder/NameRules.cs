using System.Text.RegularExpressions;

namespace Touchline.Domain.Teams.Builder;

/// <summary>
/// The format every fan-typed name must meet (ADR-031): letters, spaces, hyphens, apostrophes
/// and full stops, at most <see cref="MaxLength"/> characters and <see cref="MaxWords"/> words.
/// Names reach the agents' prompts later, so the rule also keeps instructions, markup and
/// punctuation-heavy payloads out of them. Whether a name is real or abusive is checked
/// separately, by the name guard.
/// </summary>
public static partial class NameRules
{
    /// <summary>The shortest name.</summary>
    public const int MinLength = 2;

    /// <summary>The longest name.</summary>
    public const int MaxLength = 30;

    /// <summary>The most words in a name.</summary>
    public const int MaxWords = 4;

    /// <summary>
    /// Finds what's wrong with a name's format.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The problem in plain words, or null when the format is fine.</returns>
    public static string? FormatProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A name is required.";
        }

        if (name.Length is < MinLength or > MaxLength)
        {
            return $"A name is {MinLength} to {MaxLength} characters.";
        }

        if (name != name.Trim() || name.Contains("  ", StringComparison.Ordinal))
        {
            return "A name has no leading, trailing or double spaces.";
        }

        if (name.Split(' ').Length > MaxWords)
        {
            return $"A name has at most {MaxWords} words.";
        }

        if (!Allowed().IsMatch(name))
        {
            return "A name starts with a letter and uses only letters, spaces, hyphens, apostrophes and full stops.";
        }

        // Latin letters mixed with Cyrillic or Greek ones are how look-alike spellings dodge the blocklist.
        return name.Any(IsLatinLetter) && name.Any(IsCyrillicOrGreekLetter)
            ? "A name uses one alphabet: don't mix Latin letters with Cyrillic or Greek ones."
            : null;
    }

    private static bool IsLatinLetter(char c) => char.IsLetter(c) && (c < 'ɐ' || c is >= 'Ḁ' and <= 'ỿ');

    private static bool IsCyrillicOrGreekLetter(char c) => char.IsLetter(c) && c is >= 'Ͱ' and <= 'ԯ';

    // \p{L} letters and \p{M} accents, so names like Zoë or Łukasz are fine.
    [GeneratedRegex(@"^\p{L}[\p{L}\p{M} '’.\-]*$")]
    private static partial Regex Allowed();
}
