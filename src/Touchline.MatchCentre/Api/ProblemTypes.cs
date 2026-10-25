namespace Touchline.MatchCentre.Api;

/// <summary>
/// Stable <c>type</c> URIs for the team endpoints' problems (RFC 9457), so a client tells two
/// responses with the same status apart by <c>type</c> and never by the title, which is prose
/// that may be reworded. The scheme is <c>{Base}/{kebab-case-problem}</c>. The URIs are
/// identifiers, not links: nothing is served at them.
/// </summary>
public static class ProblemTypes
{
    /// <summary>The prefix every problem type shares.</summary>
    public const string Base = "https://touchline.example/problems";

    /// <summary>403: the team is a built-in club, which can't be changed or deleted.</summary>
    public const string ReadOnlyTeam = Base + "/read-only-team";

    /// <summary>403: the edit key is missing or wrong.</summary>
    public const string WrongEditKey = Base + "/wrong-edit-key";

    /// <summary>503: names can't be checked right now (Content Safety is unavailable), so nothing was saved.</summary>
    public const string NameCheckUnavailable = Base + "/name-check-unavailable";

    /// <summary>410: a fan match that couldn't be started, or that its job never picked up (ADR-050).</summary>
    public const string MatchNotStarted = Base + "/match-not-started";
}
