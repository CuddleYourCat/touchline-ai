using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Touchline.MatchCentre.Caching;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// The same contract against a real Redis, so the Lua scripts themselves are run. Opt-in: it runs
/// only when <c>TOUCHLINE_TEST_REDIS</c> holds a connection string (for example
/// <c>localhost:6390</c> for <c>docker run -p 6390:6379 redis:7-alpine</c>), and is ignored otherwise,
/// so CI and a machine without Docker stay green. It deletes every <c>touchline:fan-matches:*</c>
/// key there before each test, so never point it at a shared Redis.
/// </summary>
[TestFixture]
[Category("RedisIntegration")]
[NonParallelizable]
public class LiveRedisFanMatchLimitStoreTests : FanMatchLimitStoreContractTests
{
    private const string _variable = "TOUCHLINE_TEST_REDIS";
    private IConnectionMultiplexer? _multiplexer;

    [OneTimeSetUp]
    public async Task Connect()
    {
        if (Environment.GetEnvironmentVariable(_variable) is not { Length: > 0 } connectionString)
        {
            Assert.Ignore($"Set {_variable} to a throwaway Redis to run the Lua scripts for real.");
            return;
        }

        var options = RedisConnection.Harden(ConfigurationOptions.Parse(connectionString), new RedisOptions());
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
    }

    [OneTimeTearDown]
    public async Task Disconnect()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
        }
    }

    /// <inheritdoc />
    protected override IFanMatchLimitStore Create()
    {
        var database = _multiplexer!.GetDatabase();
        foreach (var server in _multiplexer.GetServers())
        {
            foreach (var key in server.Keys(pattern: RedisFanMatchLimitStore.KeyPrefix + "*"))
            {
                database.KeyDelete(key);
            }
        }

        return new RedisFanMatchLimitStore(new LuaFanMatchLimitScripts(new FixedRedisConnection(database)));
    }

    /// <inheritdoc />
    protected override string StoredText()
    {
        var database = _multiplexer!.GetDatabase();
        return string.Join("\n", _multiplexer.GetServers()
            .SelectMany(server => server.Keys(pattern: RedisFanMatchLimitStore.KeyPrefix + "*"))
            .Select(key => $"{key}={database.StringGet(key)}"));
    }

    [Test]
    public async Task The_real_connection_reaches_the_same_redis()
    {
        var connection = new RedisConnection(
            () => Task.FromResult(_multiplexer!), TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<RedisConnection>.Instance);
        _ = connection.Database;
        await connection.Connecting;

        var store = new RedisFanMatchLimitStore(new LuaFanMatchLimitScripts(connection));
        (await store.TryTakeDaySlotAsync(Today, 1, CancellationToken.None)).Should().BeTrue();
        (await store.TryTakeDaySlotAsync(Today, 1, CancellationToken.None)).Should().BeFalse();
    }
}
