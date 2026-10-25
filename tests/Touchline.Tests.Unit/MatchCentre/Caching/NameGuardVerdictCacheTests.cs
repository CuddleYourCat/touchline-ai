using System.Collections.Concurrent;
using Azure;
using Azure.AI.ContentSafety;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Caching;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// Name-guard verdicts through HybridCache with a shared second level (ADR-049, ADR-061): what one
/// replica learns, another uses, and an outage is never remembered anywhere.
/// </summary>
[TestFixture]
public class NameGuardVerdictCacheTests
{
    private Mock<ContentSafetyClient> _client = null!;
    private SharedCache _shared = null!;
    private readonly List<ServiceProvider> _providers = [];

    [SetUp]
    public void SetUp()
    {
        _client = new Mock<ContentSafetyClient>();
        _shared = new SharedCache();
        Respond(Clean());
    }

    [TearDown]
    public void TearDown()
    {
        _providers.ForEach(provider => provider.Dispose());
        _providers.Clear();
    }

    [Test]
    public async Task A_verdict_one_replica_reached_is_used_by_another_without_a_second_call()
    {
        await Replica().CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        var verdicts = await Replica().CheckAsync([new(NameKind.Team, "riverside  ROVERS")], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeTrue();
        Calls().Should().Be(1);
    }

    [Test]
    public async Task A_refusal_reaches_every_replica_with_its_reason()
    {
        Respond(ContentSafetyModelFactory.AnalyzeTextResult(
            [ContentSafetyModelFactory.TextBlocklistMatch("touchline-real-names", "item-1", "blocked united")], []));
        await Replica().CheckAsync([new(NameKind.Team, "Blocked United")], CancellationToken.None);
        Respond(Clean());

        var verdicts = await Replica().CheckAsync([new(NameKind.Team, "Blocked United")], CancellationToken.None);

        verdicts.Single().Should().Be(new NameVerdict(NameKind.Team, "Blocked United", false, ContentSafetyNameGuard.RealNameReason));
    }

    [Test]
    public async Task The_shared_entry_lives_for_the_verdict_cache_ttl_under_the_normalised_text()
    {
        await Replica(new ContentSafetyOptions { VerdictCacheTtl = TimeSpan.FromMinutes(20) })
            .CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        _shared.Entries.Should().ContainSingle().Which.Key.Should().Contain("name-guard|riverside rovers");
        _shared.Lifetimes.Single().Should().Be(TimeSpan.FromMinutes(20));
    }

    [Test]
    public async Task An_outage_is_never_written_to_the_shared_cache()
    {
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "down"));

        var check = () => Replica().CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        await check.Should().ThrowAsync<NameGuardUnavailableException>();
        _shared.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task A_ttl_of_zero_caches_nothing_at_all()
    {
        var guard = Replica(new ContentSafetyOptions { VerdictCacheTtl = TimeSpan.Zero });

        await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);
        await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        Calls().Should().Be(2);
        _shared.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task With_redis_failing_the_guard_still_answers_from_content_safety_and_its_own_memory()
    {
        var failing = new RedisDistributedCache(
            new FixedRedisConnection(FailingDatabase()), TimeProvider.System, NullLogger<RedisDistributedCache>.Instance);
        var guard = Replica(cache: failing);

        await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);
        var verdicts = await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeTrue();
        Calls().Should().Be(1, "the first level still holds the verdict");
    }

    [Test]
    public async Task A_replica_keeps_a_verdict_in_memory_for_only_a_few_minutes_so_the_worst_case_is_ttl_plus_that()
    {
        var recording = new RecordingHybridCache();
        var guard = new ContentSafetyNameGuard(
            _client.Object, recording, Options.Create(new ContentSafetyOptions()), NullLogger<ContentSafetyNameGuard>.Instance);

        await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        recording.Options.Single().Expiration.Should().Be(TimeSpan.FromHours(1));
        recording.Options.Single().LocalCacheExpiration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task The_local_lifetime_is_never_longer_than_the_verdict_ttl()
    {
        var recording = new RecordingHybridCache();
        var options = new ContentSafetyOptions { VerdictCacheTtl = TimeSpan.FromMinutes(2), VerdictLocalCacheTtl = TimeSpan.FromMinutes(5) };
        var guard = new ContentSafetyNameGuard(_client.Object, recording, Options.Create(options), NullLogger<ContentSafetyNameGuard>.Instance);

        await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        recording.Options.Single().LocalCacheExpiration.Should().Be(TimeSpan.FromMinutes(2));
    }

    /// <summary>A database whose every command fails as an unreachable Redis's would.</summary>
    /// <returns>The mock's object.</returns>
    internal static IDatabase FailingDatabase() =>
        new Mock<IDatabase> { DefaultValueProvider = new ThrowingValues() }.Object;

    private ContentSafetyNameGuard Replica(ContentSafetyOptions? options = null, IDistributedCache? cache = null)
    {
        var provider = new ServiceCollection()
            .AddSingleton(cache ?? _shared)
            .AddHybridCache().Services
            .BuildServiceProvider();
        _providers.Add(provider);
        return new ContentSafetyNameGuard(
            _client.Object, provider.GetRequiredService<HybridCache>(), Options.Create(options ?? new ContentSafetyOptions()), NullLogger<ContentSafetyNameGuard>.Instance);
    }

    private int Calls() => _client.Invocations.Count(i => i.Method.Name == nameof(ContentSafetyClient.AnalyzeTextAsync));

    private static AnalyzeTextResult Clean() =>
        ContentSafetyModelFactory.AnalyzeTextResult([], [ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Hate, 0)]);

    private void Respond(AnalyzeTextResult result) =>
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));

    /// <summary>Stands in for Redis: one store every replica's HybridCache shares.</summary>
    private sealed class SharedCache : IDistributedCache
    {
        public ConcurrentDictionary<string, byte[]> Entries { get; } = new();

        public List<TimeSpan?> Lifetimes { get; } = [];

        public byte[]? Get(string key) => Entries.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Entries[key] = value;
            Lifetimes.Add(options.AbsoluteExpirationRelativeToNow);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => Entries.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>A HybridCache that caches nothing and records the entry options it's given.</summary>
    private sealed class RecordingHybridCache : HybridCache
    {
        public List<HybridCacheEntryOptions> Options { get; } = [];

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options!);
            return factory(state, cancellationToken);
        }

        public override ValueTask SetAsync<T>(
            string key, T value, HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>Makes every unset call on a mock throw a Redis connection failure.</summary>
    private sealed class ThrowingValues : DefaultValueProvider
    {
        protected override object GetDefaultValue(Type type, Mock mock) =>
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down");
    }
}
