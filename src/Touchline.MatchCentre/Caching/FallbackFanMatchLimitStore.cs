using Touchline.Storage;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// The fan-match limits with Redis first and Cosmos DB behind it (ADR-061): each call goes to
/// Redis, and if Redis can't be reached, times out or refuses the command, the same call goes to
/// the Cosmos DB store instead, logged. So the limits still hold during a Redis outage, and a
/// Cosmos DB failure surfaces exactly as it did before Redis (a <c>503</c> from the launcher).
/// </summary>
/// <remarks>
/// <para>
/// This is the decorator pattern with a fallback, rather than a circuit breaker: every call tries
/// Redis again, and while it is down that costs nothing, because a disconnected Redis fails at
/// once (<see cref="RedisConnection"/>).
/// </para>
/// <para>
/// Accepted (ADR-061): a day that switches stores part way counts in each separately, a key
/// claimed in one store isn't seen by the other, and a Redis command that timed out after it ran
/// may be repeated in Cosmos DB, spending a slot twice. The one-live-match rule stays in Cosmos
/// DB and bounds all three.
/// </para>
/// </remarks>
/// <param name="redis">The Redis store.</param>
/// <param name="cosmos">The Cosmos DB store, used when Redis fails.</param>
/// <param name="logger">Logs each fallback.</param>
internal sealed partial class FallbackFanMatchLimitStore(
    IFanMatchLimitStore redis,
    IFanMatchLimitStore cosmos,
    ILogger<FallbackFanMatchLimitStore> logger) : IFanMatchLimitStore
{
    /// <inheritdoc />
    public Task<bool> TryTakeDaySlotAsync(DateOnly day, int limit, CancellationToken cancellationToken) =>
        WithFallbackAsync(store => store.TryTakeDaySlotAsync(day, limit, cancellationToken), nameof(TryTakeDaySlotAsync));

    /// <inheritdoc />
    public Task ReturnDaySlotAsync(DateOnly day, CancellationToken cancellationToken) =>
        WithFallbackAsync(async store =>
        {
            await store.ReturnDaySlotAsync(day, cancellationToken);
            return true;
        }, nameof(ReturnDaySlotAsync));

    /// <inheritdoc />
    public Task<IdempotencyClaim?> TryClaimKeyAsync(string key, string requestId, DateTimeOffset now, CancellationToken cancellationToken) =>
        WithFallbackAsync(store => store.TryClaimKeyAsync(key, requestId, now, cancellationToken), nameof(TryClaimKeyAsync));

    /// <inheritdoc />
    public Task<bool> TryReclaimKeyAsync(string key, IdempotencyClaim previous, string requestId, DateTimeOffset now, CancellationToken cancellationToken) =>
        WithFallbackAsync(store => store.TryReclaimKeyAsync(key, previous, requestId, now, cancellationToken), nameof(TryReclaimKeyAsync));

    /// <inheritdoc />
    public Task ReleaseKeyAsync(string key, string requestId, CancellationToken cancellationToken) =>
        WithFallbackAsync(async store =>
        {
            await store.ReleaseKeyAsync(key, requestId, cancellationToken);
            return true;
        }, nameof(ReleaseKeyAsync));

    private async Task<T> WithFallbackAsync<T>(Func<IFanMatchLimitStore, Task<T>> call, string operation)
    {
        try
        {
            return await call(redis);
        }
        catch (Exception exception) when (RedisFailures.IsRedisFailure(exception))
        {
            LogFellBack(logger, exception, operation);
        }

        return await call(cosmos);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis couldn't serve the fan-match limits ({Operation}), so Cosmos DB did")]
    private static partial void LogFellBack(ILogger logger, Exception exception, string operation);
}
