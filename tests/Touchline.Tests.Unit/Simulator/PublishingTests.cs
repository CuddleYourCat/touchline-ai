extern alias simulator;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void Pacing_waits_the_match_clock_divided_by_the_speed_and_skips_half_time()
    {
        var match = Match();
        var firstHalfEnd = match.Last(e => e.Time.Period == MatchPeriod.FirstHalf).Time.Clock;
        var secondHalf = match.Where(e => e.Time.Period == MatchPeriod.SecondHalf).ToList();
        var played = firstHalfEnd + (secondHalf[^1].Time.Clock - secondHalf[0].Time.Clock);

        var total = MatchPacer.Pace(match, speed: 10).Aggregate(TimeSpan.Zero, (sum, b) => sum + b.DelayBefore);

        total.TotalSeconds.Should().BeApproximately(played.TotalSeconds / 10, 0.001, "a match at 10x takes a tenth of its playing time, with no half-time wait");
    }

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
        var plan = new MatchPlan { Seed = 42, Speed = 10 };
        var publisher = new MatchPublisher(Options.Create(new SimulatorOptions()), sender, time, new RecordingLifetime(), NullLogger<MatchPublisher>.Instance);

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

        await RunToCompletion(new MatchPublisher(Options.Create(options), sender, new InstantTimeProvider(), lifetime, NullLogger<MatchPublisher>.Instance));

        sender.Events.Select(e => e.MatchId).Distinct().Should().HaveCount(2, "both matches are played before the job ends");
        lifetime.StopRequested.Should().BeTrue();
    }

    [Test]
    public async Task Locally_the_publisher_idles_after_its_matches()
    {
        var lifetime = new RecordingLifetime();
        var options = new SimulatorOptions { Matches = [new MatchPlan { Seed = 1 }] };

        await RunToCompletion(new MatchPublisher(Options.Create(options), new RecordingSender(), new InstantTimeProvider(), lifetime, NullLogger<MatchPublisher>.Instance));

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
            await RunToCompletion(new MatchPublisher(Options.Create(options), new FailingSender(failure), new InstantTimeProvider(), lifetime, NullLogger<MatchPublisher>.Instance));

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
    public void A_plan_with_an_unknown_team_or_referee_is_rejected()
    {
        new Action(() => new MatchPlan { Home = "no-such-fc" }.CreateSimulator()).Should().Throw<ArgumentException>().WithMessage("*no-such-fc*");
        new Action(() => new MatchPlan { RefereeId = "ref-nobody" }.CreateSimulator()).Should().Throw<ArgumentException>().WithMessage("*ref-nobody*");
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
