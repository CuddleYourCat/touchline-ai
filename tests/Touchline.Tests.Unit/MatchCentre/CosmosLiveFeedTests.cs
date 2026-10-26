using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;
using Touchline.MatchCentre.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The live stream from the change feed: the hubs, the feed and the reader, with Cosmos DB mocked.</summary>
[TestFixture]
public class CosmosLiveFeedTests
{
    private static readonly KeyMomentDetector _detector = new(teams: TeamProfileCatalog.LoadEmbedded().Values);
    private static readonly IReadOnlyList<MatchEvent> _events = DemoRecording.Events;
    private static readonly string _matchId = _events[0].MatchId;
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(5);

    private Mock<Container> _eventsContainer = null!;
    private FakeTimeProvider _time = null!;
    private List<MatchEvent> _stored = null!;
    private List<MatchEvent> _gapFill = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider();
        _stored = [.. _events.Take(100)];
        _gapFill = [];
        _eventsContainer = new Mock<Container>();
        _eventsContainer.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions _) => Pages(query.QueryText == LiveMatchHubs.GapQuery ? _gapFill : _stored));
    }

    [Test]
    public async Task A_new_stream_gets_a_state_snapshot_then_live_messages()
    {
        var hubs = Hubs();
        await using var stream = Follow(hubs, after: null, Viewer.Default);

        var snapshot = await Next(stream);
        hubs.Dispatch(_events[100]);
        var live = await Through(stream, 101);

        snapshot.Event.Should().Be(StreamEvents.State);
        ((LiveState)snapshot.Data).LastSequence.Should().Be(100);
        live[0].Event.Should().Be(StreamEvents.Event);
        ((MatchEvent)live[0].Data).Sequence.Should().Be(101);
    }

    [Test]
    public async Task A_reconnect_replays_only_the_messages_after_its_last_event()
    {
        await using var stream = Follow(Hubs(), after: 90, Viewer.Default);

        var replay = await Through(stream, 100);

        replay.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence)
            .Should().Equal(Enumerable.Range(91, 10).Select(s => (long)s));
        replay.Should().OnlyContain(m => long.Parse(m.Id!) > 90);
    }

    [Test]
    public async Task A_stream_of_a_finished_match_ends_after_full_time()
    {
        _stored = [.. _events];
        var projector = new MatchStreamProjector(_matchId, _detector);
        var expected = _events.SelectMany(e => projector.Apply(e).Messages).ToList();

        var sent = new List<LiveMessage>();
        await foreach (var message in new CosmosLiveFeed(Hubs()).FollowAsync(_matchId, 0, Viewer.Default, null, CancellationToken.None))
        {
            sent.Add(message);
        }

        // The overlay cards are shaped for the viewer (ADR-055), so they are compared by ID.
        sent.Where(m => m.Event is not (StreamEvents.Commentary or StreamEvents.Overlay)).Should().Equal(expected.Where(m => m.Event != StreamEvents.Overlay));
        sent.Where(m => m.Event == StreamEvents.Overlay).Select(m => ((OverlayCard)m.Data).Id)
            .Should().Equal(expected.Where(m => m.Event == StreamEvents.Overlay).Select(m => ((OverlayCard)m.Data).Id));
        sent.Count(m => m.Event == StreamEvents.Commentary).Should().Be(24, "one template line per notable moment, the best for the viewer");
        sent[^1].Event.Should().Be(StreamEvents.FullTime);
    }

    [Test]
    public async Task Events_the_hub_already_has_are_dropped()
    {
        var hubs = Hubs();
        await using var stream = Follow(hubs, after: null, Viewer.Default);
        await Next(stream);

        hubs.Dispatch(_events[49]);
        hubs.Dispatch(_events[100]);
        hubs.Dispatch(_events[100]);
        hubs.Dispatch(_events[101]);
        var live = await Through(stream, 102);

        live.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence).Should().Equal(101L, 102L);
    }

    [Test]
    public async Task An_event_after_a_gap_is_projected_instead_of_throwing()
    {
        var hubs = Hubs();
        await using var stream = Follow(hubs, after: null, Viewer.Default);
        await Next(stream);

        hubs.Invoking(h => h.Dispatch(_events[104])).Should().NotThrow();
        var live = await Through(stream, 105);

        ((MatchEvent)live[0].Data).Sequence.Should().Be(105);
    }

    [Test]
    public async Task Commentary_reaches_only_the_viewers_it_suits()
    {
        var hubs = Hubs();
        var casualMexican = Viewer.Create(ViewingMode.Casual, "es-MX", null, null, null, _ => true, _ => true).Viewer!;
        var casualFrench = Viewer.Create(ViewingMode.Casual, "fr", null, null, null, _ => true, _ => true).Viewer!;
        var analyst = Viewer.Create(ViewingMode.Analyst, "en", null, null, null, _ => true, _ => true).Viewer!;
        await using var mexican = Follow(hubs, after: null, casualMexican);
        await using var french = Follow(hubs, after: null, casualFrench);
        await using var analysis = Follow(hubs, after: null, analyst);
        await Next(mexican);
        await Next(french);
        await Next(analysis);

        hubs.Dispatch(Line(ViewingMode.Casual, "es", "Golazo."));
        hubs.Dispatch(Line(ViewingMode.Casual, "en", "What a goal."));
        hubs.Dispatch(Line(ViewingMode.Analyst, "de", "Ein Tor."));
        hubs.Dispatch(_events[100]);

        (await CommentaryThrough(mexican)).Should().Equal(["Golazo."], "English is worse than Spanish for them");
        (await CommentaryThrough(french)).Should().Equal("What a goal.");
        (await CommentaryThrough(analysis)).Should().BeEmpty();
    }

    [Test]
    public async Task With_the_commentary_store_down_the_stream_still_delivers_overlay_cards_and_template_lines()
    {
        var store = new Mock<ICommentaryStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Service unavailable", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));
        var hubs = Hubs(buffer: 4096, store: store);
        await using var stream = Follow(hubs, after: null, Viewer.Default);
        var snapshot = await Next(stream);

        foreach (var matchEvent in _events.Skip(100).Take(300))
        {
            hubs.Dispatch(matchEvent);
        }

        var live = await Through(stream, 400);
        var cards = live.Where(m => m.Event == StreamEvents.Overlay).Select(m => (OverlayCard)m.Data).ToList();
        var lines = live.Where(m => m.Event == StreamEvents.Commentary).Select(m => (CommentaryLine)m.Data).ToList();

        snapshot.Event.Should().Be(StreamEvents.State);
        cards.Should().NotBeEmpty().And.OnlyContain(c => c.Id.EndsWith(":overlay", StringComparison.Ordinal));
        cards.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        lines.Should().NotBeEmpty().And.OnlyContain(l => l.Source == CommentarySource.Template && l.Tier == MomentTier.Notable);
        live.Count(m => m.Event == StreamEvents.Moment).Should().BeGreaterThanOrEqualTo(cards.Count);
        store.Verify(s => s.GetAsync(_matchId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Test]
    public async Task An_overflowing_stream_is_ended_without_holding_up_a_fast_one()
    {
        var hubs = Hubs(buffer: 8);
        await using var fast = Follow(hubs, after: null, Viewer.Default);
        await using var slow = Follow(hubs, after: null, Viewer.Default);
        await Next(fast);
        await Next(slow);

        var seen = new List<long>();
        foreach (var matchEvent in _events.Skip(100).Take(30))
        {
            hubs.Dispatch(matchEvent);
            seen.AddRange((await Through(fast, matchEvent.Sequence)).Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence));
        }

        seen.Should().Equal(_events.Skip(100).Take(30).Select(e => e.Sequence));
        var kept = new List<LiveMessage>();
        while (await slow.MoveNextAsync().AsTask().WaitAsync(_patience))
        {
            kept.Add(slow.Current);
        }

        kept.Should().HaveCountLessThanOrEqualTo(8, "the stream ends once its buffer is full");
        kept.Select(m => long.Parse(m.Id!)).Should().BeInAscendingOrder().And.OnlyContain(id => id < 110, "nothing was dropped from its start");
    }

    [Test]
    public async Task No_hub_loads_until_the_change_feed_has_its_start_point()
    {
        var hubs = Hubs(started: false);
        var source = new FakeChangeFeedSource();
        using var reader = new ChangeFeedReader(source, hubs, _time, NullLogger<ChangeFeedReader>.Instance);

        var get = hubs.GetAsync(_matchId, CancellationToken.None);
        await Task.Delay(50);
        get.IsCompleted.Should().BeFalse();
        _eventsContainer.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Never());

        await reader.StartAsync(CancellationToken.None);
        await get.WaitAsync(_patience);

        source.Reads.Should().BeGreaterThan(0);
        hubs.FeedStarted.IsCompletedSuccessfully.Should().BeTrue();
        _eventsContainer.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once());
        await reader.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task A_gap_is_backfilled_from_the_store_instead_of_skipped()
    {
        _gapFill = [.. _events.Skip(100).Take(4)];
        var hubs = Hubs();
        await using var stream = Follow(hubs, after: null, Viewer.Default);
        await Next(stream);

        hubs.Dispatch(_events[104]);
        var live = await Through(stream, 105);

        live.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence).Should().Equal(101L, 102L, 103L, 104L, 105L);
        _eventsContainer.Verify(c => c.GetItemQueryStreamIterator(It.Is<QueryDefinition>(q => q.QueryText == LiveMatchHubs.GapQuery), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once());
    }

    [Test]
    public async Task A_hole_the_store_lacks_is_skipped_and_a_late_event_for_it_is_logged()
    {
        _gapFill = [_events[101]];
        var logger = new RecordingLogger();
        var hubs = Hubs(logger: logger);
        await using var stream = Follow(hubs, after: null, Viewer.Default);
        await Next(stream);

        hubs.Dispatch(_events[104]);
        var live = await Through(stream, 105);
        hubs.Dispatch(_events[100]);

        live.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence).Should().Equal(102L, 105L);
        logger.Messages.Should().ContainSingle(m => m.Contains("got event 101 late"));
    }

    [Test]
    public async Task A_reconnect_replays_commentary_that_arrived_after_its_last_event()
    {
        var hubs = Hubs();
        var hub = await hubs.GetAsync(_matchId, CancellationToken.None);
        foreach (var matchEvent in _events.Skip(100).Take(10))
        {
            hubs.Dispatch(matchEvent);
        }

        hubs.Dispatch(Line(Viewer.Default.Mode, Viewer.Default.Language, "Late but lovely."));

        using var reconnect = hub.Subscribe(105, Viewer.Default);
        using var current = hub.Subscribe(110, Viewer.Default);

        var replayed = reconnect.Backlog.Where(m => m.Event == StreamEvents.Commentary).ToList();
        replayed.Should().ContainSingle().Which.Id.Should().Be("110");
        reconnect.Backlog[^1].Should().Be(replayed[0], "it arrived after event 110");
        current.Backlog.Should().BeEmpty();
    }

    [Test]
    public async Task A_failed_load_removes_its_hub_even_with_nobody_waiting()
    {
        _eventsContainer.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Throws(new CosmosException("Service unavailable", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));
        var hubs = Hubs();
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        var waiting = () => hubs.GetAsync(_matchId, gone.Token);
        await waiting.Should().ThrowAsync<OperationCanceledException>();
        var deadline = DateTime.UtcNow + _patience;
        while (hubs.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        hubs.Count.Should().Be(0);
        hubs.Invoking(h => h.Dispatch(_events[100])).Should().NotThrow();
    }

    [Test]
    public void A_faulted_hub_ignores_the_feed_and_a_loading_one_holds_at_most_the_cap()
    {
        var hub = new LiveMatchHub(_matchId, _detector, _time, 8, (_, _, _, _) => Task.FromResult<IReadOnlyList<MatchEvent>>([]), NullLogger.Instance);
        var many = Enumerable.Range(1, LiveMatchHub.MaxPending + 5)
            .Select(i => _events[0] with { Sequence = i });

        foreach (var matchEvent in many)
        {
            hub.OnEvent(matchEvent);
        }

        hub.PendingCount.Should().Be(LiveMatchHub.MaxPending);
        hub.Fault();
        hub.OnEvent(_events[0]);
        hub.PendingCount.Should().Be(0);
    }

    [Test]
    public async Task A_stream_of_a_finished_match_always_ends_with_full_time()
    {
        _stored = [.. _events];
        var hubs = Hubs();
        var last = _events[^1].Sequence;

        var fromNow = await new CosmosLiveFeed(hubs).FollowAsync(_matchId, null, Viewer.Default, null, CancellationToken.None).ToListAsync();
        var caughtUp = await new CosmosLiveFeed(hubs).FollowAsync(_matchId, last, Viewer.Default, null, CancellationToken.None).ToListAsync();

        fromNow.Select(m => m.Event).Should().Equal(StreamEvents.State, StreamEvents.FullTime);
        caughtUp.Should().ContainSingle().Which.Event.Should().Be(StreamEvents.FullTime);
    }

    [Test]
    public async Task A_client_ahead_of_the_hub_gets_nothing_it_already_has()
    {
        var hubs = Hubs();
        var hub = await hubs.GetAsync(_matchId, CancellationToken.None);
        using var subscription = hub.Subscribe(103, Viewer.Default);

        foreach (var matchEvent in _events.Skip(100).Take(5))
        {
            hubs.Dispatch(matchEvent);
        }

        var live = new List<LiveMessage>();
        while (subscription.Live.TryRead(out var message))
        {
            live.Add(message);
        }

        subscription.Backlog.Should().BeEmpty();
        live.Should().OnlyContain(m => long.Parse(m.Id!) > 103);
        live.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence).Should().Equal(104L, 105L);
    }

    [Test]
    public async Task A_viewer_gets_a_line_only_when_it_is_no_worse_than_one_already_sent()
    {
        var hubs = Hubs();
        var casualMexican = Viewer.Create(ViewingMode.Casual, "es-MX", null, null, null, _ => true, _ => true).Viewer!;
        await using var stream = Follow(hubs, after: null, casualMexican);
        await Next(stream);

        hubs.Dispatch(Line(ViewingMode.Casual, "es", "Gol.", CommentarySource.Template));
        hubs.Dispatch(Line(ViewingMode.Casual, "en", "Goal.", CommentarySource.Model));
        hubs.Dispatch(Line(ViewingMode.Casual, "es", "Golazo."));
        hubs.Dispatch(Line(ViewingMode.Casual, "es-MX", "Golazo, hermano.", CommentarySource.Template));
        hubs.Dispatch(Line(ViewingMode.Casual, "es", "Otro gol.", CommentarySource.Template));
        hubs.Dispatch(_events[100]);
        var hub = await hubs.GetAsync(_matchId, CancellationToken.None);
        using var reconnect = hub.Subscribe(99, casualMexican);

        (await CommentaryThrough(stream)).Should().Equal("Gol.", "Golazo.", "Golazo, hermano.");
        reconnect.Backlog.Where(m => m.Event == StreamEvents.Commentary).Select(m => ((CommentaryLine)m.Data).Text)
            .Should().Equal("Golazo, hermano.");
    }

    [Test]
    public async Task A_hub_is_evicted_ten_minutes_after_full_time_even_with_streams_open()
    {
        var hubs = Hubs();
        var hub = await hubs.GetAsync(_matchId, CancellationToken.None);
        using var subscription = hub.Subscribe(null, Viewer.Default);

        foreach (var matchEvent in _events.Skip(100))
        {
            hubs.Dispatch(matchEvent);
        }

        _time.Advance(TimeSpan.FromMinutes(9));
        hubs.EvictExpired().Should().Be(0);
        _time.Advance(TimeSpan.FromMinutes(1));
        hubs.EvictExpired().Should().Be(1);

        var drain = async () => await subscription.Live.ReadAllAsync().CountAsync();
        await drain.Should().CompleteWithinAsync(_patience, "eviction ends the open streams");
        await hubs.GetAsync(_matchId, CancellationToken.None);
        _eventsContainer.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Exactly(2));
    }

    [Test]
    public async Task A_hub_is_evicted_after_ten_minutes_with_nobody_watching()
    {
        var hubs = Hubs();
        var hub = await hubs.GetAsync(_matchId, CancellationToken.None);
        var subscription = hub.Subscribe(null, Viewer.Default);

        _time.Advance(TimeSpan.FromMinutes(11));
        hubs.EvictExpired().Should().Be(0, "someone is still watching");
        subscription.Dispose();
        _time.Advance(TimeSpan.FromMinutes(9));
        hubs.EvictExpired().Should().Be(0);
        _time.Advance(TimeSpan.FromMinutes(1));
        hubs.EvictExpired().Should().Be(1);
    }

    [Test]
    public async Task A_hub_shares_one_read_of_the_stored_events_between_its_streams()
    {
        var hubs = Hubs();

        await hubs.GetAsync(_matchId, CancellationToken.None);
        await hubs.GetAsync(_matchId, CancellationToken.None);

        _eventsContainer.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once());
    }

    [Test]
    public async Task A_failing_poll_is_logged_counted_and_retried_without_stopping_the_reader()
    {
        var hubs = Hubs();
        var source = new FakeChangeFeedSource();
        source.Changes.Enqueue(() => throw new CosmosException("Service unavailable", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));
        source.Changes.Enqueue(() => [JsonSerializer.SerializeToElement(_events[100], DomainJson.Options)]);
        using var reader = new ChangeFeedReader(source, hubs, _time, NullLogger<ChangeFeedReader>.Instance);
        await using var stream = Follow(hubs, after: null, Viewer.Default);
        await Next(stream);

        await reader.StartAsync(CancellationToken.None);
        var delivered = Through(stream, 101);
        while (!delivered.IsCompleted)
        {
            _time.Advance(ChangeFeedReader.Interval);
            await Task.Delay(10);
        }

        ((MatchEvent)(await delivered)[0].Data).Sequence.Should().Be(101);
        reader.FailedPolls.Should().Be(1);
        await reader.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task The_Cosmos_source_reads_changes_after_the_continuation_it_kept()
    {
        var container = new Mock<Container>();
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Events)).Returns(container.Object);
        var starts = new List<ChangeFeedStartFrom>();
        var replies = new Queue<ResponseMessage>([NotModified("now"), Page([_events[0]], "after-first"), NotModified("after-first")]);
        container.Setup(c => c.GetChangeFeedStreamIterator(It.IsAny<ChangeFeedStartFrom>(), It.IsAny<ChangeFeedMode>(), It.IsAny<ChangeFeedRequestOptions>()))
            .Returns((ChangeFeedStartFrom start, ChangeFeedMode _, ChangeFeedRequestOptions _) =>
            {
                starts.Add(start);
                var iterator = new Mock<FeedIterator>();
                iterator.Setup(i => i.HasMoreResults).Returns(true);
                iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(replies.Dequeue);
                return iterator.Object;
            });
        var source = new CosmosChangeFeedSource(client.Object);

        var first = await source.ReadChangesAsync(CosmosNames.Events, CancellationToken.None);
        var second = await source.ReadChangesAsync(CosmosNames.Events, CancellationToken.None);

        first.Should().BeEmpty("the first poll only marks now");
        second.Should().ContainSingle().Which.Deserialize<MatchEvent>(DomainJson.Options)!.Sequence.Should().Be(_events[0].Sequence);
        starts.Should().HaveCount(2);
        starts[1].Should().NotBeSameAs(starts[0]);
    }

    private LiveMatchHubs Hubs(int buffer = LiveMatchHubs.DefaultBuffer, bool started = true, ILogger<LiveMatchHubs>? logger = null, Mock<ICommentaryStore>? store = null)
    {
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Events)).Returns(_eventsContainer.Object);
        var commentary = store ?? new Mock<ICommentaryStore>();
        if (store is null)
        {
            commentary.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        }

        var hubs = new LiveMatchHubs(client.Object, commentary.Object, _detector, _time, logger ?? NullLogger<LiveMatchHubs>.Instance, buffer);
        if (started)
        {
            hubs.MarkFeedStarted();
        }

        return hubs;
    }

    private static IAsyncEnumerator<LiveMessage> Follow(LiveMatchHubs hubs, long? after, Viewer viewer) =>
        new CosmosLiveFeed(hubs).FollowAsync(_matchId, after, viewer, null, CancellationToken.None).GetAsyncEnumerator();

    private static async Task<LiveMessage> Next(IAsyncEnumerator<LiveMessage> stream)
    {
        (await stream.MoveNextAsync().AsTask().WaitAsync(_patience)).Should().BeTrue();
        return stream.Current;
    }

    // Reads up to and including the state message for a sequence.
    private static async Task<List<LiveMessage>> Through(IAsyncEnumerator<LiveMessage> stream, long sequence)
    {
        var messages = new List<LiveMessage>();
        while (true)
        {
            var message = await Next(stream);
            messages.Add(message);
            if (message is { Event: StreamEvents.State, Data: LiveState state } && state.LastSequence >= sequence)
            {
                return messages;
            }
        }
    }

    private static async Task<List<string>> CommentaryThrough(IAsyncEnumerator<LiveMessage> stream) =>
        [.. (await Through(stream, 101)).Where(m => m.Event == StreamEvents.Commentary).Select(m => ((CommentaryLine)m.Data).Text)];

    private static CommentaryLine Line(ViewingMode mode, string language, string text, CommentarySource source = CommentarySource.Model) =>
        new(_matchId, MomentIds.For(_matchId, 100), 100, MomentTier.Key, source, text, mode, language);

    private static FeedIterator Pages<T>(IEnumerable<T> documents)
    {
        var iterator = new Mock<FeedIterator>();
        iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Page(documents, null));
        return iterator.Object;
    }

    private static ResponseMessage Page<T>(IEnumerable<T> documents, string? continuation)
    {
        var page = new JsonObject { ["Documents"] = JsonSerializer.SerializeToNode(documents.ToList(), DomainJson.Options) };
        var response = new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(page, DomainJson.Options)) };
        if (continuation is not null)
        {
            response.Headers.Set("x-ms-continuation", continuation);
        }

        return response;
    }

    private static ResponseMessage NotModified(string continuation)
    {
        var response = new ResponseMessage(HttpStatusCode.NotModified);
        response.Headers.Set("x-ms-continuation", continuation);
        return response;
    }

    private sealed class FakeChangeFeedSource : IChangeFeedSource
    {
        public Queue<Func<IReadOnlyList<JsonElement>>> Changes { get; } = new();

        public int Reads { get; private set; }

        public Task<IReadOnlyList<JsonElement>> ReadChangesAsync(string container, CancellationToken cancellationToken)
        {
            Reads++;
            return container == CosmosNames.Events && Changes.TryDequeue(out var next)
                ? Task.FromResult(next())
                : Task.FromResult<IReadOnlyList<JsonElement>>([]);
        }
    }

    private sealed class RecordingLogger : ILogger<LiveMatchHubs>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
