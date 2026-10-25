using System.Globalization;

namespace Touchline.Domain.Teams.Builder;

/// <summary>
/// The players a team names for one match (ADR-032): a starting eleven and its substitutes.
/// </summary>
/// <param name="StartingEleven">Eleven player IDs, exactly one of them a goalkeeper.</param>
/// <param name="Substitutes">Up to <see cref="MatchdaySheetRules.MaxSubstitutes"/> player IDs, none of them starting.</param>
public sealed record MatchdaySheet(IReadOnlyList<string> StartingEleven, IReadOnlyList<string> Substitutes);

/// <summary>
/// Checks a matchday sheet against a squad. Pure logic, shared by the Match Centre (which
/// rejects a bad sheet) and the engine (which plays a good one).
/// </summary>
public static class MatchdaySheetRules
{
    /// <summary>The number of starters.</summary>
    public const int Starters = 11;

    /// <summary>The most substitutes a sheet may name.</summary>
    public const int MaxSubstitutes = 7;

    /// <summary>
    /// Finds everything wrong with a sheet.
    /// </summary>
    /// <param name="sheet">The sheet.</param>
    /// <param name="squad">The team's squad: each player ID and its position.</param>
    /// <param name="unavailable">Players who can't play (suspended or injured).</param>
    /// <returns>The problems, in plain words; empty when the sheet is good.</returns>
    public static IReadOnlyList<string> Problems(
        MatchdaySheet sheet,
        IReadOnlyDictionary<string, PlayerPosition> squad,
        IReadOnlySet<string>? unavailable = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(squad);

        var problems = new List<string>();
        var starters = sheet.StartingEleven ?? [];
        var substitutes = sheet.Substitutes ?? [];

        if (starters.Count != Starters)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"The starting eleven has {starters.Count} players, not {Starters}."));
        }

        if (substitutes.Count > MaxSubstitutes)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"There are {substitutes.Count} substitutes; the most is {MaxSubstitutes}."));
        }

        // A null ID would make the dictionary lookups below throw, so it's a problem like any other.
        var allNamed = starters.Concat(substitutes).ToList();
        if (allNamed.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("The sheet names an empty player.");
        }

        var named = allNamed.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        problems.AddRange(named.Where(id => !squad.ContainsKey(id)).Distinct().Select(id => $"Player '{id}' isn't in the squad."));
        problems.AddRange(named.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => $"Player '{g.Key}' is named more than once."));
        problems.AddRange(named.Where(id => unavailable?.Contains(id) == true).Distinct().Select(id => $"Player '{id}' isn't available for this match."));

        var keepers = starters.Count(id => !string.IsNullOrWhiteSpace(id) && squad.TryGetValue(id, out var position) && position == PlayerPosition.Goalkeeper);
        if (keepers != 1)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"The starting eleven needs exactly one goalkeeper, not {keepers}."));
        }

        return problems;
    }

    /// <summary>
    /// The formation an eleven lines up in, from its outfield positions: defenders, then
    /// midfielders, then forwards, for example <c>4-4-2</c>.
    /// </summary>
    /// <param name="eleven">The starters' positions.</param>
    /// <returns>The formation.</returns>
    public static string Formation(IEnumerable<PlayerPosition> eleven)
    {
        ArgumentNullException.ThrowIfNull(eleven);
        var positions = eleven.ToList();
        int Count(PlayerPosition position) => positions.Count(p => p == position);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Count(PlayerPosition.Defender)}-{Count(PlayerPosition.Midfielder)}-{Count(PlayerPosition.Forward)}");
    }
}
