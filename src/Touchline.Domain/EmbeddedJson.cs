using System.Reflection;
using System.Text.Json;

namespace Touchline.Domain;

/// <summary>Reads the JSON files embedded in the domain assembly (team and referee profiles).</summary>
internal static class EmbeddedJson
{
    public static Assembly Assembly => typeof(EmbeddedJson).Assembly;

    public static IReadOnlyList<string> Names(string prefix) =>
        Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .ToList();

    public static Stream Open(string resourceName) =>
        Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Embedded resource '{resourceName}' could not be opened.");

    /// <summary>Deserializes with the domain's JSON options, reporting every failure as invalid data naming the source.</summary>
    public static T Deserialize<T>(string source, Stream json, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, DomainJson.Options)
                ?? throw new InvalidDataException($"{what} '{source}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{what} '{source}' isn't valid JSON: {ex.Message}", ex);
        }
    }
}
