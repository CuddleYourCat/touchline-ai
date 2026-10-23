using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Picks the one line of a moment's commentary a viewer sees. A pure rule, so it's tested alone.
/// </summary>
/// <remarks>
/// In order: the viewer's mode in their exact language; the same mode in the language's base
/// (<c>es-MX</c> to <c>es</c>); the same mode in English; any mode in English; otherwise none.
/// At each step a model line beats a template line.
/// <para>
/// The "any mode in English" step can return a line whose <see cref="CommentaryLine.Mode"/>
/// differs from the viewer's. The line keeps its own mode, so the UI must check it and label the
/// line (for example "Analyst commentary") rather than present it as the viewer's mode.
/// </para>
/// </remarks>
internal static class CommentarySelection
{
    /// <summary>The worst rank a line in the viewer's own mode can have.</summary>
    public const int WorstInMode = 5;

    private const string _english = "en";

    /// <summary>Picks the viewer's line from one moment's lines.</summary>
    /// <param name="lines">Every stored line for the moment.</param>
    /// <param name="mode">The viewer's mode.</param>
    /// <param name="language">The viewer's language tag, such as <c>es-MX</c>.</param>
    /// <returns>The line to show, or null when nothing suitable is written yet.</returns>
    public static CommentaryLine? Pick(IEnumerable<CommentaryLine> lines, ViewingMode mode, string language) =>
        lines.Select(line => (Line: line, Rank: Rank(line, mode, language)))
            .Where(ranked => ranked.Rank is not null)
            .OrderBy(ranked => ranked.Rank)
            .Select(ranked => ranked.Line)
            .FirstOrDefault();

    /// <summary>
    /// Ranks a line for a viewer, lower being better, by the steps <see cref="Pick"/> follows:
    /// 0 and 1 for the exact language, 2 and 3 for its base, 4 and 5 for English in the viewer's
    /// mode, 6 and 7 for English in another mode. The even rank of each pair is the model line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="mode">The viewer's mode.</param>
    /// <param name="language">The viewer's language tag.</param>
    /// <returns>The rank, or null when the line doesn't suit the viewer at all.</returns>
    public static int? Rank(CommentaryLine line, ViewingMode mode, string language)
    {
        int? step = line.Mode == mode && Same(line.Language, language) ? 0
            : line.Mode == mode && Same(line.Language, language.Split('-')[0]) ? 1
            : line.Mode == mode && Same(line.Language, _english) ? 2
            : Same(line.Language, _english) ? 3
            : null;
        return (step * 2) + (line.Source == CommentarySource.Model ? 0 : 1);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
