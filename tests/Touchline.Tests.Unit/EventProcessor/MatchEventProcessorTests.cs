extern alias processor;
extern alias simulator;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using processor::Touchline.EventProcessor.Processing;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>
/// The Event Processor's rules (ADR-023), driven with in-memory fakes for Cosmos DB and Service
/// Bus: tolerant reading, idempotent redelivery, ordering, and what happens when a dependency
/// fails part-way.
/// </summary>
[TestFixture]
public class MatchEventProcessorTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private InMemoryMatchStore _store = null!;
    private RecordingPublisher _publisher = null!;
    private ServiceProvider _services = null!;
    private MatchEventProcessor _processor = null!;
    private MeterListener _listener = null!;

    // Counter totals by instrument name, with the skip reason appended where there is one.
    private readonly Dictionary<string, long> _counts = [];

    [SetUp]
    public void CreateProcessor()
    {
        _store = new InMemoryMatchStore();
        _publisher = new RecordingPublisher();
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = _services.GetRequiredService<IMeterFactory>();
        var telemetry = new PipelineTelemetry(meterFactory);
        _processor = new MatchEventProcessor(_store, _publisher, new KeyMomentDetector(), telemetry, NullLogger<MatchEventProcessor>.Instance);

        // Listen only to this test's meter: the factory is the meter's scope.
        _counts.Clear();
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == meterFactory)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var reason = tags.ToArray().FirstOrDefault(t => t.Key == "reason").Value;
            var key = reason is null ? instrument.Name : $"{instrument.Name}:{reason}";
            _counts[key] = _counts.GetValueOrDefault(key) + value;
        });
        _listener.Start();
    }

    [TearDown]
    public void DisposeServices()
    {
        _listener.Dispose();
        _services.Dispose();
    }

    private long Count(string key) => _counts.GetValueOrDefault(key);

    private static List<MatchEvent> Match(int seed = 42) =>
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], seed).Play()];

    private static IncomingEvent Message(MatchEvent matchEvent) =>
        new(EventEnvelope.For(matchEvent), BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(matchEvent, DomainJson.Options)));

    private static IncomingEvent WithProperty(IncomingEvent message, string name, object value) =>
        message with { Properties = new Dictionary<string, object>(message.Properties) { [name] = value } };

    private async Task ProcessInBatches(IEnumerable<IncomingEvent> messages, int size = 100)
    {
        foreach (var batch in messages.Chunk(size))
        {
            await _processor.ProcessAsync(batch, CancellationToken.None);
        }
    }

    private static List<KeyMoment> ExpectedMoments(List<MatchEvent> match)
    {
        var detector = new KeyMomentDetector();
        var state = MatchState.KickOff(match[0].MatchId);
        var moments = new List<KeyMoment>();
        foreach (var e in match)
        {
            if (detector.Detect(e, state) is { } moment)
            {
                moments.Add(moment);
            }

            state = state.Apply(e);
        }

        return moments;
    }

    [Test]
    public async Task A_whole_match_is_stored_its_state_kept_and_its_key_moments_published()
    {
        var match = Match();

        await ProcessInBatches(match.Select(Message));

        var stored = _store.Matches[match[0].MatchId];
        _store.Events[match[0].MatchId].Values.Should().BeEquivalentTo(match, options => options.WithStrictOrdering());
        stored.State.LastSequence.Should().Be(match.Count);
        stored.State.Score.Should().Be(match.Aggregate(MatchState.KickOff(match[0].MatchId), (s, e) => s.Apply(e)).Score);
        stored.Metadata.Should().Be(match[0].Metadata);
        _publisher.Published.Select(m => (m.Kind, m.Trigger.Sequence)).Should().Equal(ExpectedMoments(match).Select(m => (m.Kind, m.Trigger.Sequence)));
    }

    [Test]
    public async Task Each_published_moment_carries_the_score_after_its_trigger_event()
    {
        var match = Match();
        var state = MatchState.KickOff(match[0].MatchId);
        var after = new Dictionary<long, (int?, int?)>();
        foreach (var e in match)
        {
            state = state.Apply(e);
            after[e.Sequence] = (state.Home.Goals, state.Away.Goals);
        }

        await ProcessInBatches(match.Select(Message));

        _publisher.Published.Should().Contain(m => m.Kind == KeyMomentKind.Goal);
        _publisher.Published.Select(m => (m.HomeScore, m.AwayScore))
            .Should().Equal(_publisher.Published.Select(m => after[m.Trigger.Sequence]));
    }

    [Test]
    public async Task A_redelivered_batch_changes_nothing_and_publishes_nothing_again()
    {
        var match = Match();
        var messages = match.Select(Message).ToList();
        await ProcessInBatches(messages);
        var publishedOnce = _publisher.Published.Count;
        var writes = _store.MatchWrites;

        // At-least-once delivery: the same batch arrives again after a restart.
        await ProcessInBatches(messages.Skip(600).Take(100));

        _publisher.Published.Should().HaveCount(publishedOnce);
        _store.MatchWrites.Should().Be(writes, "events already processed don't rewrite the state");
        _store.Matches[match[0].MatchId].State.LastSequence.Should().Be(match.Count);
    }

    [Test]
    public async Task An_unknown_event_type_is_skipped_and_the_events_after_it_still_apply()
    {
        var match = Match().Take(50).ToList();
        var messages = match.Select(Message).ToList();
        // A newer producer sends a type this consumer has never heard of, at sequence 20.
        messages[19] = WithProperty(messages[19], EventEnvelope.TypeProperty, "Injury");

        await ProcessInBatches(messages);

        var stored = _store.Events[match[0].MatchId];
        stored.Keys.Should().NotContain(20).And.HaveCount(49);
        _store.Matches[match[0].MatchId].State.LastSequence.Should().Be(50, "the skipped event still moves the sequence on");
    }

    [Test]
    public async Task A_newer_major_schema_an_invalid_body_and_a_mismatched_envelope_are_each_skipped()
    {
        var match = Match().Take(30).ToList();
        var messages = match.Select(Message).ToList();
        messages[4] = WithProperty(messages[4], EventEnvelope.SchemaVersionProperty, "2.0");
        messages[9] = messages[9] with { Body = BinaryData.FromString("{ not json") };
        messages[14] = messages[14] with { Body = Message(match[15]).Body };

        await ProcessInBatches(messages);

        _store.Events[match[0].MatchId].Keys.Should().NotContain([5L, 10L, 15L]).And.HaveCount(27);
        _store.Matches[match[0].MatchId].State.LastSequence.Should().Be(30);
        Count("touchline.events.skipped:NewerSchema").Should().Be(1);
        Count("touchline.events.skipped:InvalidEvent").Should().Be(2, "a body that isn't JSON and one that disagrees with its envelope");
        Count("touchline.events.processed").Should().Be(27);
    }

    [Test]
    public async Task A_message_without_an_envelope_is_skipped_without_breaking_the_batch()
    {
        var match = Match().Take(10).ToList();
        var batch = match.Select(Message).Prepend(new IncomingEvent(new Dictionary<string, object>(), BinaryData.FromString("{}"))).ToList();

        await _processor.ProcessAsync(batch, CancellationToken.None);

        _store.Events[match[0].MatchId].Should().HaveCount(10);
    }

    [Test]
    public async Task A_gap_is_counted_as_lost_and_the_match_carries_on_rather_than_freezing()
    {
        var match = Match().Take(20).ToList();
        await ProcessInBatches(match.Take(5).Select(Message));

        // Events 6 and 7 never arrive: a batch that failed every retry before the trigger checkpointed past it.
        await ProcessInBatches(match.Skip(7).Select(Message));

        _store.Matches[match[0].MatchId].State.LastSequence.Should().Be(20, "waiting for lost events would freeze the match for good");
        _store.Events[match[0].MatchId].Keys.Should().NotContain([6L, 7L]).And.HaveCount(18);
        Count("touchline.events.lost").Should().Be(2);
    }

    [Test]
    public async Task A_match_whose_first_event_is_lost_is_counted_as_missing_its_metadata()
    {
        var match = Match().Take(20).ToList();

        await ProcessInBatches(match.Skip(1).Select(Message));

        _store.Matches[match[0].MatchId].Metadata.Should().BeNull();
        Count("touchline.matches.missing_metadata").Should().Be(1, "its kind, and so whether it counts towards the season, is unknown");
        Count("touchline.events.lost").Should().Be(1);
    }

    [Test]
    public async Task A_message_from_the_simulator_goes_through_the_function_into_the_store()
    {
        // The producer's own message (Simulator), through the trigger's adapter (Event Processor).
        var match = Match().Take(30).ToList();
        var messages = match.Select(simulator::Touchline.Simulator.Publishing.EventHubsMatchEventSender.ToEventData).ToArray();

        await new processor::Touchline.EventProcessor.ProcessMatchEvents(_processor).RunAsync(messages, CancellationToken.None);

        _store.Events[match[0].MatchId].Values.Should().BeEquivalentTo(match, options => options.WithStrictOrdering());
        Count("touchline.events.skipped:MissingEnvelope").Should().Be(0, "the envelope survives the trip as message properties");
    }

    [Test]
    public async Task Two_matches_in_one_batch_are_kept_apart()
    {
        var first = Match(1).Take(40).ToList();
        var second = Match(2).Take(40).ToList();
        var interleaved = first.Zip(second).SelectMany(pair => new[] { pair.First, pair.Second }).Select(Message).ToList();

        await _processor.ProcessAsync(interleaved, CancellationToken.None);

        _store.Matches[first[0].MatchId].State.LastSequence.Should().Be(40);
        _store.Matches[second[0].MatchId].State.LastSequence.Should().Be(40);
    }

    // Chaos test: a dependency fails part-way, then recovers.

    [Test]
    public async Task If_publishing_fails_the_state_isn_t_written_so_a_retry_publishes_every_moment()
    {
        var match = Match();
        var firstGoal = match.First(e => e.IsGoal);
        var batch = match.TakeWhile(e => e.Sequence <= firstGoal.Sequence + 5).Select(Message).ToList();
        _publisher.FailNext = true;

        var process = () => _processor.ProcessAsync(batch, CancellationToken.None);

        await process.Should().ThrowAsync<InvalidOperationException>("Service Bus is unavailable");
        _store.Matches[match[0].MatchId].State.LastSequence.Should().Be(0, "the watermark is written last, after the moments");
        _store.Matches[match[0].MatchId].Metadata.Should().NotBeNull("the line-ups are written before the moments go out");
        _store.Events[match[0].MatchId].Should().HaveCount(batch.Count, "the events were stored, and storing them again is harmless");

        // The Functions retry policy runs the same batch again once Service Bus is back.
        await process();

        _publisher.Published.Should().Contain(m => m.Kind == KeyMomentKind.Goal && m.Trigger.Sequence == firstGoal.Sequence);
        _store.Matches[match[0].MatchId].State.LastSequence.Should().Be(batch.Count);
    }

    [Test]
    public async Task Events_then_the_metadata_are_stored_before_moments_are_published_and_the_state_is_written_last()
    {
        var match = Match();
        var firstGoal = match.First(e => e.IsGoal);
        var calls = new List<string>();
        MatchRecord? atPublish = null;
        _store.OnCall = calls.Add;
        _publisher.OnCall = call =>
        {
            calls.Add(call);
            atPublish = _store.Matches.GetValueOrDefault(match[0].MatchId);
        };

        await _processor.ProcessAsync([.. match.TakeWhile(e => e.Sequence <= firstGoal.Sequence).Select(Message)], CancellationToken.None);

        calls.Should().Equal("get match", "upsert events", "upsert match", "publish", "upsert match");
        atPublish!.Metadata.Should().NotBeNull("the commentary function reads the line-ups for each moment");
        atPublish.State.LastSequence.Should().Be(0, "the watermark still goes last");
    }

    [Test]
    public async Task A_later_batch_with_the_metadata_already_stored_writes_the_match_once()
    {
        var match = Match();
        var firstGoal = match.First(e => e.IsGoal);
        await _processor.ProcessAsync([.. match.Take(5).Select(Message)], CancellationToken.None);
        var calls = new List<string>();
        _store.OnCall = calls.Add;
        _publisher.OnCall = calls.Add;

        await _processor.ProcessAsync([.. match.Skip(5).TakeWhile(e => e.Sequence <= firstGoal.Sequence).Select(Message)], CancellationToken.None);

        calls.Should().Equal("get match", "upsert events", "publish", "upsert match");
    }

    [Test]
    public void The_processor_no_longer_announces_finished_matches_the_change_feed_does()
    {
        typeof(MatchEventProcessor).GetConstructors().SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(IMatchFinishedPublisher));
    }
    private sealed class RecordingPublisher : IKeyMomentPublisher
    {
        public List<KeyMoment> Published { get; } = [];

        public bool FailNext { get; set; }

        public Action<string> OnCall { get; set; } = _ => { };

        public Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken)
        {
            OnCall("publish");
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Service Bus is unavailable.");
            }

            Published.AddRange(moments);
            return Task.CompletedTask;
        }
    }
}
