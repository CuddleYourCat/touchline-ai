using System.Text.Json;
using System.Text.Json.Serialization;

namespace Touchline.Domain;

/// <summary>
/// The one JSON shape for domain types, shared by every service that reads or writes them
/// (team profiles, Event Hubs messages, Cosmos DB documents): camelCase names and enums as
/// strings, so a stored event reads <c>"type": "Shot"</c> rather than <c>"type": 6</c>.
/// Every constructor parameter must be present in the JSON.
/// </summary>
public static class DomainJson
{
    /// <summary>Serializer options for all domain types. Treat as read-only.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            // A constructor parameter missing from the JSON is an error rather than a silent
            // 0 or null, so a player without a top speed can't load as one who never runs.
            RespectRequiredConstructorParameters = true
        };
        // Read-only options can't be changed by a caller, and are faster to reuse.
        // populateMissingResolver supplies the default reflection-based resolver.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
