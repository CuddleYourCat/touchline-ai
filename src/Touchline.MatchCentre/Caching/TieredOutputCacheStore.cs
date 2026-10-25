using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// The output cache's store when Redis is configured (ADR-061): this replica's memory first, then
/// Redis, which every replica shares. A write goes to both. With Redis down, the memory level
/// keeps working and a Redis read or write is a logged miss, never a failed request.
/// </summary>
/// <remarks>
/// <para>
/// Redis keys carry the Container Apps revision (<c>CONTAINER_APP_REVISION</c>), so a new
/// revision starts with an empty cache. That matters for <c>wipe-data</c> (ADR-059, ADR-060): the
/// wipe always ends on a new revision, so no replica can serve a cached copy of a deleted match.
/// </para>
/// <para>
/// Tags aren't used by the finished-match policy, so eviction by tag does nothing; entries go
/// when they expire. Each entry is one Redis key, so it lives in one cluster slot.
/// </para>
/// </remarks>
internal sealed partial class TieredOutputCacheStore : IOutputCacheStore, IDisposable
{
    /// <summary>The most entries kept in memory, so a burst of viewer variations can't grow without limit.</summary>
    public const int MemoryEntryLimit = 2000;

    private readonly IRedisConnection _connection;
    private readonly ILogger<TieredOutputCacheStore> _logger;
    private readonly MemoryCache _memory;
    private readonly string _prefix;

    /// <summary>Creates the store.</summary>
    /// <param name="connection">The shared Redis connection.</param>
    /// <param name="configuration">The app's configuration, for the revision name.</param>
    /// <param name="logger">Logs a failed Redis read or write.</param>
    public TieredOutputCacheStore(IRedisConnection connection, IConfiguration configuration, ILogger<TieredOutputCacheStore> logger)
    {
        _connection = connection;
        _logger = logger;
        _memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = MemoryEntryLimit });
        _prefix = KeyPrefix(configuration["CONTAINER_APP_REVISION"]);
    }

    /// <summary>The Redis key prefix for a revision.</summary>
    /// <param name="revision">The Container Apps revision, or null when running locally.</param>
    /// <returns>For example <c>touchline:oc:ca-matchcentre--0000042:</c>.</returns>
    public static string KeyPrefix(string? revision) =>
        $"touchline:oc:{(string.IsNullOrWhiteSpace(revision) ? "local" : revision)}:";

    /// <inheritdoc />
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(key, out byte[]? local))
        {
            return local;
        }

        if (_connection.Database is not { } database)
        {
            return null;
        }

        try
        {
            return await database.StringGetAsync(_prefix + key).WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(_logger, exception, "read");
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (validFor <= TimeSpan.Zero)
        {
            return;
        }

        _memory.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = validFor, Size = 1 });
        if (_connection.Database is not { } database)
        {
            return;
        }

        try
        {
            await database.StringSetAsync(_prefix + key, value, validFor).WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFailed(_logger, exception, "write");
        }
    }

    /// <inheritdoc />
    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A Redis output cache {Operation} failed, so it counts as a miss")]
    private static partial void LogFailed(ILogger logger, Exception exception, string operation);
}
