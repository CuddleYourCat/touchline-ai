using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>
/// The fan-match limits (ADR-032, ADR-050): ten a day, one live, the <c>Idempotency-Key</c> and
/// refunds. The limit store is the real Cosmos DB one over an in-memory container that honours
/// ETags; the requests and the job are mocked.
/// </summary>
[TestFixture]
public class FanMatchLimitsTests
{
    private const string _fanId = "fan-test-rovers";
    private static readonly FanTeamSpec _fan = TestFanTeams.Standard(_fanId, ManagerStyle.Counter);
    private static readonly MatchRequest _match = new(_fanId, "north-bank-fc");

    private readonly Dictionary<string, JsonObject> _documents = [];
    private readonly Dictionary<string, FanMatchRequest> _requests = [];
    private FakeTimeProvider _time = null!;
    private int _etags;
    private Func<string, ResponseMessage?> _beforeReplace = _ => null;
    private Mock<Container> _container = null!;
    private CosmosFanMatchLimitStore _limits = null!;
    private Mock<IFanMatchRequestStore> _requestStore = null!;
    private Mock<ISimulatorJob> _job = null!;
    private Mock<ITeamStore> _teams = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
        _documents.Clear();
        _requests.Clear();
        _beforeReplace = _ => null;
        _container = InMemoryContainer();
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.MatchRequests)).Returns(_container.Object);
        _limits = new CosmosFanMatchLimitStore(client.Object);

        _requestStore = new Mock<IFanMatchRequestStore>();
        _requestStore.Setup(r => r.CreateAsync(It.IsAny<FanMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FanMatchRequest request, CancellationToken _) => _requests.TryAdd(request.Id, request));
        _requestStore.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => _requests.GetValueOrDefault(id));
        _requestStore.Setup(r => r.ListActiveAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset since, CancellationToken _) =>
                [.. _requests.Values.Where(r => r.Status is FanMatchStatus.Scheduled or FanMatchStatus.Running && r.CreatedUtc >= since)]);
        _requestStore.Setup(r => r.SetStatusAsync(It.IsAny<string>(), It.IsAny<FanMatchStatus>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, FanMatchStatus status, string? reason, IReadOnlyCollection<FanMatchStatus> from, CancellationToken _) =>
            {
                if (!_requests.TryGetValue(id, out var current) || !from.Contains(current.Status))
                {
                    return false;
                }

                _requests[id] = current with { Status = status, Reason = reason };
                return true;
            });

        _job = new Mock<ISimulatorJob>();
        _teams = new Mock<ITeamStore>();
        _teams.Setup(t => t.GetAsync(_fanId, It.IsAny<CancellationToken>())).ReturnsAsync(new StoredTeam(FanMatchLauncherTests.Detail(_fan), "hash"));
        _teams.Setup(t => t.GetAsync("north-bank-fc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredTeam(TeamViews.FromProfile(TeamProfileCatalog.LoadEmbedded()["north-bank-fc"]), null));
    }

    [Test]
    public async Task The_eleventh_match_of_a_day_is_busy_until_midnight_UTC_and_a_new_day_resets()
    {
        for (var i = 0; i < 10; i++)
        {
            (await PlayToFullTimeAsync()).Outcome.Should().Be(LaunchOutcome.Accepted);
        }

        var eleventh = await Launcher().LaunchAsync(_match, null, CancellationToken.None);

        eleventh.Outcome.Should().Be(LaunchOutcome.Busy);
        eleventh.RetryAfter.Should().Be(TimeSpan.FromHours(15), "it's 09:00 UTC");
        eleventh.Reason.Should().Be("Today's ten fan matches have been played. The next slot opens at midnight UTC.");
        DayCount().Should().Be(10);
        _job.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(10));

        _time.Advance(TimeSpan.FromHours(15));
        (await Launcher().LaunchAsync(_match, null, CancellationToken.None)).Outcome.Should().Be(LaunchOutcome.Accepted);
    }

    [Test]
    public async Task One_live_fan_match_blocks_a_second_and_a_stale_scheduled_one_does_not()
    {
        var first = await Launcher().LaunchAsync(_match, null, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        var second = await Launcher().LaunchAsync(_match, null, CancellationToken.None);

        second.Outcome.Should().Be(LaunchOutcome.Busy);
        second.Reason.Should().Be(FanMatchLauncher.LiveReason);
        second.RetryAfter.Should().Be(TimeSpan.FromMinutes(8), "the first started two minutes ago and a match takes about ten");
        DayCount().Should().Be(1, "a refused launch spends no slot");

        _time.Advance(TimeSpan.FromMinutes(2));
        var third = await Launcher().LaunchAsync(_match, null, CancellationToken.None);

        third.Outcome.Should().Be(LaunchOutcome.Accepted, "nothing picked the first up within three minutes");
        _requests[first.Match!.MatchId].Status.Should().Be(FanMatchStatus.Failed);
    }

    [Test]
    public async Task A_running_match_near_its_end_still_asks_for_at_least_thirty_seconds()
    {
        var first = await Launcher().LaunchAsync(_match, null, CancellationToken.None);
        _requests[first.Match!.MatchId] = _requests[first.Match.MatchId] with { Status = FanMatchStatus.Running };
        _time.Advance(TimeSpan.FromMinutes(12));

        (await Launcher().LaunchAsync(_match, null, CancellationToken.None)).RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task The_same_idempotency_key_twice_gives_one_request_one_job_start_and_one_slot()
    {
        var first = await Launcher().LaunchAsync(_match, "click-7f3a", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await Launcher().LaunchAsync(_match, "click-7f3a", CancellationToken.None);

        first.Outcome.Should().Be(LaunchOutcome.Accepted);
        second.Should().BeEquivalentTo(first, "a repeat gets the same 202 and body");
        _requests.Should().ContainSingle();
        _job.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        DayCount().Should().Be(1);
        _documents.Keys.Should().Contain(CosmosFanMatchLimitStore.KeyId("click-7f3a"))
            .And.NotContain(k => k.Contains("click-7f3a"), "the key itself is never stored");
    }

    [Test]
    public async Task A_repeat_while_the_first_is_still_being_written_gets_the_same_match()
    {
        var claimedId = FanMatchIds.New(_time.GetUtcNow());
        (await _limits.TryClaimKeyAsync("click-in-flight", claimedId, _time.GetUtcNow(), CancellationToken.None)).Should().BeNull();

        var repeat = await Launcher().LaunchAsync(_match, "click-in-flight", CancellationToken.None);

        repeat.Match!.MatchId.Should().Be(claimedId);
        _job.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        DayCount().Should().Be(0);
    }

    [Test]
    public async Task A_failed_start_refunds_the_slot_and_a_retry_with_the_same_key_starts_afresh()
    {
        _job.SetupSequence(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Azure.RequestFailedException(500, "boom"))
            .Returns(Task.CompletedTask);

        var failed = await Launcher().LaunchAsync(_match, "click-retry", CancellationToken.None);

        failed.Outcome.Should().Be(LaunchOutcome.Unavailable);
        DayCount().Should().Be(0, "the slot is given back");
        _requests.Values.Should().ContainSingle().Which.Status.Should().Be(FanMatchStatus.Failed);

        var retried = await Launcher().LaunchAsync(_match, "click-retry", CancellationToken.None);

        retried.Outcome.Should().Be(LaunchOutcome.Accepted);
        _requests.Should().HaveCount(2);
        DayCount().Should().Be(1);
    }

    [Test]
    public async Task A_refund_that_cannot_be_written_is_logged_and_never_thrown()
    {
        var limits = new Mock<IFanMatchLimitStore>();
        limits.Setup(l => l.TryTakeDaySlotAsync(It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        limits.Setup(l => l.ReturnDaySlotAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        _job.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Azure.RequestFailedException(500, "boom"));
        var logger = new RecordingLogger();

        var launch = await Launcher(limits.Object, logger).LaunchAsync(_match, null, CancellationToken.None);

        launch.Outcome.Should().Be(LaunchOutcome.Unavailable);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Message.Contains("couldn't be given back") && e.Exception is HttpRequestException);
    }

    [Test]
    public async Task Two_writers_at_once_each_count_once()
    {
        await _limits.TryTakeDaySlotAsync(Today(), 5, CancellationToken.None);
        var raced = false;

        // The first replace loses to another writer, who takes a slot between our read and our write.
        _beforeReplace = id =>
        {
            if (raced)
            {
                return null;
            }

            raced = true;
            var current = _documents[id];
            current["count"] = current["count"]!.GetValue<int>() + 1;
            current["_etag"] = $"\"e{++_etags}\"";
            return new ResponseMessage(HttpStatusCode.PreconditionFailed);
        };

        (await _limits.TryTakeDaySlotAsync(Today(), 5, CancellationToken.None)).Should().BeTrue();

        DayCount().Should().Be(3, "the first slot, the other writer's and ours, each once");
    }

    [Test]
    public async Task A_refund_never_goes_below_zero()
    {
        await _limits.ReturnDaySlotAsync(Today(), CancellationToken.None);
        await _limits.TryTakeDaySlotAsync(Today(), 5, CancellationToken.None);
        await _limits.ReturnDaySlotAsync(Today(), CancellationToken.None);
        await _limits.ReturnDaySlotAsync(Today(), CancellationToken.None);

        DayCount().Should().Be(0);
    }

    [Test]
    public async Task Day_counters_and_keys_clear_themselves()
    {
        await Launcher().LaunchAsync(_match, "click-ttl", CancellationToken.None);

        _documents[CosmosFanMatchLimitStore.DayId(Today())]["ttl"]!.GetValue<int>().Should().Be((int)TimeSpan.FromDays(3).TotalSeconds);
        _documents[CosmosFanMatchLimitStore.KeyId("click-ttl")]["ttl"]!.GetValue<int>().Should().Be((int)TimeSpan.FromDays(1).TotalSeconds);
    }

    [Test]
    public async Task A_client_that_goes_away_during_the_request_write_still_gets_its_match_started()
    {
        using var caller = new CancellationTokenSource();
        _requestStore.Setup(r => r.CreateAsync(It.IsAny<FanMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FanMatchRequest request, CancellationToken token) =>
            {
                caller.Cancel();
                token.ThrowIfCancellationRequested();
                return _requests.TryAdd(request.Id, request);
            });

        var launch = await Launcher().LaunchAsync(_match, "click-abort-write", caller.Token);

        launch.Outcome.Should().Be(LaunchOutcome.Accepted, "once the slot is taken the launch finishes");
        _job.Verify(j => j.StartAsync(launch.Match!.MatchId, It.Is<CancellationToken>(t => !t.IsCancellationRequested)), Times.Once);
        DayCount().Should().Be(1);
        KeyPointsAtARealRequest("click-abort-write").Should().BeTrue();
    }

    [Test]
    public async Task A_client_that_goes_away_during_the_job_start_either_starts_or_is_refunded_never_neither()
    {
        using var caller = new CancellationTokenSource();
        _job.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken token) =>
            {
                caller.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var started = await Launcher().LaunchAsync(_match, "click-abort-start", caller.Token);

        started.Outcome.Should().Be(LaunchOutcome.Accepted);
        DayCount().Should().Be(1);
        _requests[started.Match!.MatchId].Status.Should().Be(FanMatchStatus.Scheduled);
        KeyPointsAtARealRequest("click-abort-start").Should().BeTrue();

        // And when the start itself is cancelled underneath (its own deadline), the slot comes back.
        _time.Advance(TimeSpan.FromHours(1));
        _requests[started.Match.MatchId] = _requests[started.Match.MatchId] with { Status = FanMatchStatus.Finished };
        _job.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException("deadline"));

        var cancelled = await Launcher().LaunchAsync(_match, "click-abort-start-2", CancellationToken.None);

        cancelled.Outcome.Should().Be(LaunchOutcome.Unavailable);
        DayCount().Should().Be(1, "the second slot was refunded");
        _requests.Values.Should().Contain(r => r.Status == FanMatchStatus.Failed);
        KeyPointsAtARealRequest("click-abort-start-2").Should().BeTrue("the key stays on the failed request, so a retry starts afresh");
    }

    [Test]
    public async Task A_client_that_goes_away_before_the_slot_aborts_and_lets_its_key_go()
    {
        using var caller = new CancellationTokenSource();
        _requestStore.Setup(r => r.ListActiveAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((DateTimeOffset _, CancellationToken token) =>
            {
                caller.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<FanMatchRequest>>([]);
            });

        var act = () => Launcher().LaunchAsync(_match, "click-abort-early", caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        DayCount().Should().Be(0);
        _requests.Should().BeEmpty();
        _documents.Should().NotContainKey(CosmosFanMatchLimitStore.KeyId("click-abort-early"));
        _job.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Chaos_a_counter_store_that_is_down_answers_unavailable_and_starts_nothing()
    {
        var limits = new Mock<IFanMatchLimitStore>();
        limits.Setup(l => l.TryTakeDaySlotAsync(It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("unavailable", HttpStatusCode.ServiceUnavailable, 0, "a", 0));

        var launch = await Launcher(limits.Object).LaunchAsync(_match, null, CancellationToken.None);

        launch.Outcome.Should().Be(LaunchOutcome.Unavailable);
        _requests.Should().BeEmpty();
        _job.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task<MatchLaunch> PlayToFullTimeAsync()
    {
        var launch = await Launcher().LaunchAsync(_match, null, CancellationToken.None);
        if (launch.Match is { } match)
        {
            _requests[match.MatchId] = _requests[match.MatchId] with { Status = FanMatchStatus.Finished };
        }

        return launch;
    }

    private bool KeyPointsAtARealRequest(string key) =>
        _documents.TryGetValue(CosmosFanMatchLimitStore.KeyId(key), out var claim)
        && _requests.ContainsKey(claim["requestId"]!.GetValue<string>());

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    private int DayCount() =>
        _documents.TryGetValue(CosmosFanMatchLimitStore.DayId(Today()), out var counter) ? counter["count"]!.GetValue<int>() : 0;

    private FanMatchLauncher Launcher(IFanMatchLimitStore? limits = null, ILogger<FanMatchLauncher>? logger = null) => new(
        _teams.Object, _requestStore.Object, limits ?? _limits, _job.Object, _time, Options.Create(new FanMatchOptions()),
        logger ?? NullLogger<FanMatchLauncher>.Instance);

    private Mock<Container> InMemoryContainer()
    {
        var container = new Mock<Container>();
        container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
                _documents.TryGetValue(id, out var document)
                    ? new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document)) }
                    : new ResponseMessage(HttpStatusCode.NotFound));
        container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
            {
                var document = JsonNode.Parse(content)!.AsObject();
                var id = document["id"]!.GetValue<string>();
                if (_documents.ContainsKey(id))
                {
                    return new ResponseMessage(HttpStatusCode.Conflict);
                }

                Store(id, document);
                return new ResponseMessage(HttpStatusCode.Created);
            });
        container.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, string id, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                if (_beforeReplace(id) is { } interrupted)
                {
                    return interrupted;
                }

                if (!_documents.TryGetValue(id, out var current))
                {
                    return new ResponseMessage(HttpStatusCode.NotFound);
                }

                if (options?.IfMatchEtag is { } expected && expected != current["_etag"]!.GetValue<string>())
                {
                    return new ResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                Store(id, JsonNode.Parse(content)!.AsObject());
                return new ResponseMessage(HttpStatusCode.OK);
            });
        container.Setup(c => c.DeleteItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
                _documents.Remove(id) ? new ResponseMessage(HttpStatusCode.NoContent) : new ResponseMessage(HttpStatusCode.NotFound));
        return container;
    }

    private sealed class RecordingLogger : ILogger<FanMatchLauncher>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private void Store(string id, JsonObject document)
    {
        document["_etag"] = $"\"e{++_etags}\"";
        _documents[id] = document;
    }
}
