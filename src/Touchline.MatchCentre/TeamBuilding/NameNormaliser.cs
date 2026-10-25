using System.Globalization;
using System.Text;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Reduces a name to a plain, lower-case Latin form before it is checked against the blocklist
/// (ADR-049), so the blocklist's exact terms can't be dodged by accents, look-alike letters from
/// other scripts, or punctuation slipped into a word.
/// </summary>
/// <remarks>
/// The steps are deterministic: compatibility decomposition (NFKD) and removal of combining marks,
/// folding of common Cyrillic and Greek look-alikes to Latin, lower-casing, removal of
/// apostrophes, full stops and hyphens, and collapsing of white space. Blocklist terms are loaded
/// in the same form (see <c>infra/load-name-blocklist.ps1</c>).
/// </remarks>
internal static class NameNormaliser
{
    private static readonly Dictionary<char, char> _confusables = BuildConfusables();

    /// <summary>Normalises a name.</summary>
    /// <param name="name">The name as typed.</param>
    /// <returns>The normalised text, which may be empty.</returns>
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormKD))
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark:
                    continue;
            }

            if (c is '\'' or '’' or '.' or '-')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                }

                continue;
            }

            text.Append(char.ToLowerInvariant(_confusables.GetValueOrDefault(c, c)));
        }

        return text.ToString().TrimEnd();
    }

    private static Dictionary<char, char> BuildConfusables()
    {
        var map = new Dictionary<char, char>();
        void Add(string from, string to)
        {
            for (var i = 0; i < from.Length; i++)
            {
                map[from[i]] = to[i];
            }
        }

        // Cyrillic look-alikes, lower then upper case.
        Add("аеорсухіјѕкмнтв", "aeopcyxijskmhtb");
        Add("АЕОРСУХІЈЅКМНТВ", "AEOPCYXIJSKMHTB");

        // Greek look-alikes, lower then upper case.
        Add("αορεικντυχ", "aopeikvtux");
        Add("ΑΒΕΖΗΙΚΜΝΟΡΤΥΧ", "ABEZHIKMNOPTYX");

        // Latin letters that NFKD does not decompose.
        Add("øłđðı", "olddi");
        Add("ØŁĐÐ", "OLDD");
        return map;
    }
}
