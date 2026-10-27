using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// The commissioned crest artwork for the built-in clubs (ADR-072 amendment). It is embedded in this assembly as
/// 256 × 256 WebP and served in place of the generated SVG, from the same crest route, so every page that follows a
/// team's <c>crestUrl</c> picks it up with no client change.
/// </summary>
internal static class CrestArtwork
{
    /// <summary>The WebP media type.</summary>
    public const string ContentType = "image/webp";

    private static readonly Lazy<FrozenDictionary<string, Artwork>> _byTeamId = new(Load);

    /// <summary>Finds the artwork for a team.</summary>
    /// <param name="teamId">The team's ID.</param>
    /// <param name="artwork">The artwork, when the team has any.</param>
    /// <returns>True when the team has commissioned artwork.</returns>
    public static bool TryGet(string teamId, out Artwork artwork) => _byTeamId.Value.TryGetValue(teamId, out artwork!);

    private static FrozenDictionary<string, Artwork> Load() =>
        new[] { "north-bank-fc", "thames-valley-united" }
            .ToFrozenDictionary(id => id, id => Read($"crest-{id}.webp"), StringComparer.Ordinal);

    private static Artwork Read(string resource)
    {
        using var stream = typeof(CrestArtwork).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded resource '{resource}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        return new Artwork(bytes, $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..16]}\"");
    }

    /// <summary>One crest image.</summary>
    /// <param name="Bytes">The WebP file.</param>
    /// <param name="ETag">A strong entity tag from the file's hash.</param>
    internal sealed record Artwork(byte[] Bytes, string ETag);
}
