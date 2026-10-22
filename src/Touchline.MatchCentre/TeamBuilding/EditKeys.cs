using System.Security.Cryptography;
using System.Text;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Edit keys for fan-built teams (ADR-031): a random 256-bit secret, given to the creator once,
/// with only its SHA-256 hash stored.
/// </summary>
/// <remarks>
/// A plain hash is enough here, with no salt or slow hashing like a password needs: the key is
/// 256 random bits, so there's no dictionary to guess from.
/// </remarks>
internal static class EditKeys
{
    /// <summary>The request header that carries an edit key.</summary>
    public const string Header = "Touchline-Edit-Key";

    /// <summary>Makes a new key.</summary>
    /// <returns>The key, to give to the creator, and its hash, to store.</returns>
    public static (string Key, string Hash) Create()
    {
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (key, Hash(key));
    }

    /// <summary>Hashes a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The SHA-256 hash, as lower-case hex.</returns>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Whether a key matches a stored hash, compared in constant time so the comparison can't be
    /// timed to guess the hash byte by byte.
    /// </summary>
    /// <param name="key">The key the client sent, or null.</param>
    /// <param name="hash">The stored hash, or null for a read-only team.</param>
    /// <returns>True only when both are present and match.</returns>
    public static bool Matches(string? key, string? hash) =>
        key is { Length: > 0 } && hash is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(hash));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
