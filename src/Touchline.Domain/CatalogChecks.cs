namespace Touchline.Domain;

/// <summary>Validation shared by the profile catalogs (teams and referees).</summary>
internal static class CatalogChecks
{
    /// <summary>One problem per value that appears more than once. Blank values are left to the "missing" checks.</summary>
    public static IEnumerable<string> Duplicates(IEnumerable<string?> values, string label) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate {label} '{group.Key}'");
}
