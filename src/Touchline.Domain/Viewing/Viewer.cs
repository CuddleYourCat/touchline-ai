using System.Text.RegularExpressions;

namespace Touchline.Domain.Viewing;

/// <summary>
/// How a viewer wants the match explained (ADR-027).
/// </summary>
public enum ViewingMode
{
    /// <summary>Plain words, the story of the match, little jargon.</summary>
    Casual,

    /// <summary>Numbers and tactics: xG, pressing, momentum and why things happened.</summary>
    Analyst
}

/// <summary>
/// Who is watching, and how they want the match told. The service stores no profile: the
/// browser keeps it and sends it with each request (ADR-027), so every value is untrusted
/// until <see cref="Create"/> has checked it.
/// </summary>
public sealed partial record Viewer
{
    /// <summary>The longest favourite-metric name accepted.</summary>
    public const int MaxMetricLength = 40;

    private Viewer(ViewingMode mode, string language, string? favouriteTeamId, string? focusPlayerId, string? favouriteMetric)
    {
        Mode = mode;
        Language = language;
        FavouriteTeamId = favouriteTeamId;
        FocusPlayerId = focusPlayerId;
        FavouriteMetric = favouriteMetric;
    }

    /// <summary>A casual viewer reading English, with no favourites.</summary>
    public static Viewer Default { get; } = new(ViewingMode.Casual, "en", null, null, null);

    /// <summary>The viewing mode.</summary>
    public ViewingMode Mode { get; }

    /// <summary>The language to write in, as a BCP 47 tag such as <c>en</c> or <c>es-MX</c>.</summary>
    public string Language { get; }

    /// <summary>The viewer's club, or null.</summary>
    public string? FavouriteTeamId { get; }

    /// <summary>The player the viewer is following (player-focus mode), or null.</summary>
    public string? FocusPlayerId { get; }

    /// <summary>The one metric the viewer most wants to see, such as <c>xG</c>, or null.</summary>
    public string? FavouriteMetric { get; }

    /// <summary>
    /// Checks a viewer's settings and builds the viewer.
    /// </summary>
    /// <param name="mode">The viewing mode.</param>
    /// <param name="language">A language tag such as <c>en</c> or <c>pt-BR</c>; null for English.</param>
    /// <param name="favouriteTeamId">A club ID, which <paramref name="isKnownTeam"/> must recognise.</param>
    /// <param name="focusPlayerId">A player ID, which <paramref name="isKnownPlayer"/> must recognise.</param>
    /// <param name="favouriteMetric">A metric name: letters and digits only.</param>
    /// <param name="isKnownTeam">Whether a club ID exists in the team profiles.</param>
    /// <param name="isKnownPlayer">Whether a player ID exists in the team profiles.</param>
    /// <returns>The viewer, or the problems found, keyed by setting name.</returns>
    public static (Viewer? Viewer, IReadOnlyDictionary<string, string[]> Problems) Create(
        ViewingMode mode,
        string? language,
        string? favouriteTeamId,
        string? focusPlayerId,
        string? favouriteMetric,
        Func<string, bool> isKnownTeam,
        Func<string, bool> isKnownPlayer)
    {
        ArgumentNullException.ThrowIfNull(isKnownTeam);
        ArgumentNullException.ThrowIfNull(isKnownPlayer);

        var problems = new Dictionary<string, string[]>();
        language = string.IsNullOrWhiteSpace(language) ? "en" : language;

        if (!Enum.IsDefined(mode))
        {
            problems["mode"] = ["The mode must be Casual or Analyst."];
        }

        if (!LanguageTag().IsMatch(language))
        {
            problems["lang"] = ["The language must be a tag such as en or es-MX."];
        }

        if (favouriteTeamId is not null && !isKnownTeam(favouriteTeamId))
        {
            problems["team"] = ["That club isn't one of the team profiles."];
        }

        if (focusPlayerId is not null && !isKnownPlayer(focusPlayerId))
        {
            problems["player"] = ["That player isn't in any squad."];
        }

        if (favouriteMetric is not null && !MetricName().IsMatch(favouriteMetric))
        {
            problems["metric"] = [$"A metric name is 1 to {MaxMetricLength} letters or digits."];
        }

        return problems.Count > 0
            ? (null, problems)
            : (new Viewer(mode, language, favouriteTeamId, focusPlayerId, favouriteMetric), problems);
    }

    [GeneratedRegex("^[a-z]{2,3}(-[A-Z]{2})?$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex("^[A-Za-z0-9]{1,40}$")]
    private static partial Regex MetricName();
}
