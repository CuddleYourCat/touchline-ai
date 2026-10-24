using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Live;
using Touchline.MatchCentre.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class MatchSourceRoutingTests
{
    private const string _storedId = "fixture-2026-r1-001";

    private Mock<IMatchReadModel> _demo = null!;
    private Mock<IMatchReadModel> _cosmos = null!;

    [SetUp]
    public void CreateSources()
    {
        _demo = new Mock<IMatchReadModel>(MockBehavior.Strict);
        _cosmos = new Mock<IMatchReadModel>(MockBehavior.Strict);
    }

    [Test]
    public async Task A_demo_id_is_served_by_the_recording_and_any_other_id_by_cosmos_db()
    {
        _demo.Setup(m => m.GetMomentumAsync("demo-1", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _cosmos.Setup(m => m.GetMomentumAsync(_storedId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var router = Router(_cosmos.Object);

        (await router.GetMomentumAsync("demo-1", CancellationToken.None)).Should().BeEmpty();
        (await router.GetMomentumAsync(_storedId, CancellationToken.None)).Should().BeEmpty();

        _demo.Verify(m => m.GetMomentumAsync("demo-1", It.IsAny<CancellationToken>()), Times.Once());
        _cosmos.Verify(m => m.GetMomentumAsync(_storedId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Test]
    public async Task Without_cosmos_db_every_id_goes_to_the_recording()
    {
        _demo.Setup(m => m.GetMomentumAsync(_storedId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<Touchline.Domain.Analytics.MomentumReading>?)null);

        (await Router(cosmos: null).GetMomentumAsync(_storedId, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task The_match_list_merges_both_sources_live_first_then_newest()
    {
        var kickOff = new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero);
        _demo.Setup(m => m.ListMatchesAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchList([Summary("demo-1", MatchStatus.FullTime, kickOff.AddHours(2))]));
        _cosmos.Setup(m => m.ListMatchesAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchList([Summary("old", MatchStatus.FullTime, kickOff), Summary("live", MatchStatus.Live, kickOff.AddHours(-1))]));

        var matches = (await Router(_cosmos.Object).ListMatchesAsync(null, null, CancellationToken.None)).Matches;

        matches.Select(m => m.Id).Should().Equal("live", "demo-1", "old");
    }

    [Test]
    public async Task With_cosmos_db_down_the_list_is_the_demo_alone_and_a_stored_id_is_unavailable()
    {
        var demoMatch = Summary("demo-1", MatchStatus.Live, DateTimeOffset.UnixEpoch);
        _demo.Setup(m => m.ListMatchesAsync(MatchStatus.Live, null, It.IsAny<CancellationToken>())).ReturnsAsync(new MatchList([demoMatch]));
        _demo.Setup(m => m.GetMomentumAsync("demo-1", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _cosmos.Setup(m => m.ListMatchesAsync(It.IsAny<MatchStatus?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));
        _cosmos.Setup(m => m.GetMomentumAsync(_storedId, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("unreachable"));
        var router = Router(_cosmos.Object);

        (await router.ListMatchesAsync(MatchStatus.Live, null, CancellationToken.None)).Should().BeEquivalentTo(new MatchList([demoMatch], Degraded: true));
        (await router.GetMomentumAsync("demo-1", CancellationToken.None)).Should().BeEmpty();
        var stored = () => router.GetMomentumAsync(_storedId, CancellationToken.None);
        (await stored.Should().ThrowAsync<MatchSourceUnavailableException>()).WithInnerException<HttpRequestException>();
    }

    [Test]
    public async Task A_stream_fails_fast_when_the_change_feed_never_starts()
    {
        var time = new FakeTimeProvider();
        var feed = new RoutingLiveFeed(
            Mock.Of<ILiveFeed>(),
            new CosmosLiveFeed(Hubs(time, TimeSpan.FromSeconds(10))),
            NullLogger<RoutingLiveFeed>.Instance);

        var prepare = feed.PrepareAsync(_storedId, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(20);
        prepare.IsCompleted.Should().BeFalse("the wait is ten seconds");

        time.Advance(TimeSpan.FromSeconds(1));

        var waited = () => prepare.WaitAsync(TimeSpan.FromSeconds(5));
        (await waited.Should().ThrowAsync<MatchSourceUnavailableException>()).WithInnerException<TimeoutException>();
    }

    [Test]
    public void A_cosmos_connection_is_recognised_however_it_is_configured()
    {
        MatchSources.IsCosmosConfigured(Config()).Should().BeFalse();
        MatchSources.IsCosmosConfigured(Config(("ConnectionStrings:cosmos", "AccountEndpoint=https://x;"))).Should().BeTrue();
        MatchSources.IsCosmosConfigured(Config(("Aspire:Microsoft:Azure:Cosmos:AccountEndpoint", "https://x"))).Should().BeTrue();
        MatchSources.IsCosmosConfigured(Config(("Aspire:Microsoft:Azure:Cosmos:cosmos:ConnectionString", "AccountEndpoint=https://x;"))).Should().BeTrue();
    }

    [Test]
    public async Task With_no_cosmos_connection_the_app_is_demo_only()
    {
        await using var factory = MatchCentreFactory.Live();
        using var client = factory.CreateClient();

        factory.Services.GetKeyedService<IMatchReadModel>(MatchSources.Cosmos).Should().BeNull();
        factory.Services.GetService<LiveMatchHubs>().Should().BeNull();
        (await MatchIdsAsync(client)).Should().NotBeEmpty().And.OnlyContain(id => id.StartsWith("demo-", StringComparison.Ordinal));
        using var stored = await client.GetAsync(new Uri($"/api/matches/{_storedId}", UriKind.Relative));
        stored.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task With_cosmos_db_down_the_demo_still_serves_and_stored_ids_answer_503()
    {
        var failing = new Mock<IMatchReadModel>();
        failing.Setup(m => m.ListMatchesAsync(It.IsAny<MatchStatus?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("down", HttpStatusCode.ServiceUnavailable, 0, "", 0));
        failing.Setup(m => m.GetMatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("down", HttpStatusCode.ServiceUnavailable, 0, "", 0));
        await using var factory = WithCosmos(failing.Object, Mock.Of<ILiveFeed>());
        using var client = factory.CreateClient();

        var ids = await MatchIdsAsync(client);
        ids.Should().NotBeEmpty().And.OnlyContain(id => id.StartsWith("demo-", StringComparison.Ordinal));
        using var demoMatch = await client.GetAsync(new Uri($"/api/matches/{ids[0]}", UriKind.Relative));
        demoMatch.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stored = await client.GetAsync(new Uri($"/api/matches/{_storedId}", UriKind.Relative));
        stored.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stored.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task A_list_served_without_cosmos_db_says_so_in_a_header()
    {
        var failing = new Mock<IMatchReadModel>();
        failing.Setup(m => m.ListMatchesAsync(It.IsAny<MatchStatus?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("down", HttpStatusCode.ServiceUnavailable, 0, "", 0));
        await using var factory = WithCosmos(failing.Object, Mock.Of<ILiveFeed>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/matches", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(DegradedSources.Header).Should().Equal(DegradedSources.Cosmos);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Test]
    public async Task A_full_list_has_no_degraded_header()
    {
        var healthy = new Mock<IMatchReadModel>();
        healthy.Setup(m => m.ListMatchesAsync(It.IsAny<MatchStatus?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new MatchList([]));
        await using var factory = WithCosmos(healthy.Object, Mock.Of<ILiveFeed>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/matches", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(DegradedSources.Header).Should().BeFalse();
    }

    [Test]
    public async Task A_stored_match_whose_change_feed_never_starts_answers_503_instead_of_hanging()
    {
        await using var demoOnly = MatchCentreFactory.Live();
        var demo = demoOnly.Services.GetRequiredKeyedService<IMatchReadModel>(MatchSources.Demo);
        var demoId = (await demo.ListMatchesAsync(null, null, CancellationToken.None)).Matches[0].Id;
        var detail = await demo.GetMatchAsync(demoId, CancellationToken.None);
        var stored = new Mock<IMatchReadModel>();
        stored.Setup(m => m.GetMatchAsync(_storedId, It.IsAny<CancellationToken>())).ReturnsAsync(detail);
        await using var factory = WithCosmos(stored.Object, new CosmosLiveFeed(Hubs(TimeProvider.System, TimeSpan.FromMilliseconds(50))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/api/matches/{_storedId}/stream", UriKind.Relative)).WaitAsync(TimeSpan.FromSeconds(10));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task A_bug_in_cosmos_db_code_is_a_500_not_a_503()
    {
        var broken = new Mock<IMatchReadModel>();
        broken.Setup(m => m.GetMatchAsync(_storedId, It.IsAny<CancellationToken>())).ThrowsAsync(new JsonException("bad document"));
        await using var factory = WithCosmos(broken.Object, Mock.Of<ILiveFeed>());
        using var client = factory.CreateClient();

        using var stored = await client.GetAsync(new Uri($"/api/matches/{_storedId}", UriKind.Relative));

        stored.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Only_an_outage_degrades_the_match_list()
    {
        _demo.Setup(m => m.ListMatchesAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(new MatchList([]));
        _cosmos.Setup(m => m.ListMatchesAsync(null, null, It.IsAny<CancellationToken>())).ThrowsAsync(new ArgumentException("bug"));

        var list = () => Router(_cosmos.Object).ListMatchesAsync(null, null, CancellationToken.None);

        await list.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public void Outages_are_told_apart_from_bugs()
    {
        MatchSources.IsOutage(new CosmosException("busy", HttpStatusCode.TooManyRequests, 0, "", 0), CancellationToken.None).Should().BeTrue();
        MatchSources.IsOutage(new CosmosException("down", HttpStatusCode.ServiceUnavailable, 0, "", 0), CancellationToken.None).Should().BeTrue();
        MatchSources.IsOutage(new CosmosException("missing", HttpStatusCode.NotFound, 0, "", 0), CancellationToken.None).Should().BeFalse();
        MatchSources.IsOutage(new TimeoutException(), CancellationToken.None).Should().BeTrue();
        MatchSources.IsOutage(new OperationCanceledException(), CancellationToken.None).Should().BeTrue();
        MatchSources.IsOutage(new OperationCanceledException(), new CancellationToken(canceled: true)).Should().BeFalse();
        MatchSources.IsOutage(new JsonException(), CancellationToken.None).Should().BeFalse();
    }

    [Test]
    public async Task A_stream_that_loses_cosmos_db_after_it_started_logs_one_warning_and_ends()
    {
        var cosmos = new Mock<ILiveFeed>();
        cosmos.Setup(f => f.FollowAsync(_storedId, null, Viewer.Default, It.IsAny<CancellationToken>())).Returns(FailingStream());
        var logger = new ListLogger<RoutingLiveFeed>();
        var feed = new RoutingLiveFeed(Mock.Of<ILiveFeed>(), cosmos.Object, logger);

        var messages = new List<LiveMessage>();
        await foreach (var message in feed.FollowAsync(_storedId, null, Viewer.Default, CancellationToken.None))
        {
            messages.Add(message);
        }

        messages.Select(m => m.Event).Should().Equal("state");
        logger.Levels.Should().Equal(LogLevel.Warning);
    }

    [Test]
    public async Task After_a_feed_start_timeout_streams_fail_at_once_for_thirty_seconds_then_one_probe_waits()
    {
        var time = new FakeTimeProvider();
        var hubs = Hubs(time, TimeSpan.FromSeconds(10));
        var first = hubs.GetAsync(_storedId, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await ((Func<Task>)(() => first.WaitAsync(TimeSpan.FromSeconds(5)))).Should().ThrowAsync<TimeoutException>();

        hubs.GetAsync(_storedId, CancellationToken.None).IsFaulted.Should().BeTrue("the last wait timed out moments ago");
        time.Advance(TimeSpan.FromSeconds(29));
        hubs.GetAsync(_storedId, CancellationToken.None).IsFaulted.Should().BeTrue("the window is thirty seconds");

        time.Advance(TimeSpan.FromSeconds(1));
        var probe = hubs.GetAsync(_storedId, CancellationToken.None);
        await Task.Delay(20);
        probe.IsCompleted.Should().BeFalse("one stream may wait again");
        hubs.GetAsync(_storedId, CancellationToken.None).IsFaulted.Should().BeTrue("only one stream probes at a time");

        time.Advance(TimeSpan.FromSeconds(10));
        await ((Func<Task>)(() => probe.WaitAsync(TimeSpan.FromSeconds(5)))).Should().ThrowAsync<TimeoutException>();
        hubs.GetAsync(_storedId, CancellationToken.None).IsFaulted.Should().BeTrue("the failed probe opens a new window");
    }

    [Test]
    public async Task With_a_cosmos_connection_the_routers_and_the_change_feed_reader_are_composed()
    {
        await using var factory = MatchCentreFactory.Live().WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:cosmos", "AccountEndpoint=https://localhost:8081/;")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton(new Mock<CosmosClient>().Object);
                services.AddSingleton<IChangeFeedSource, NoChanges>();
                services.AddKeyedSingleton(MatchSources.Cosmos, new Mock<IMatchReadModel>(MockBehavior.Strict).Object);
                services.AddKeyedSingleton(MatchSources.Cosmos, new Mock<ILiveFeed>(MockBehavior.Strict).Object);
            }));
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<IMatchReadModel>().Should().BeOfType<RoutingMatchReadModel>();
        factory.Services.GetRequiredService<ILiveFeed>().Should().BeOfType<RoutingLiveFeed>();
        factory.Services.GetRequiredService<LiveMatchHubs>().Should().NotBeNull();
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(service => service is ChangeFeedReader);
        var demoId = (await factory.Services.GetRequiredKeyedService<IMatchReadModel>(MatchSources.Demo)
            .ListMatchesAsync(null, null, CancellationToken.None)).Matches[0].Id;
        demoId.Should().StartWith("demo-");
        using var demoMatch = await client.GetAsync(new Uri($"/api/matches/{demoId}", UriKind.Relative));
        demoMatch.StatusCode.Should().Be(HttpStatusCode.OK, "a demo ID never reaches the strict Cosmos DB mock");
    }

    private static async IAsyncEnumerable<LiveMessage> FailingStream()
    {
        yield return new LiveMessage("state", "1", new object());
        await Task.Yield();
        throw new HttpRequestException("Cosmos DB went away");
    }

    private static LiveMatchHubs Hubs(TimeProvider time, TimeSpan startTimeout)
    {
        var commentary = new Mock<ICommentaryStore>();
        var detector = new KeyMomentDetector(teams: TeamProfileCatalog.LoadEmbedded().Values);
        var options = Options.Create(new LiveFeedOptions { StartTimeout = startTimeout });
        return new LiveMatchHubs(new Mock<CosmosClient>().Object, commentary.Object, detector, time, NullLogger<LiveMatchHubs>.Instance, options: options);
    }

    private sealed class NoChanges : IChangeFeedSource
    {
        public Task<IReadOnlyList<JsonElement>> ReadChangesAsync(string container, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonElement>>([]);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private static WebApplicationFactory<Program> WithCosmos(IMatchReadModel readModel, ILiveFeed feed) =>
        MatchCentreFactory.Live().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton(MatchSources.Cosmos, readModel);
            services.AddKeyedSingleton(MatchSources.Cosmos, feed);
        }));

    private static async Task<IReadOnlyList<string>> MatchIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/matches", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.EnumerateArray().Select(match => match.GetProperty("id").GetString()!)];
    }

    private RoutingMatchReadModel Router(IMatchReadModel? cosmos) =>
        new(_demo.Object, cosmos, NullLogger<RoutingMatchReadModel>.Instance);

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    private static MatchSummary Summary(string id, MatchStatus status, DateTimeOffset kickOff)
    {
        var team = new MatchTeam("t", "Team", "TEA", "/crests/t.svg");
        return new MatchSummary(id, MatchKind.Exhibition, status, team, team, new Score(0, 0), new ClockReading(MatchPeriod.FirstHalf, 0, 0, "0'"), kickOff, null, null, "synthetic");
    }
}
