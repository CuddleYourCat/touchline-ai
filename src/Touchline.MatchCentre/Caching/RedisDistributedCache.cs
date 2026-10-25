using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// HybridCache's second level (ADR-061): plain string keys in Redis with an expiry, shared by
/// every replica. A Redis failure is a logged miss and a skipped write, never an exception, so
/// HybridCache simply carries on with its in-memory first level.
/// </summary>
/// <remarks>
/// Each entry is one key, so it lives in one cluster slot (Managed Redis is OSS-clustered).
/// Sliding expiry isn't used by HybridCache; if given, it's treated as an absolute lifetime.
/// </remarks>
/// <param name="connection">The shared Redis connection.</param>
/// <param name="time">The clock, for an absolute expiry.</param>
/// <param name="logger">Logs a failed read or write.</param>
internal sealed partial class RedisDistributedCache(IRedisConnection connection, TimeProvider time, ILogger<RedisDistributedCache> logger) : IDistributedCache
{
    /// <summary>Every HybridCache key in Redis starts with this.</summary>
    public const string KeyPrefix = "touchline:hc:";

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        if (connection.Database is not { } database)
        {
            return null;
        }

        try
        {
            return database.StringGet(KeyPrefix + key);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(logger, exception, "read");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        if (connection.Database is not { } database)
        {
            return null;
        }

        try
        {
            return await database.StringGetAsync(KeyPrefix + key).WaitAsync(token);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(logger, exception, "read");
            return null;
        }
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (connection.Database is not { } database || Lifetime(options) is not { } lifetime)
        {
            return;
        }

        try
        {
            database.StringSet(KeyPrefix + key, value, lifetime);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(logger, exception, "write");
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        if (connection.Database is not { } database || Lifetime(options) is not { } lifetime)
        {
            return;
        }

        try
        {
            await database.StringSetAsync(KeyPrefix + key, value, lifetime).WaitAsync(token);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(logger, exception, "write");
        }
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        // Nothing slides: every entry has an absolute lifetime.
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Remove(string key)
    {
        if (connection.Database is not { } database)
        {
            return;
        }

        try
        {
            database.KeyDelete(KeyPrefix + key);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(logger, exception, "delete");
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        if (connection.Database is not { } database)
        {
            return;
        }

        try
        {
            await database.KeyDeleteAsync(KeyPrefix + key).WaitAsync(token);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(logger, exception, "delete");
        }
    }

    // An entry always gets a lifetime, so nothing in Redis lives for ever. Null means it has
    // already expired and isn't written.
    private TimeSpan? Lifetime(DistributedCacheEntryOptions options)
    {
        var lifetime = options.AbsoluteExpirationRelativeToNow
            ?? (options.AbsoluteExpiration is { } at ? at - time.GetUtcNow() : (TimeSpan?)null)
            ?? options.SlidingExpiration
            ?? TimeSpan.FromMinutes(5);
        return lifetime > TimeSpan.Zero ? lifetime : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A Redis cache {Operation} failed, so it counts as a miss")]
    private static partial void LogFailed(ILogger logger, Exception exception, string operation);
}
