using System.Text.RegularExpressions;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// The format rule for each kind of name (ADR-031): <see cref="NameRules"/> for names, and one to
/// four capital letters for an abbreviation.
/// </summary>
internal static partial class NameFormat
{
    /// <summary>Finds what's wrong with a name's format.</summary>
    /// <param name="kind">What it's a name for.</param>
    /// <param name="name">The name.</param>
    /// <returns>The problem in plain words, or null when the format is fine.</returns>
    public static string? Problem(NameKind kind, string? name) =>
        kind == NameKind.Abbreviation
            ? name is not null && Abbreviation().IsMatch(name) ? null : "An abbreviation is 1 to 4 capital letters."
            : NameRules.FormatProblem(name);

    [GeneratedRegex("^[A-Z]{1,4}$")]
    private static partial Regex Abbreviation();
}
