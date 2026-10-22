using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Touchline.Agents;

/// <summary>
/// Checks generated text for personal names that aren't in the match (ADR-044), so a model
/// can't slip a real player into a fictional match. A pure function: no I/O, no model.
/// </summary>
/// <remarks>
/// <para><b>The heuristic.</b></para>
/// <list type="number">
/// <item>Find runs of capitalised words joined by single spaces, allowing lower-case name
/// particles between them (<c>van</c>, <c>de</c>, <c>da</c>...). A possessive (<c>'s</c>),
/// punctuation or a lower-case word ends a run. ALL-CAPS words (<c>GOAL</c>, <c>VAR</c>) are
/// shouting, not names, and are skipped.</item>
/// <item>Known phrases are every contiguous run of words from a roster name or another known
/// name (team names, short names, venues, referees), so <c>Okafor</c>, <c>Tom Okafor</c> and
/// <c>North Bank</c> all match. Matching ignores case and accents.</item>
/// <item>A run passes if it splits, left to right, into known phrases and common football or
/// sentence words (<c>Goal</c>, <c>Referee</c>, <c>The</c>...).</item>
/// <item>At the start of a sentence, a first word is dropped only when it's a common word or
/// a common commentary opener (<c>Brilliant</c>, <c>What</c>, <c>Superb</c>, <c>Increíble</c>...),
/// so <c>Brilliant Okafor!</c> passes. Any other unknown sentence-opening word is treated as a
/// possible name, so <c>Saka! Through on goal.</c> is flagged.</item>
/// <item>Whatever doesn't pass is returned, as written.</item>
/// </list>
/// <para><b>Limits.</b> It is a name detector, not a fact checker. A real surname that is also
/// a listed opener or common word (<c>Great</c>, <c>Free</c>) isn't flagged when it opens a
/// sentence, and an unlisted ordinary word opening a sentence (<c>Clinical finish.</c>) is
/// flagged: a false positive costs one retry. It doesn't catch a first
/// name of one player joined to another's surname, names written in lower case, or a real
/// person whose name is all common words. Capitalised non-names mid-sentence that aren't in
/// the word list (a nationality such as <c>Spanish</c>, a city) are flagged: a false positive
/// costs one retry and, at worst, the template line, which ADR-044 accepts.</para>
/// </remarks>
public static partial class RosterGuardrail
{
    private static readonly HashSet<string> _particles = new(StringComparer.Ordinal)
    {
        "van", "von", "de", "der", "den", "da", "di", "del", "della", "dos", "du", "le", "la", "bin", "al", "el", "ter", "ten"
    };

    // Words that are capitalised for reasons other than being a name: football terms, sentence
    // openers, days and months, and the same in the localised languages (ADR-045). Normalised.
    private static readonly HashSet<string> _commonWords = new(
        [
            // Football.
            "goal", "goals", "penalty", "penalties", "corner", "free", "kick", "free-kick", "kick-off", "offside",
            "var", "referee", "ref", "keeper", "goalkeeper", "captain", "manager", "coach", "striker", "defender",
            "midfielder", "winger", "substitute", "sub", "half", "time", "half-time", "full", "full-time", "first",
            "second", "extra", "stoppage", "yellow", "red", "card", "box", "area", "league", "cup", "final", "derby",
            "stadium", "park", "ground", "home", "away", "fc", "afc", "united", "city", "athletic", "rovers", "town",
            "match", "minute", "minutes", "save", "shot", "header", "cross", "foul", "handball", "woodwork", "post",
            "bar", "crossbar", "net", "synthetic", "live", "replay", "analysis", "casual", "analyst",
            // Sentence and pronoun words.
            "the", "a", "an", "and", "but", "or", "so", "what", "that", "this", "these", "those", "it", "its", "he",
            "she", "they", "his", "her", "their", "we", "our", "i", "you", "your", "in", "on", "at", "from", "with",
            "after", "before", "now", "then", "still", "just", "oh", "yes", "no", "not", "here", "there", "well",
            "who", "why", "how", "when", "where", "if", "as", "for", "to", "of", "by", "into", "over", "under",
            "all", "both", "one", "two", "three", "is", "was", "what's", "it's", "that's", "he's", "she's", "they're",
            // Days and months.
            "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "january", "february",
            "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
            // Spanish and French.
            "gol", "penalti", "penal", "arbitro", "portero", "el", "la", "los", "las", "un",
            "una", "y", "que", "pero", "arbitre", "gardien", "le", "les", "une", "et", "quel", "quelle",
            "mais", "il", "elle", "ils"
        ],
        StringComparer.Ordinal);

    // Words that often open a commentary sentence without being a name: exclamations and
    // adjectives, and the same in the localised languages. Only consulted for a sentence's
    // first word. Normalised.
    private static readonly HashSet<string> _openers = new(
        [
            "brilliant", "superb", "great", "lovely", "excellent", "wonderful", "unbelievable", "incredible",
            "magnificent", "stunning", "fantastic", "sublime", "amazing", "terrific", "outstanding", "through",
            "back", "pass", "another", "chance", "close", "wide", "wow", "unlucky", "clever",
            // Spanish.
            "increible", "magnifico", "magnifica", "excelente", "genial", "estupendo", "estupenda", "precioso",
            "preciosa", "brillante", "fantastico", "fantastica", "impresionante", "maravilloso", "maravillosa",
            "golazo",
            // French.
            "incroyable", "magnifique", "superbe", "brillant", "formidable", "fantastique", "merveilleux",
            "splendide"
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Returns the capitalised personal names in <paramref name="text"/> that aren't on the
    /// match's rosters or among the other known names.
    /// </summary>
    /// <param name="text">The generated text.</param>
    /// <param name="rosterNames">Every player's full name, from both teams' rosters (fan-built teams included).</param>
    /// <param name="otherKnownNames">Other names that may appear: team names and short names, venues, the referee.</param>
    /// <returns>The unknown names, as written, first appearance first, without duplicates. Empty when the text is clean.</returns>
    public static IReadOnlyList<string> FindUnknownNames(
        string text,
        IEnumerable<string> rosterNames,
        IEnumerable<string>? otherKnownNames = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rosterNames);

        var known = KnownPhrases(rosterNames.Concat(otherKnownNames ?? []));
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var run in Runs(text))
        {
            var words = run.Words;
            int start = 0;
            var first = Normalise(words[0]);
            if (run.AtSentenceStart
                && !known.Contains(first)
                && (_commonWords.Contains(first) || _openers.Contains(first)))
            {
                start = 1;
            }

            if (start < words.Count && !SplitsIntoKnown(words, start, known))
            {
                var name = string.Join(' ', words.Skip(start));
                if (seen.Add(Normalise(name)))
                {
                    unknown.Add(name);
                }
            }
        }

        return unknown;
    }

    private static HashSet<string> KnownPhrases(IEnumerable<string> names)
    {
        var phrases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var words = Normalise(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int from = 0; from < words.Length; from++)
            {
                for (int to = from + 1; to <= words.Length; to++)
                {
                    phrases.Add(string.Join(' ', words[from..to]));
                }
            }
        }

        return phrases;
    }

    private static bool SplitsIntoKnown(List<string> words, int start, HashSet<string> known)
    {
        int i = start;
        while (i < words.Count)
        {
            int next = -1;
            for (int end = words.Count; end > i; end--)
            {
                var phrase = Normalise(string.Join(' ', words.Skip(i).Take(end - i)));
                if (known.Contains(phrase) || (end - i == 1 && _commonWords.Contains(phrase)))
                {
                    next = end;
                    break;
                }
            }

            if (next < 0)
            {
                return false;
            }

            i = next;
        }

        return true;
    }

    private static IEnumerable<Run> Runs(string text)
    {
        Run? current = null;
        var pendingParticles = new List<string>();
        int previousEnd = -1;

        foreach (Match match in WordPattern().Matches(text))
        {
            var raw = match.Value;
            bool possessive = raw.EndsWith("'s", StringComparison.Ordinal) || raw.EndsWith("’s", StringComparison.Ordinal);
            var word = (possessive ? raw[..^2] : raw).Trim('\'', '’', '-');
            bool joined = current is not null && previousEnd >= 0 && text.AsSpan(previousEnd, match.Index - previousEnd) is " ";
            previousEnd = match.Index + match.Length;

            if (word.Length == 0)
            {
                continue;
            }

            if (IsCapitalised(word))
            {
                if (current is not null && joined)
                {
                    current.Words.AddRange(pendingParticles);
                    current.Words.Add(word);
                }
                else
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = new Run(IsSentenceStart(text, match.Index), [word]);
                }

                pendingParticles.Clear();
                if (possessive)
                {
                    yield return current;
                    current = null;
                }
            }
            else if (current is not null && joined && !possessive && _particles.Contains(word))
            {
                pendingParticles.Add(word);
            }
            else
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = null;
                pendingParticles.Clear();
            }
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    private static bool IsCapitalised(string word) =>
        char.IsUpper(word[0]) && !(word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)));

    private static bool IsSentenceStart(string text, int index)
    {
        int i = index - 1;
        while (i >= 0 && (text[i] is ' ' or '\t' or '"' or '\'' or '(' or '[' or '“' or '‘' or '«' or '¿' or '¡'))
        {
            i--;
        }

        return i < 0 || text[i] is '.' or '!' or '?' or ':' or ';' or '\n' or '\r' or '…' or '—';
    }

    private static string Normalise(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c == '’' ? '\'' : char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\p{L}[\p{L}\p{M}'’\-]*")]
    private static partial Regex WordPattern();

    private sealed record Run(bool AtSentenceStart, List<string> Words);
}
