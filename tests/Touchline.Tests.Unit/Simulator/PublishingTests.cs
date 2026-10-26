extern alias simulator;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using simulator::Touchline.Simulator.Engine;
using simulator::Touchline.Simulator.Publishing;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>The simulator worker's side of ADR-023: pacing, the message, batching and the publisher.</summary>
[TestFixture]
public class PublishingTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private static List<MatchEvent> Match(int seed = 42) =>
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], seed).Play()];

    // Kick-off time

    [Test]
    public void An_exhibition_without_a_kick_off_time_kicks_off_at_the_clock_s_now()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 18, 30, 0, TimeSpan.Zero));

        var metadata = new MatchPlan { Seed = 42 }.CreateSimulator(time).Play().First().Metadata!;

        metadata.KickOffUtc.Should().Be(time.GetUtcNow());
    }

    [TestCase(MatchKind.Fixture, null)]
    [TestCase(MatchKind.Replay, "match-original")]
    public void A_fixture_or_replay_without_a_kick_off_time_keeps_the_engine_s_default(MatchKind kind, string? replayOf)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 18, 30, 0, TimeSpan.Zero));
        var plan = new MatchPlan { Seed = 42, Kind = kind, Season = "2026-27", Round = 3, ReplayOf = replayOf };

        var metadata = plan.CreateSimulator(time).Play().First().Metadata!;

        metadata.KickOffUtc.Should().Be(MatchSimulator.DefaultKickOffUtc);
    }

    [Test]
    public void A_fixture_with_a_kick_off_time_keeps_it()
    {
        var kickOff = new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);
        var plan = new MatchPlan { Seed = 42, Kind = MatchKind.Fixture, Season = "2026-27", Round = 3, KickOff = kickOff };

        var metadata = plan.CreateSimulator(new FakeTimeProvider()).Play().First().Metadata!;

        metadata.KickOffUtc.Should().Be(kickOff);
    }

    [Test]
    public void A_plan_with_a_kick_off_time_keeps_it()
    {
        var kickOff = new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

        var metadata = new MatchPlan { Seed = 42, KickOff = kickOff }.CreateSimulator(new FakeTimeProvider()).Play().First().Metadata!;

        metadata.KickOffUtc.Should().Be(kickOff);
    }

    // Pacing

    [Test]
    public void Pacing_keeps_every_event_in_order_and_groups_those_at_the_same_moment()
    {
        var match = Match();

        var batches = MatchPacer.Pace(match, speed: 10).ToList();

        batches.SelectMany(b => b.Events).Should().Equal(match);
        batches.Should().OnlyContain(b => b.Events.Select(e => e.Time.Clock).Distinct().Count() == 1);
    }

    [Test]
    public void Pacing_waits_the_match_clock_divided_by_the_speed_plus_the_half_time_pause()
    {
        var match = Match();
        var firstHalfEnd = match.Last(e => e.Time.Period == MatchPeriod.FirstHalf).Time.Clock;
        var secondHalf = match.Where(e => e.Time.Period == MatchPeriod.SecondHalf).ToList();
        var played = firstHalfEnd + (secondHalf[^1].Time.Clock - secondHalf[0].Time.Clock);

        var total = MatchPacer.Pace(match, speed: 10).Aggregate(TimeSpan.Zero, (sum, b) => sum + b.DelayBefore);

        total.TotalSeconds.Should().BeApproximately((played.TotalSeconds / 10) + 90, 0.001, "a match at 10x takes a tenth of its playing time, plus the 90 s half-time pause");
    }

    [TestCase(10, 90)]
    [TestCase(60, 15)]
    [TestCase(100, 15)]
    public void The_batch_after_the_first_half_ends_waits_the_half_time_pause(double speed, double seconds)
    {
        var batches = MatchPacer.Pace(Match(), speed).ToList();

        var afterHalfTime = batches[batches.FindIndex(b => b.Events.Any(IsFirstHalfEnd)) + 1];

        afterHalfTime.Events[0].Time.Period.Should().Be(MatchPeriod.SecondHalf);
        afterHalfTime.DelayBefore.TotalSeconds.Should().BeApproximately(seconds, 0.001);
    }

    [Test]
    public void The_pause_applies_when_the_first_half_ends_at_the_same_clock_reading_the_second_half_starts()
    {
        var template = Match().First(e => e.Time.Period == MatchPeriod.SecondHalf);
        var end = template with { Type = MatchEventType.PeriodEnd, Time = MatchTime.At(MatchPeriod.FirstHalf, 45) };
        var restart = template with { Time = MatchTime.At(MatchPeriod.SecondHalf, 45) };

        var batches = MatchPacer.Pace([end, restart], speed: 10).ToList();

        batches.Should().HaveCount(2);
        batches[1].DelayBefore.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Test]
    public void Every_other_gap_is_unchanged_by_the_half_time_pause()
    {
        var match = Match();
        var expected = new List<TimeSpan>();
        for (var i = 0; i < match.Count;)
        {
            var clock = match[i].Time.Clock;
            var end = i;
            while (end < match.Count && match[end].Time.Clock == clock)
            {
                end++;
            }

            expected.Add(expected.Count == 0 || match[i - 1].Time.Clock > clock ? TimeSpan.Zero : (clock - match[i - 1].Time.Clock) / 10);
            i = end;
        }

        var delays = MatchPacer.Pace(match, speed: 10).Select(b => b.DelayBefore).ToList();
        var halfTime = MatchPacer.Pace(match, speed: 10).ToList().FindIndex(b => b.Events.Any(IsFirstHalfEnd)) + 1;

        delays.Count.Should().Be(expected.Count);
        delays.Where((_, i) => i != halfTime).Should().Equal(expected.Where((_, i) => i != halfTime));
    }

    [Test]
    public void A_match_with_no_first_half_period_end_has_no_half_time_pause()
    {
        var match = Match().Where(e => !IsFirstHalfEnd(e)).ToList();
        var withPause = MatchPacer.Pace(Match(), speed: 10).Aggregate(TimeSpan.Zero, (sum, b) => sum + b.DelayBefore);

        var total = MatchPacer.Pace(match, speed: 10).Aggregate(TimeSpan.Zero, (sum, b) => sum + b.DelayBefore);

        MatchPacer.Pace(match, speed: 10).Max(b => b.DelayBefore).Should().BeLessThan(TimeSpan.FromSeconds(90));
        total.Should().BeLessThan(withPause);
    }

    private static bool IsFirstHalfEnd(MatchEvent e) => e is { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf };

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(double.NaN)]
    public void Pacing_rejects_a_speed_that_isn_t_positive_as_soon_as_it_s_asked(double speed)
    {
        var pace = () => MatchPacer.Pace(Match(), speed);

        pace.Should().Throw<ArgumentOutOfRangeException>("the check runs when Pace is called, not when the first batch is read");
    }

    // The message

    [Test]
    public void A_message_carries_the_event_as_json_and_its_envelope_as_properties()
    {
        var goal = Match().First(e => e.IsGoal);

        var message = EventHubsMatchEventSender.ToEventData(goal);

        message.MessageId.Should().Be($"{goal.MatchId}:{goal.Sequence}");
        message.ContentType.Should().Be("application/json");
        EventEnvelope.Check(message.Properties.AsReadOnly()).Should().Be(new EnvelopeCheck(null, goal.MatchId, goal.Sequence));
        JsonSerializer.Deserialize<MatchEvent>(message.EventBody.ToArray(), DomainJson.Options).Should().BeEquivalentTo(goal);
    }

    // Batching

    [Test]
    public async Task A_full_batch_is_sent_and_the_rest_go_in_a_new_one_with_nothing_lost()
    {
        var sent = new List<(string? PartitionKey, List<EventData> Events)>();
        var producer = new Mock<EventHubProducerClient>();
        producer.Setup(p => p.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateBatchOptions options, CancellationToken _) =>
            {
                var store = new List<EventData>();
                sent.Add((options.PartitionKey, store));
                // A batch with room for three messages.
                return EventHubsModelFactory.EventDataBatch(1024, store, options, _ => store.Count < 3);
            });
        var events = Match().Take(7).ToList();

        await new EventHubsMatchEventSender(producer.Object).SendAsync(events, CancellationToken.None);

        producer.Verify(p => p.SendAsync(It.IsAny<EventDataBatch>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        sent.Select(s => s.Events.Count).Should().Equal(3, 3, 1);
        sent.Should().OnlyContain(s => s.PartitionKey == events[0].MatchId, "a match's events share a partition, so they stay in order");
        sent.SelectMany(s => s.Events).Select(e => e.MessageId).Should().Equal(events.Select(e => $"{e.MatchId}:{e.Sequence}"));
    }

    [Test]
    public async Task Events_from_two_matches_can_t_be_sent_together()
    {
        var producer = new Mock<EventHubProducerClient>();
        producer.Setup(p => p.CreateBatchAsync(It.IsAny<CreateBatchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateBatchOptions options, CancellationToken _) => EventHubsModelFactory.EventDataBatch(1024, [], options));
        var mixed = new List<MatchEvent> { Match(1)[1], Match(2)[1] };

        var send = () => new EventHubsMatchEventSender(producer.Object).SendAsync(mixed, CancellationToken.None);

        await send.Should().ThrowAsync<ArgumentException>().WithMessage("*same match*");
    }

    // The publisher

    [Test]
    public async Task The_publisher_sends_a_whole_match_in_order_and_waits_as_the_pacing_says()
    {
        var sender = new RecordingSender();
        var time = new InstantTimeProvider();
        var plan = new MatchPlan { Seed = 42, Speed = 10, KickOff = MatchSimulator.DefaultKickOffUtc };
        var publisher = new MatchPublisher(Options.Create(new SimulatorOptions()), sender, time, new FakeSeason(), new RecordingLifetime(), NullLogger<MatchPublisher>.Instance);

        await publisher.PlayAsync(plan, CancellationToken.None);

        var expected = Match();
        sender.Events.Should().Equal(expected);
        time.TotalDelay.Should().Be(MatchPacer.Pace(expected, 10).Aggregate(TimeSpan.Zero, (sum, b) => sum + b.DelayBefore));
    }

    [Test]
    public async Task As_a_job_the_publisher_plays_every_match_then_stops_the_host()
    {
        var sender = new RecordingSender();
        var lifetime = new RecordingLifetime();
        var options = new SimulatorOptions { ExitWhenDone = true, Matches = [new MatchPlan { Seed = 1 }, new MatchPlan { Seed = 2 }] };

        await RunToCompletion(new MatchPublisher(Options.Create(options), sender, new InstantTimeProvider(), new FakeSeason(), lifetime, NullLogger<MatchPublisher>.Instance));

        sender.Events.Select(e => e.MatchId).Distinct().Should().HaveCount(2, "both matches are played before the job ends");
        lifetime.StopRequested.Should().BeTrue();
    }

    [Test]
    public async Task Locally_the_publisher_idles_after_its_matches()
    {
        var lifetime = new RecordingLifetime();
        var options = new SimulatorOptions { Matches = [new MatchPlan { Seed = 1 }] };

        await RunToCompletion(new MatchPublisher(Options.Create(options), new RecordingSender(), new InstantTimeProvider(), new FakeSeason(), lifetime, NullLogger<MatchPublisher>.Instance));

        lifetime.StopRequested.Should().BeFalse("under Aspire the worker stays up until the AppHost stops it");
    }

    [TestCase(typeof(InvalidOperationException))]
    [TestCase(typeof(TaskCanceledException))]
    public async Task A_job_whose_match_fails_still_stops_and_is_marked_as_failed(Type failure)
    {
        var lifetime = new RecordingLifetime();
        var options = new SimulatorOptions { ExitWhenDone = true, Matches = [new MatchPlan { Seed = 1 }] };
        var exitCode = Environment.ExitCode;
        try
        {
            await RunToCompletion(new MatchPublisher(Options.Create(options), new FailingSender(failure), new InstantTimeProvider(), new FakeSeason(), lifetime, NullLogger<MatchPublisher>.Instance));

            lifetime.StopRequested.Should().BeTrue("a failed execution must end, not hang until its replica timeout");
            // A timeout from the SDK is an OperationCanceledException too, but it's still a failure.
            Environment.ExitCode.Should().Be(1, "so Container Apps records the execution as failed");
        }
        finally
        {
            Environment.ExitCode = exitCode;
        }
    }

    [Test]
    public async Task Before_a_fixture_the_publisher_reads_the_season_and_leaves_that_round_s_banned_players_out()
    {
        var banned = Match()[0].Metadata!.Away.StartingEleven[5];
        var season = new FakeSeason(Status(latestRound: 3, (banned, [4])));
        var sender = new RecordingSender();

        await Publisher(season, sender).PlayAsync(Fixture(round: 4), CancellationToken.None);

        season.Asked.Should().Equal("2026-27");
        sender.Events.Should().NotContain(e => e.PlayerId == banned);
    }

    [Test]
    public async Task An_exhibition_never_reads_the_season()
    {
        var season = new FakeSeason();

        await Publisher(season).PlayAsync(new MatchPlan { Seed = 42 }, CancellationToken.None);

        season.Asked.Should().BeEmpty("only a fixture counts towards a season, so only a fixture has bans");
    }

    [Test]
    public async Task A_round_already_played_is_refused()
    {
        var season = new FakeSeason(Status(latestRound: 4));

        var play = () => Publisher(season).PlayAsync(Fixture(round: 4), CancellationToken.None);

        await play.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already been played*replay*",
            "a second fixture for the same round would count it twice");
    }

    [Test]
    public async Task A_fixture_waits_for_the_previous_round_to_be_projected()
    {
        // The first read is from before round 3's projection has landed; the second is after.
        var season = new FakeSeason(Status(latestRound: 2), Status(latestRound: 3));
        var sender = new RecordingSender();

        await Publisher(season, sender).PlayAsync(Fixture(round: 4), CancellationToken.None);

        season.Asked.Should().HaveCount(2);
        sender.Events.Should().NotBeEmpty();
    }

    [Test]
    public async Task A_fixture_gives_up_when_the_previous_round_never_arrives()
    {
        var season = new FakeSeason(Status(latestRound: 2));
        var sender = new RecordingSender();

        var play = () => Publisher(season, sender).PlayAsync(Fixture(round: 4), CancellationToken.None);

        await play.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Round 3*hasn't been projected*");
        sender.Events.Should().BeEmpty("without round 3's bans, round 4 can't know who is suspended");
        season.Asked.Should().HaveCount((int)(MatchPublisher.ProjectionWait / TimeSpan.FromSeconds(5)) + 1);
    }

    [Test]
    public void The_wait_covers_a_whole_match_at_the_default_speed_and_still_fits_the_job_s_timeout()
    {
        var match = MatchPacer.Pace(Match(), new MatchPlan().Speed).Aggregate(TimeSpan.Zero, (sum, b) => sum + b.DelayBefore);

        // Started back to back, round 2 waits while round 1 plays and is processed (a couple of minutes).
        MatchPublisher.ProjectionWait.Should().BeGreaterThan(match + TimeSpan.FromMinutes(2));

        // Then it plays its own match, all inside the job's 30-minute replica timeout (main.bicep).
        (MatchPublisher.ProjectionWait + match).Should().BeLessThan(TimeSpan.FromMinutes(30));
    }

    [Test]
    public async Task The_first_round_needs_nothing_before_it()
    {
        var season = new FakeSeason();

        await Publisher(season).PlayAsync(Fixture(round: 1), CancellationToken.None);

        season.Asked.Should().HaveCount(1);
    }

    [Test]
    public void A_worker_with_no_matches_configured_is_rejected()
    {
        var result = new SimulatorOptionsValidator().Validate(null, new SimulatorOptions());

        result.Failed.Should().BeTrue("an empty list plays nothing and would still look like success");
        result.Failures.Should().ContainSingle(f => f.Contains("Simulator:Matches is empty", StringComparison.Ordinal));
    }

    [Test]
    public void A_plan_with_an_unknown_team_or_referee_is_rejected()
    {
        new Action(() => new MatchPlan { Home = "no-such-fc" }.CreateSimulator(TimeProvider.System)).Should().Throw<ArgumentException>().WithMessage("*no-such-fc*");
        new Action(() => new MatchPlan { RefereeId = "ref-nobody" }.CreateSimulator(TimeProvider.System)).Should().Throw<ArgumentException>().WithMessage("*ref-nobody*");
    }

    [Test]
    public void Every_configured_match_is_checked_at_startup_before_anything_is_published()
    {
        var options = new SimulatorOptions
        {
            Matches =
            [
                new MatchPlan { Seed = 1 },
                new MatchPlan { Seed = 2, RefereeId = "ref-nobody" },
                new MatchPlan { Seed = 3, Kind = MatchKind.Fixture },
                new MatchPlan { Seed = 4, Speed = 0 }
            ]
        };

        var result = new SimulatorOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(3, "a typo in the second match must stop the worker before the first one is published");
        result.Failures.Should().Contain(f => f.Contains("Matches:1", StringComparison.Ordinal) && f.Contains("ref-nobody", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("Matches:2", StringComparison.Ordinal) && f.Contains("season", StringComparison.Ordinal))
            .And.Contain(f => f.Contains("Matches:3", StringComparison.Ordinal) && f.Contains("Speed", StringComparison.Ordinal));
        new SimulatorOptionsValidator().Validate(null, new SimulatorOptions { Matches = [new MatchPlan { Seed = 42 }] }).Succeeded.Should().BeTrue();
    }

    private static async Task RunToCompletion(MatchPublisher publisher)
    {
        await publisher.StartAsync(CancellationToken.None);
        await publisher.ExecuteTask!;
    }

    private static MatchPublisher Publisher(FakeSeason season, RecordingSender? sender = null) =>
        new(Options.Create(new SimulatorOptions()), sender ?? new RecordingSender(), new InstantTimeProvider(), season, new RecordingLifetime(), NullLogger<MatchPublisher>.Instance);

    private static MatchPlan Fixture(int round) => new() { Seed = 42, Kind = MatchKind.Fixture, Season = "2026-27", Round = round };

    // A season whose starters have played up to a round, plus any players with bans.
    private static SeasonStatus Status(int latestRound, params (string PlayerId, int[] Banned)[] bans) =>
        new([new SeasonStanding("nbfc-01", [.. Enumerable.Range(1, latestRound)], []),
            .. bans.Select(b => new SeasonStanding(b.PlayerId, [.. Enumerable.Range(1, latestRound)], b.Banned))]);

    // The season as each read finds it, in turn; the last one repeats. And which seasons were read.
    private sealed class FakeSeason(params SeasonStatus[] reads) : ISeasonReader
    {
        public List<string> Asked { get; } = [];

        public Task<SeasonStatus> ReadAsync(string season, CancellationToken cancellationToken)
        {
            Asked.Add(season);
            return Task.FromResult(reads.Length == 0 ? SeasonStatus.Empty : reads[Math.Min(Asked.Count, reads.Length) - 1]);
        }
    }

    private sealed class FailingSender(Type failure) : IMatchEventSender
    {
        public Task SendAsync(IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken) =>
            throw (Exception)Activator.CreateInstance(failure, "Event Hubs is unavailable.")!;
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }

    private sealed class RecordingSender : IMatchEventSender
    {
        public List<MatchEvent> Events { get; } = [];

        public Task SendAsync(IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    // Completes every wait at once, adding up how long it would have been.
    private sealed class InstantTimeProvider : TimeProvider
    {
        private long _ticks;

        public TimeSpan TotalDelay => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _ticks, dueTime.Ticks);
            return new FiredTimer(callback, state);
        }

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
