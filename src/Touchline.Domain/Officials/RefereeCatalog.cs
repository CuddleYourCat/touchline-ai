using System.Globalization;

namespace Touchline.Domain.Officials;

/// <summary>
/// Loads the referee profiles shipped with the domain as embedded JSON, checked once on
/// first use. Cosmos DB becomes the source of truth later (ADR-021). This file stays as its
/// seed data and the unit tests' fixture.
/// </summary>
public static class RefereeCatalog
{
    private const string _resourceName = "Touchline.Domain.Officials.referees.json";

    private static readonly Lazy<IReadOnlyList<RefereeProfile>> _embedded = new(LoadAllEmbedded);

    /// <summary>
    /// The validated embedded referees, in ID order, so a seed always picks the same one.
    /// </summary>
    /// <returns>The referees.</returns>
    /// <exception cref="InvalidDataException">The file is missing, malformed or fails validation.</exception>
    public static IReadOnlyList<RefereeProfile> LoadEmbedded() => _embedded.Value;

    /// <summary>
    /// Picks the referee for a seed, without using a random draw, so the choice doesn't
    /// change the match itself (ADR-020).
    /// </summary>
    /// <param name="seed">The match seed. Negative seeds are fine.</param>
    /// <returns>A referee from the catalog.</returns>
    public static RefereeProfile ForSeed(int seed)
    {
        var referees = LoadEmbedded();
        var index = (int)(((long)seed % referees.Count + referees.Count) % referees.Count);
        return referees[index];
    }

    /// <summary>
    /// Parses and validates referees from a named JSON source.
    /// </summary>
    /// <param name="source">The source's name, used in error messages.</param>
    /// <param name="json">A JSON array of referees.</param>
    /// <returns>The referees, in ID order.</returns>
    /// <exception cref="InvalidDataException">The source is malformed, empty or invalid.</exception>
    internal static IReadOnlyList<RefereeProfile> Parse(string source, Stream json)
    {
        var referees = EmbeddedJson.Deserialize<List<RefereeProfile>>(source, json, "Referee list");
        var problems = new List<string>();

        if (referees.Count == 0 || referees.Any(r => r is null))
        {
            throw new InvalidDataException($"Referee list '{source}' is invalid: it must hold at least one referee and no nulls.");
        }

        foreach (var referee in referees)
        {
            var label = string.IsNullOrWhiteSpace(referee.Id) ? "a referee" : $"referee '{referee.Id}'";
            if (string.IsNullOrWhiteSpace(referee.Id) || string.IsNullOrWhiteSpace(referee.Name) || string.IsNullOrWhiteSpace(referee.Style))
            {
                problems.Add($"{label} needs an ID, a name and a style");
            }

            foreach (var (name, value) in new[] { ("strictness", referee.Strictness), ("whistle", referee.Whistle), ("advantage", referee.Advantage) })
            {
                if (!Ranges.IsBetween(value, 0, 1))
                {
                    problems.Add($"{label}'s {name} is {value.ToString(CultureInfo.InvariantCulture)}; it must be between 0 and 1");
                }
            }
        }

        problems.AddRange(CatalogChecks.Duplicates(referees.Select(r => r.Id), "referee ID"));

        return problems.Count > 0
            ? throw new InvalidDataException($"Referee list '{source}' is invalid: {string.Join("; ", problems)}.")
            : referees.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<RefereeProfile> LoadAllEmbedded()
    {
        using var json = EmbeddedJson.Open(_resourceName);
        return Parse(_resourceName, json);
    }
}
