extern alias simulator;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using simulator::Touchline.Simulator.Publishing;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.Storage;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>The simulator job plays a fan match request (ADR-050).</summary>
[TestFixture]
public class FanMatchRequestPlayTests
{
    private const string _requestId = "exh-202610241530-1a2b3c4d";
    private static readonly FanTeamSpec _fan = TestFanTeams.Standard("fan-test-rovers", ManagerStyle.Direct);

    // The second keeper starts and the first sits on the bench, so the eleven is plainly the sheet's, not the default pick.
    private static readonly MatchdaySheet _sheet = new(
        [_fan.Players[11].Id, .. _fan.Players.Skip(1).Take(10).Select(p => p.Id)],
        [_fan.Players[0].Id, _fan.Players[12].Id]);

    private static readonly FanMatchRequest _request = new(
        _requestId,
        new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.Zero),
        Seed: 9001,
        KickOffUtc: new DateTimeOffset(2026, 10, 24, 15, 31, 0, TimeSpan.Zero),
        new FanMatchSide(_fan.Id, _fan, _sheet),
        new FanMatchSide("north-bank-fc", null, null),
        FanMatchStatus.Scheduled);

    private Mock<IFanMatchRequestStore> _store = null!;
    private List<FanMatchStatus> _statuses = null!;

    [SetUp]
    public void CreateStore()
    {
        _statuses = [];
        _store = new Mock<IFanMatchRequestStore>();
        _store.Setup(s => s.GetAsync(_requestId, It.IsAny<CancellationToken>())).ReturnsAsync(_request);
        _store.Setup(s => s.SetStatusAsync(_requestId, It.IsAny<FanMatchStatus>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, FanMatchStatus status, string? _, IReadOnlyCollection<FanMatchStatus> _, CancellationToken _) => _statuses.Add(status))
            .ReturnsAsync(true);
    }

    [Test]
    public async Task A_fan_request_plays_its_sheet_seed_and_match_id_and_moves_from_running_to_finished()
    {
        var sender = new RecordingSender();

        await Publisher(sender).PlayRequestAsync(_requestId, 1000, CancellationToken.None);

        sender.Events.Should().NotBeEmpty();
        sender.Events.Should().OnlyContain(e => e.MatchId == _requestId);
        var metadata = sender.Events[0].Metadata!;
        metadata.Kind.Should().Be(MatchKind.Exhibition);
        metadata.KickOffUtc.Should().Be(_request.KickOffUtc);
        metadata.Home.Id.Should().Be(_fan.Id);
        metadata.Home.StartingEleven.Should().BeEquivalentTo(_sheet.StartingEleven);
        metadata.Home.StartingEleven[0].Should().Be(_fan.Players[11].Id, "the sheet's keeper starts");
        sender.Events.Where(e => e.Team == TeamSide.Home && e.PlayerId is not null)
            .Select(e => e.PlayerId!).Should().NotContain(_sheet.Substitutes, "the bench is stored, not played");
        _statuses.Should().Equal(FanMatchStatus.Running, FanMatchStatus.Finished);
        _store.Verify(s => s.SetStatusAsync(_requestId, FanMatchStatus.Running, null,
            It.Is<IReadOnlyCollection<FanMatchStatus>>(from => from.SequenceEqual(new[] { FanMatchStatus.Scheduled })), It.IsAny<CancellationToken>()));
    }

    [Test]
    public async Task A_request_replays_exactly()
    {
        var first = new RecordingSender();
        var second = new RecordingSender();

        await Publisher(first).PlayRequestAsync(_requestId, 1000, CancellationToken.None);
        await Publisher(second).PlayRequestAsync(_requestId, 1000, CancellationToken.None);

        second.Events.Should().BeEquivalentTo(first.Events, options => options.WithStrictOrdering(), "the request's seed and teams fix the match");
        first.Events.Should().BeEquivalentTo(
            FanMatchPlans.CreateSimulator(_request).Play().ToList(), options => options.WithStrictOrdering(),
            "the job publishes exactly what the engine plays");
    }

    [Test]
    public async Task A_missing_request_fails_cleanly_publishing_nothing_and_writing_no_status()
    {
        var sender = new RecordingSender();
        _store.Setup(s => s.GetAsync(_requestId, It.IsAny<CancellationToken>())).ReturnsAsync((FanMatchRequest?)null);

        var act = () => Publisher(sender).PlayRequestAsync(_requestId, 1000, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no fan match request*");
        sender.Events.Should().BeEmpty();
        _statuses.Should().BeEmpty();
    }

    [Test]
    public async Task A_request_that_is_not_scheduled_is_not_played_again()
    {
        var sender = new RecordingSender();
        _store.Setup(s => s.SetStatusAsync(_requestId, FanMatchStatus.Running, null, It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => Publisher(sender).PlayRequestAsync(_requestId, 1000, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        sender.Events.Should().BeEmpty();
    }

    [Test]
    public async Task Chaos_a_send_that_fails_marks_the_request_failed_with_a_short_reason()
    {
        var act = () => Publisher(new FailingSender()).PlayRequestAsync(_requestId, 1000, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _statuses.Should().Equal(FanMatchStatus.Running, FanMatchStatus.Failed);
        _store.Verify(s => s.SetStatusAsync(_requestId, FanMatchStatus.Failed, "The match failed to publish (HttpRequestException).",
            It.IsAny<IReadOnlyCollection<FanMatchStatus>>(), It.IsAny<CancellationToken>()));
    }

    [Test]
    public async Task A_job_run_with_a_request_id_plays_the_request_and_not_the_configured_matches()
    {
        var sender = new RecordingSender();
        var options = new SimulatorOptions { RequestId = _requestId, RequestSpeed = 1000, ExitWhenDone = true, Matches = [new MatchPlan { Seed = 42 }] };
        var publisher = Publisher(sender, options);

        await publisher.StartAsync(CancellationToken.None);
        await publisher.ExecuteTask!;

        sender.Events.Select(e => e.MatchId).Distinct().Should().Equal(_requestId);
    }

    [Test]
    public void A_request_id_alone_is_a_valid_configuration()
    {
        new SimulatorOptionsValidator().Validate(null, new SimulatorOptions { RequestId = _requestId }).Succeeded.Should().BeTrue();
        new SimulatorOptionsValidator().Validate(null, new SimulatorOptions { RequestId = "not-a-request" }).Failed.Should().BeTrue();
    }

    private MatchPublisher Publisher(IMatchEventSender sender, SimulatorOptions? options = null) => new(
        Options.Create(options ?? new SimulatorOptions()),
        sender,
        new InstantTimeProvider(),
        Mock.Of<ISeasonReader>(),
        Mock.Of<IHostApplicationLifetime>(),
        NullLogger<MatchPublisher>.Instance,
        _store.Object);

    private sealed class RecordingSender : IMatchEventSender
    {
        public List<MatchEvent> Events { get; } = [];

        public Task SendAsync(IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSender : IMatchEventSender
    {
        public Task SendAsync(IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Event Hubs is unavailable.");
    }

    // Completes every wait at once.
    private sealed class InstantTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FiredTimer(callback, state);

        private sealed class FiredTimer : ITimer
        {
            public FiredTimer(TimerCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ => callback(state));

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
