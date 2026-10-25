using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Touchline.MatchCentre.Caching;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// The fan-match limits fall back to Cosmos DB when Redis can't answer (ADR-061), the chaos test
/// for this slice: Redis is down and the limits still hold.
/// </summary>
[TestFixture]
public class FallbackFanMatchLimitStoreTests
{
    private static readonly DateOnly _today = new(2026, 10, 25);
    private static readonly DateTimeOffset _now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private InMemoryLimitScripts _scripts = null!;
    private Mock<IFanMatchLimitStore> _cosmos = null!;
    private ListLogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _scripts = new InMemoryLimitScripts { Now = _now };
        _cosmos = new Mock<IFanMatchLimitStore>();
        _cosmos.Setup(c => c.TryTakeDaySlotAsync(It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _logger = new ListLogger();
    }

    [Test]
    public async Task With_redis_up_cosmos_db_is_never_touched()
    {
        var store = Store();

        (await store.TryTakeDaySlotAsync(_today, 5, CancellationToken.None)).Should().BeTrue();
        await store.TryClaimKeyAsync("k", "exh-a", _now, CancellationToken.None);
        await store.ReleaseKeyAsync("k", "exh-a", CancellationToken.None);
        await store.ReturnDaySlotAsync(_today, CancellationToken.None);

        _cosmos.Invocations.Should().BeEmpty();
        _logger.Levels.Should().BeEmpty();
    }

    [TestCaseSource(nameof(RedisFailures))]
    public async Task With_redis_failing_every_call_goes_to_cosmos_db_and_is_logged(Exception failure)
    {
        _scripts.FailWith = failure;
        var claim = new IdempotencyClaim("exh-old", _now);
        _cosmos.Setup(c => c.TryClaimKeyAsync("k", "exh-a", _now, It.IsAny<CancellationToken>())).ReturnsAsync(claim);
        _cosmos.Setup(c => c.TryReclaimKeyAsync("k", claim, "exh-a", _now, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var store = Store();

        (await store.TryTakeDaySlotAsync(_today, 5, CancellationToken.None)).Should().BeTrue();
        (await store.TryClaimKeyAsync("k", "exh-a", _now, CancellationToken.None)).Should().Be(claim);
        (await store.TryReclaimKeyAsync("k", claim, "exh-a", _now, CancellationToken.None)).Should().BeTrue();
        await store.ReleaseKeyAsync("k", "exh-a", CancellationToken.None);
        await store.ReturnDaySlotAsync(_today, CancellationToken.None);

        _cosmos.Verify(c => c.TryTakeDaySlotAsync(_today, 5, It.IsAny<CancellationToken>()), Times.Once);
        _cosmos.Verify(c => c.ReleaseKeyAsync("k", "exh-a", It.IsAny<CancellationToken>()), Times.Once);
        _cosmos.Verify(c => c.ReturnDaySlotAsync(_today, It.IsAny<CancellationToken>()), Times.Once);
        _logger.Levels.Should().HaveCount(5).And.OnlyContain(level => level == LogLevel.Warning);
    }

    [Test]
    public async Task A_full_day_in_cosmos_db_is_still_full_while_redis_is_down()
    {
        _scripts.FailWith = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        _cosmos.Setup(c => c.TryTakeDaySlotAsync(_today, 5, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await Store().TryTakeDaySlotAsync(_today, 5, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task A_disconnected_redis_falls_back_through_the_real_scripts_too()
    {
        var store = new FallbackFanMatchLimitStore(
            new RedisFanMatchLimitStore(new LuaFanMatchLimitScripts(new FixedRedisConnection(null))), _cosmos.Object, _logger);

        (await store.TryTakeDaySlotAsync(_today, 5, CancellationToken.None)).Should().BeTrue();

        _cosmos.Verify(c => c.TryTakeDaySlotAsync(_today, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task A_bug_is_not_hidden_by_falling_back()
    {
        _scripts.FailWith = new InvalidOperationException("a bug");

        var take = () => Store().TryTakeDaySlotAsync(_today, 5, CancellationToken.None);

        await take.Should().ThrowAsync<InvalidOperationException>();
        _cosmos.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task A_cancelled_call_is_cancelled_not_retried_on_cosmos_db()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var take = () => Store().TryTakeDaySlotAsync(_today, 5, cancelled.Token);

        await take.Should().ThrowAsync<OperationCanceledException>();
        _cosmos.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task A_cosmos_db_failure_behind_a_failed_redis_surfaces_as_before()
    {
        _scripts.FailWith = new RedisTimeoutException("slow", CommandStatus.Sent);
        _cosmos.Setup(c => c.TryTakeDaySlotAsync(_today, 5, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("cosmos down"));

        var take = () => Store().TryTakeDaySlotAsync(_today, 5, CancellationToken.None);

        await take.Should().ThrowAsync<HttpRequestException>();
    }

    private static IEnumerable<Exception> RedisFailures()
    {
        yield return new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        yield return new RedisTimeoutException("slow", CommandStatus.Sent);
        yield return new RedisServerException("ERR script");
        yield return new RedisUnavailableException("not connected");
    }

    private FallbackFanMatchLimitStore Store() => new(new RedisFanMatchLimitStore(_scripts), _cosmos.Object, _logger);

    private sealed class ListLogger : ILogger<FallbackFanMatchLimitStore>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}

/// <summary>What the Lua runner sends to Redis.</summary>
[TestFixture]
public class LuaFanMatchLimitScriptsTests
{
    private Mock<IDatabase> _database = null!;
    private LuaFanMatchLimitScripts _scripts = null!;

    [SetUp]
    public void SetUp()
    {
        _database = new Mock<IDatabase>();
        _scripts = new LuaFanMatchLimitScripts(new FixedRedisConnection(_database.Object));
    }

    [Test]
    public async Task Taking_a_slot_sends_the_limit_and_the_expiry_in_unix_seconds_against_one_key()
    {
        Returns(LuaFanMatchLimitScripts.Take, RedisResult.Create(1));
        var midnight = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

        (await _scripts.TakeAsync("touchline:fan-matches:day-20261025", 5, midnight)).Should().BeTrue();

        _database.Verify(d => d.ScriptEvaluateAsync(
            LuaFanMatchLimitScripts.Take,
            It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == "touchline:fan-matches:day-20261025"),
            It.Is<RedisValue[]>(values => values.Length == 2 && values[0] == 5 && values[1] == midnight.ToUnixTimeSeconds()),
            CommandFlags.None));
    }

    [Test]
    public async Task A_full_day_reads_as_no_slot()
    {
        Returns(LuaFanMatchLimitScripts.Take, RedisResult.Create(0));

        (await _scripts.TakeAsync("day", 5, DateTimeOffset.UnixEpoch)).Should().BeFalse();
    }

    [Test]
    public async Task A_claim_that_was_set_reads_as_null_and_an_existing_one_as_its_value()
    {
        Returns(LuaFanMatchLimitScripts.Claim, RedisResult.Create(RedisValue.Null));
        (await _scripts.ClaimAsync("k", "1:exh-a", TimeSpan.FromDays(1))).Should().BeNull();

        Returns(LuaFanMatchLimitScripts.Claim, RedisResult.Create((RedisValue)"1:exh-b"));
        (await _scripts.ClaimAsync("k", "1:exh-a", TimeSpan.FromDays(1))).Should().Be("1:exh-b");

        _database.Verify(d => d.ScriptEvaluateAsync(
            LuaFanMatchLimitScripts.Claim,
            It.IsAny<RedisKey[]>(),
            It.Is<RedisValue[]>(values => values[0] == "1:exh-a" && values[1] == (long)TimeSpan.FromDays(1).TotalMilliseconds),
            CommandFlags.None));
    }

    [TestCase(LuaFanMatchLimitScripts.Take)]
    [TestCase(LuaFanMatchLimitScripts.Return)]
    [TestCase(LuaFanMatchLimitScripts.Claim)]
    [TestCase(LuaFanMatchLimitScripts.Reclaim)]
    [TestCase(LuaFanMatchLimitScripts.Release)]
    public void Every_script_touches_only_its_one_key_so_it_runs_in_one_cluster_slot(string script)
    {
        script.Should().NotContain("KEYS[2]");
        script.Should().Contain("KEYS[1]");
    }

    [Test]
    public void Reclaim_and_release_compare_the_request_before_changing_anything()
    {
        LuaFanMatchLimitScripts.Reclaim.IndexOf("== ARGV[1]", StringComparison.Ordinal)
            .Should().BeLessThan(LuaFanMatchLimitScripts.Reclaim.LastIndexOf("redis.call('SET'", StringComparison.Ordinal));
        LuaFanMatchLimitScripts.Release.IndexOf("== ARGV[1]", StringComparison.Ordinal)
            .Should().BeLessThan(LuaFanMatchLimitScripts.Release.IndexOf("redis.call('DEL'", StringComparison.Ordinal));
    }

    private void Returns(string script, RedisResult result) =>
        _database.Setup(d => d.ScriptEvaluateAsync(script, It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
            .ReturnsAsync(result);
}
