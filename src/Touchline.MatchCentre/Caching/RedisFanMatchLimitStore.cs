using System.Globalization;
using StackExchange.Redis;
using Touchline.Storage;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// The Redis commands behind the fan-match limits, each one atomic (a Lua script runs alone on
/// its shard), so two launches can't both take the last slot or move the same key. Every script
/// touches one key, so it runs in one cluster slot.
/// </summary>
internal interface IFanMatchLimitScripts
{
    /// <summary>Adds one to a day counter if it's below the limit, and sets it to expire.</summary>
    /// <param name="key">The day counter.</param>
    /// <param name="limit">How many the day allows.</param>
    /// <param name="expiresAt">When the counter goes.</param>
    /// <returns>False when the day is full.</returns>
    Task<bool> TakeAsync(string key, int limit, DateTimeOffset expiresAt);

    /// <summary>Takes one off a day counter, never below zero, keeping its expiry.</summary>
    /// <param name="key">The day counter.</param>
    /// <returns>A task that completes once it's done.</returns>
    Task ReturnAsync(string key);

    /// <summary>Sets a key's claim if it has none.</summary>
    /// <param name="key">The key's Redis key.</param>
    /// <param name="claim">The claim to set.</param>
    /// <param name="lifetime">How long the claim lasts.</param>
    /// <returns>Null when this call set it; otherwise the claim already there, unchanged.</returns>
    Task<string?> ClaimAsync(string key, string claim, TimeSpan lifetime);

    /// <summary>Replaces a key's claim only if it still belongs to a given request, or sets it if it went.</summary>
    /// <param name="key">The key's Redis key.</param>
    /// <param name="expectedRequestId">The request the claim must still be for.</param>
    /// <param name="claim">The new claim.</param>
    /// <param name="lifetime">How long the new claim lasts.</param>
    /// <returns>False if the claim had moved to another request.</returns>
    Task<bool> ReclaimAsync(string key, string expectedRequestId, string claim, TimeSpan lifetime);

    /// <summary>Deletes a key's claim only if it belongs to a given request.</summary>
    /// <param name="key">The key's Redis key.</param>
    /// <param name="requestId">The request the claim must be for.</param>
    /// <returns>A task that completes once it's done.</returns>
    Task ReleaseAsync(string key, string requestId);
}

/// <summary>
/// A claim's stored form: <c>{claimed, Unix milliseconds}:{request ID}</c>. The time comes first
/// and has no colon, so the request ID is everything after the first colon, which is what the
/// scripts compare.
/// </summary>
internal static class ClaimValue
{
    /// <summary>Writes a claim.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="claimedUtc">When it was claimed.</param>
    /// <returns>For example <c>1761382800000:exh-202610250900-1a2b3c4d</c>.</returns>
    public static string Format(string requestId, DateTimeOffset claimedUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{claimedUtc.ToUnixTimeMilliseconds()}:{requestId}");

    /// <summary>Reads a claim.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The claim, or null if the value isn't one.</returns>
    public static IdempotencyClaim? Parse(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && long.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var millis)
            ? new IdempotencyClaim(value[(colon + 1)..], DateTimeOffset.FromUnixTimeMilliseconds(millis))
            : null;
    }

    /// <summary>The request ID of a stored claim, as the scripts read it.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>Everything after the first colon, or null with no colon.</returns>
    public static string? RequestIdOf(string value) =>
        value.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? value[(colon + 1)..] : null;
}

/// <summary>
/// The fan-match limits in Redis (ADR-061), with the same rules as
/// <see cref="CosmosFanMatchLimitStore"/>: one counter for the whole UTC day (not per fan), and
/// one claim per <c>Idempotency-Key</c>, keyed by its hash so the key itself is never stored.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A day is <c>touchline:fan-matches:day-yyyyMMdd</c>: an atomic check-and-<c>INCR</c> against
/// the limit, expiring at the next UTC midnight. A refund is a <c>DECR</c> that never goes below zero.</item>
/// <item>A key is <c>touchline:fan-matches:idem-{32 hex of its SHA-256}</c>: a <c>SET NX</c> of the
/// request ID and the claim time, for <see cref="CosmosFanMatchLimitStore.KeyTtl"/> (a day), as in
/// Cosmos DB, so a retry just after midnight is still a repeat.</item>
/// <item>Moving or releasing a claim compares its request ID first, in the same script, so one
/// request can't release or overwrite another's claim.</item>
/// </list>
/// Throws when Redis can't be reached; <see cref="FallbackFanMatchLimitStore"/> then uses Cosmos DB.
/// </remarks>
/// <param name="scripts">The atomic Redis commands.</param>
internal sealed class RedisFanMatchLimitStore(IFanMatchLimitScripts scripts) : IFanMatchLimitStore
{
    /// <summary>Every limit key in Redis starts with this.</summary>
    public const string KeyPrefix = "touchline:fan-matches:";

    /// <summary>A day counter's Redis key.</summary>
    /// <param name="day">The UTC day.</param>
    /// <returns>For example <c>touchline:fan-matches:day-20261025</c>.</returns>
    public static string DayKey(DateOnly day) => KeyPrefix + CosmosFanMatchLimitStore.DayId(day);

    /// <summary>An idempotency key's Redis key.</summary>
    /// <param name="key">The client's key.</param>
    /// <returns>For example <c>touchline:fan-matches:idem-3f2a…</c>.</returns>
    public static string ClaimKey(string key) => KeyPrefix + CosmosFanMatchLimitStore.KeyId(key);

    /// <summary>When a day's counter expires: the UTC midnight that ends it.</summary>
    /// <param name="day">The UTC day.</param>
    /// <returns>Midnight at the start of the next day.</returns>
    public static DateTimeOffset DayEnds(DateOnly day) => new(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <inheritdoc />
    public async Task<bool> TryTakeDaySlotAsync(DateOnly day, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return limit >= 1 && await scripts.TakeAsync(DayKey(day), limit, DayEnds(day)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReturnDaySlotAsync(DateOnly day, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await scripts.ReturnAsync(DayKey(day)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim?> TryClaimKeyAsync(string key, string requestId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await scripts.ClaimAsync(ClaimKey(key), ClaimValue.Format(requestId, now), CosmosFanMatchLimitStore.KeyTtl)
            .WaitAsync(cancellationToken);
        return existing is null
            ? null
            : ClaimValue.Parse(existing) ?? throw new InvalidOperationException("An idempotency key's claim in Redis couldn't be read.");
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimKeyAsync(string key, IdempotencyClaim previous, string requestId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        cancellationToken.ThrowIfCancellationRequested();
        return await scripts.ReclaimAsync(ClaimKey(key), previous.RequestId, ClaimValue.Format(requestId, now), CosmosFanMatchLimitStore.KeyTtl)
            .WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseKeyAsync(string key, string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await scripts.ReleaseAsync(ClaimKey(key), requestId).WaitAsync(cancellationToken);
    }
}

/// <summary>
/// <see cref="IFanMatchLimitScripts"/> as Lua scripts on the shared connection. Throws
/// <see cref="RedisUnavailableException"/> while Redis isn't connected.
/// </summary>
/// <param name="connection">The shared Redis connection.</param>
internal sealed class LuaFanMatchLimitScripts(IRedisConnection connection) : IFanMatchLimitScripts
{
    /// <summary>KEYS[1] the counter; ARGV[1] the limit, ARGV[2] the expiry in Unix seconds. 1 if a slot was taken.</summary>
    internal const string Take = """
        local count = tonumber(redis.call('GET', KEYS[1]) or '0')
        if count >= tonumber(ARGV[1]) then return 0 end
        redis.call('INCR', KEYS[1])
        redis.call('EXPIREAT', KEYS[1], ARGV[2])
        return 1
        """;

    /// <summary>KEYS[1] the counter. DECR keeps the key's expiry.</summary>
    internal const string Return = """
        local count = tonumber(redis.call('GET', KEYS[1]) or '0')
        if count > 0 then redis.call('DECR', KEYS[1]) end
        return 1
        """;

    /// <summary>KEYS[1] the claim; ARGV[1] the claim, ARGV[2] its lifetime in milliseconds. Nil if set, else the claim there.</summary>
    internal const string Claim = """
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then return false end
        return redis.call('GET', KEYS[1])
        """;

    /// <summary>KEYS[1] the claim; ARGV[1] the expected request ID, ARGV[2] the new claim, ARGV[3] its lifetime in milliseconds.</summary>
    internal const string Reclaim = """
        local current = redis.call('GET', KEYS[1])
        if not current then
          if redis.call('SET', KEYS[1], ARGV[2], 'NX', 'PX', ARGV[3]) then return 1 end
          return 0
        end
        local colon = string.find(current, ':', 1, true)
        if colon and string.sub(current, colon + 1) == ARGV[1] then
          redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
          return 1
        end
        return 0
        """;

    /// <summary>KEYS[1] the claim; ARGV[1] the request ID it must be for.</summary>
    internal const string Release = """
        local current = redis.call('GET', KEYS[1])
        if current then
          local colon = string.find(current, ':', 1, true)
          if colon and string.sub(current, colon + 1) == ARGV[1] then
            redis.call('DEL', KEYS[1])
            return 1
          end
        end
        return 0
        """;

    /// <inheritdoc />
    public async Task<bool> TakeAsync(string key, int limit, DateTimeOffset expiresAt) =>
        (long)await RunAsync(Take, key, limit, expiresAt.ToUnixTimeSeconds()) == 1;

    /// <inheritdoc />
    public Task ReturnAsync(string key) => RunAsync(Return, key);

    /// <inheritdoc />
    public async Task<string?> ClaimAsync(string key, string claim, TimeSpan lifetime)
    {
        var result = await RunAsync(Claim, key, claim, (long)lifetime.TotalMilliseconds);
        return result.IsNull ? null : (string?)result;
    }

    /// <inheritdoc />
    public async Task<bool> ReclaimAsync(string key, string expectedRequestId, string claim, TimeSpan lifetime) =>
        (long)await RunAsync(Reclaim, key, expectedRequestId, claim, (long)lifetime.TotalMilliseconds) == 1;

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string requestId) => RunAsync(Release, key, requestId);

    private async Task<RedisResult> RunAsync(string script, string key, params RedisValue[] arguments) =>
        connection.Database is { } database
            ? await database.ScriptEvaluateAsync(script, new RedisKey[] { key }, arguments)
            : throw new RedisUnavailableException("Redis isn't connected.");
}
