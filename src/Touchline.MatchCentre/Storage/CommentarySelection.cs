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
    private const string _english = "en";

    /// <summary>Picks the viewer's line from one moment's lines.</summary>
    /// <param name="lines">Every stored line for the moment.</param>
    /// <param name="mode">The viewer's mode.</param>
    /// <param name="language">The viewer's language tag, such as <c>es-MX</c>.</param>
    /// <returns>The line to show, or null when nothing suitable is written yet.</returns>
    public static CommentaryLine? Pick(IEnumerable<CommentaryLine> lines, ViewingMode mode, string language)
    {
        List<CommentaryLine> all = [.. lines];
        var baseLanguage = language.Split('-')[0];
        return Best(all, line => line.Mode == mode && Same(line.Language, language))
            ?? Best(all, line => line.Mode == mode && Same(line.Language, baseLanguage))
            ?? Best(all, line => line.Mode == mode && Same(line.Language, _english))
            ?? Best(all, line => Same(line.Language, _english));
    }

    private static CommentaryLine? Best(List<CommentaryLine> lines, Func<CommentaryLine, bool> matches) =>
        lines.Where(matches).OrderByDescending(line => line.Source == CommentarySource.Model).FirstOrDefault();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
