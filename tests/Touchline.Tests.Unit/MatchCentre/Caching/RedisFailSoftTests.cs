using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Touchline.MatchCentre.Caching;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// A Redis failure is a logged miss, never a failed request (ADR-061): the HybridCache second
/// level, the output cache store and the connection itself.
/// </summary>
[TestFixture]
public class RedisFailSoftTests
{
    private static readonly byte[] _body = [1, 2, 3];

    [Test]
    public async Task The_second_level_reads_and_writes_nothing_while_redis_is_disconnected()
    {
        var cache = new RedisDistributedCache(new FixedRedisConnection(null), TimeProvider.System, NullLogger<RedisDistributedCache>.Instance);

        await cache.SetAsync("k", _body, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

        (await cache.GetAsync("k")).Should().BeNull();
        cache.Get("k").Should().BeNull();
    }

    [Test]
    public async Task The_second_level_turns_a_failing_redis_into_misses()
    {
        var cache = new RedisDistributedCache(
            new FixedRedisConnection(NameGuardVerdictCacheTests.FailingDatabase()), TimeProvider.System, NullLogger<RedisDistributedCache>.Instance);
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

        var calls = new Func<Task>[]
        {
            () => cache.GetAsync("k"),
            () => cache.SetAsync("k", _body, options),
            () => cache.RemoveAsync("k"),
            () => Task.Run(() => cache.Get("k")),
            () => Task.Run(() => cache.Set("k", _body, options)),
            () => Task.Run(() => cache.Remove("k")),
        };

        foreach (var call in calls)
        {
            await call.Should().NotThrowAsync();
        }

        (await cache.GetAsync("k")).Should().BeNull();
    }

    [Test]
    public async Task The_output_cache_keeps_working_from_memory_while_redis_is_down()
    {
        var store = Store(NameGuardVerdictCacheTests.FailingDatabase());

        await store.SetAsync("GET /api/matches/m1", _body, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        (await store.GetAsync("GET /api/matches/m1", CancellationToken.None)).Should().Equal(_body);
        (await store.GetAsync("GET /api/matches/m2", CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task The_output_cache_misses_quietly_when_redis_is_disconnected()
    {
        var store = Store(null);

        (await store.GetAsync("GET /api/matches/m1", CancellationToken.None)).Should().BeNull();
    }

    [TestCase(null, "touchline:oc:local:")]
    [TestCase("ca-matchcentre--0000042", "touchline:oc:ca-matchcentre--0000042:")]
    public void Output_cache_keys_belong_to_one_revision_so_a_wipe_starts_empty(string? revision, string prefix) =>
        TieredOutputCacheStore.KeyPrefix(revision).Should().Be(prefix);

    [Test]
    public async Task A_failed_connection_leaves_no_database_and_is_retried_only_after_the_wait()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var attempts = 0;
        var connection = new RedisConnection(
            () =>
            {
                attempts++;
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "no route");
            },
            TimeSpan.FromSeconds(30),
            time,
            NullLogger<RedisConnection>.Instance);

        connection.Database.Should().BeNull();
        await connection.Connecting;
        connection.Database.Should().BeNull();
        await connection.Connecting;
        attempts.Should().Be(1, "the next attempt waits");

        time.Advance(TimeSpan.FromSeconds(31));
        _ = connection.Database;
        await connection.Connecting;

        attempts.Should().Be(2);
    }

    [Test]
    public void Connections_fail_fast_with_short_timeouts_and_never_block_start_up()
    {
        var options = RedisConnection.Harden(ConfigurationOptions.Parse("localhost:6379"), new RedisOptions());

        options.AbortOnConnectFail.Should().BeFalse();
        options.ConnectTimeout.Should().Be(3000);
        options.SyncTimeout.Should().Be(1000);
        options.AsyncTimeout.Should().Be(1000);
        options.BacklogPolicy.Should().Be(BacklogPolicy.FailFast);
    }

    private static TieredOutputCacheStore Store(IDatabase? database) => new(
        new FixedRedisConnection(database),
        new ConfigurationBuilder().Build(),
        NullLogger<TieredOutputCacheStore>.Instance);
}
