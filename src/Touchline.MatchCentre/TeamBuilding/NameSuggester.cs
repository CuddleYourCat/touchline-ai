using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Suggests fictional names by combining made-up parts (ADR-031). The parts are invented or
/// generic, so a suggestion is very unlikely to be a real club or player; suggestions still
/// go through the name guard when the team is saved.
/// </summary>
/// <param name="random">The source of randomness: shared in production, seeded in tests.</param>
internal sealed class NameSuggester(Random random)
{
    /// <summary>The most suggestions in one request.</summary>
    public const int MaxCount = 10;

    private static readonly string[] _places = ["Ashwold", "Brackwater", "Corrinford", "Dunmere", "Elmsgrove", "Fenwold", "Gallowmere", "Harrowmere", "Ivyholme", "Kestrelmouth", "Larkmoor", "Marrowby", "Northwold", "Oakhaven", "Penmarrow", "Quarrymere", "Redmoorend", "Saltmere", "Thornwold", "Wexmouth"];
    private static readonly string[] _clubSuffixes = ["Rovers", "Athletic", "Town", "Wanderers", "Rangers", "Albion", "Harriers", "Borough", "Vale", "FC"];
    private static readonly string[] _grounds = ["Park", "Lane", "Road", "Meadow", "Ground", "Field", "Arena"];
    private static readonly string[] _firstNames = ["Arlo", "Bastian", "Cormac", "Dario", "Emre", "Felipe", "Gideon", "Hakon", "Idris", "Jonah", "Kasimir", "Leif", "Mateo", "Nilo", "Oskar", "Pavel", "Quinn", "Rafferty", "Sefton", "Tobiah", "Ugo", "Viggo", "Wilder", "Yannis", "Zeno"];
    private static readonly string[] _surnameStarts = ["Ash", "Brin", "Cal", "Dun", "Ever", "Fal", "Gar", "Hol", "Kel", "Lor", "Mor", "Nor", "Pen", "Quel", "Ros", "Sel", "Tam", "Vor", "Wex", "Yar"];
    private static readonly string[] _surnameEnds = ["brook", "croft", "dane", "ford", "gate", "holm", "lowe", "mere", "ney", "ridge", "stead", "ton", "vane", "well", "wick"];

    /// <summary>
    /// Suggests names.
    /// </summary>
    /// <param name="kind">What the names are for.</param>
    /// <param name="count">How many, from 1 to <see cref="MaxCount"/>.</param>
    /// <returns>Distinct suggestions.</returns>
    public IReadOnlyList<string> Suggest(NameKind kind, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);

        var names = new HashSet<string>(StringComparer.Ordinal);
        lock (random)
        {
            while (names.Count < count)
            {
                names.Add(kind switch
                {
                    NameKind.Team => $"{Pick(_places)} {Pick(_clubSuffixes)}",
                    NameKind.Venue => $"{Pick(_places)} {Pick(_grounds)}",
                    _ => $"{Pick(_firstNames)} {Pick(_surnameStarts)}{Pick(_surnameEnds)}"
                });
            }
        }

        return [.. names];
    }

    private string Pick(string[] parts) => parts[random.Next(parts.Length)];
}
