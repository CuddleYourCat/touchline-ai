namespace Touchline.Storage;

/// <summary>
/// The rules Cosmos DB sets for a document <c>id</c>, checked before a request is sent, so a bad
/// ID from a URL reads as "no such document" instead of failing as a server error.
/// </summary>
public static class CosmosIds
{
    /// <summary>The longest <c>id</c> Cosmos DB accepts.</summary>
    public const int MaxLength = 255;

    private static readonly char[] _forbidden = ['/', '\\', '?', '#'];

    /// <summary>
    /// Says whether a string can be a Cosmos DB document <c>id</c>.
    /// </summary>
    /// <param name="id">The candidate ID.</param>
    /// <returns>True when it's non-empty, at most 255 characters, and has none of <c>/ \ ? #</c>.</returns>
    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxLength && id.IndexOfAny(_forbidden) < 0;
}
