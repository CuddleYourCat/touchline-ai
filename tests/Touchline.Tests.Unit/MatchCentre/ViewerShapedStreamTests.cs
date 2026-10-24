using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>Overlay cards and minor restarts shaped per viewer on the hub, the projector and the demo source (ADR-055).</summary>
[TestFixture]
public class ViewerShapedStreamTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();
    private static readonly KeyMomentDetector _detector = new(teams: _teams.Values);
    private static readonly IReadOnlyList<MatchEvent> _events = DemoRecording.Events;
    private static readonly string _matchId = _events[0].MatchId;
    private static readonly string[] _minorKinds = [nameof(NotableMomentKind.ThrowIn), nameof(NotableMomentKind.GoalKick)];

    private static LiveMatchHub NewHub(int buffer = 4096)
    {
        var hub = new LiveMatchHub(
            _matchId, _detector, new FakeTimeProvider(), buffer,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<MatchEvent>>([]),
            NullLogger.Instance);
        hub.Load([], []);
        return hub;
    }

    private static Viewer ViewerOf(ViewingMode mode, string? metric = null) =>
        Viewer.Create(mode, null, null, null, metric, _ => true, _ => true).Viewer!;

    private static List<LiveMessage> Drain(Subscription subscription)
    {
        var messages = new List<LiveMessage>();
        while (subscription.Live.TryRead(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static IEnumerable<OverlayCard> Cards(IEnumerable<LiveMessage> messages) =>
        messages.Where(m => m.Event == StreamEvents.Overlay).Select(m => (OverlayCard)m.Data);

    private static IEnumerable<MomentView> Moments(IEnumerable<LiveMessage> messages) =>
        messages.Where(m => m.Event == StreamEvents.Moment).Select(m => (MomentView)m.Data);

    private static IEnumerable<CommentaryLine> Lines(IEnumerable<LiveMessage> messages) =>
        messages.Where(m => m.Event == StreamEvents.Commentary).Select(m => (CommentaryLine)m.Data);

    private static bool IsMinor(MomentView moment) => _minorKinds.Contains(moment.Kind);

    [Test]
    public void The_projector_keeps_every_minor_moment_off_the_shared_messages_and_holds_its_card_back_inside_the_quiet_window()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var projections = _events.Select(projector.Apply).ToList();

        projections.SelectMany(p => p.Messages).Where(m => m.Event == StreamEvents.Moment).Select(m => (MomentView)m.Data)
            .Should().NotContain(m => IsMinor(m));
        projections.Where(p => p.Notable is { IsMinor: true }).Should().HaveCount(55).And.OnlyContain(p => p.Minor != null);
        projections.Where(p => p.Notable is not { IsMinor: true }).Should().OnlyContain(p => p.Minor == null);

        var lastKey = long.MinValue / 2;
        foreach (var projection in projections)
        {
            if (projection.Moment is not null)
            {
                lastKey = projection.Moment.Trigger.Sequence;
            }

            if (projection.Minor is { } minor)
            {
                var inside = projection.Notable!.Trigger.Sequence - lastKey <= MatchStreamProjector.NotableCardQuietEvents;
                (minor.Card is null).Should().Be(inside, "a minor card is skipped inside a key card's quiet window, and only there");
            }
        }
    }

    [Test]
    public void A_minor_card_does_not_open_a_quiet_window_for_a_notable_card()
    {
        var withMinors = new MatchStreamProjector(_matchId, _detector);
        var cards = _events.Select(withMinors.Apply).SelectMany(p => p.Messages).Count(m => m.Event == StreamEvents.Overlay);

        cards.Should().Be(23, "the shared card count is unchanged by minor moments");
    }

    [Test]
    public void Two_subscribers_on_the_same_events_get_different_overlays()
    {
        var hub = NewHub();
        using var casual = hub.Subscribe(after: null, ViewerOf(ViewingMode.Casual, "xG"));
        using var analyst = hub.Subscribe(after: null, ViewerOf(ViewingMode.Analyst));

        foreach (var matchEvent in _events.Take(400))
        {
            hub.OnEvent(matchEvent);
        }

        var casualCards = Cards(Drain(casual)).ToList();
        var analystMessages = Drain(analyst);
        var analystCards = Cards(analystMessages).ToList();

        casualCards.Should().NotBeEmpty().And.OnlyContain(c => c.Payload.Subtitle == null && c.Priority != "low");
        analystCards.Where(c => c.Priority == "low").Should().NotBeEmpty();
        analystCards.Where(c => c.Type == "goal" || c.Type == "bigchance").Should().OnlyContain(c => c.Payload.Subtitle!.StartsWith("xG ", StringComparison.Ordinal));
        analystCards.Where(c => c.Priority != "low").Select(c => c.Id).Should().Equal(casualCards.Select(c => c.Id), "the same cards, shaped differently");
        Moments(analystMessages).Count(IsMinor).Should().BeGreaterThan(0);
        Moments(analystMessages).Where(IsMinor).Should().OnlyContain(m => m.Tier == MomentTier.Minor, "the UI can tell a throw-in from a yellow card");
        Moments(analystMessages).Where(m => !IsMinor(m)).Should().NotContain(m => m.Tier == MomentTier.Minor);
        Lines(analystMessages).Select(l => l.MomentId).Should().Contain(Moments(analystMessages).Where(IsMinor).Select(m => m.Id), "the Analyst is sent the minor moment's template line");
    }

    [Test]
    public void A_minor_moment_never_reaches_a_casual_subscriber_live_or_on_replay()
    {
        var hub = NewHub();
        using var live = hub.Subscribe(after: null, Viewer.Default);
        foreach (var matchEvent in _events.Take(600))
        {
            hub.OnEvent(matchEvent);
        }

        var liveMessages = Drain(live);
        using var replay = hub.Subscribe(after: 0, Viewer.Default);
        using var spanish = hub.Subscribe(after: 0, Viewer.Create(ViewingMode.Casual, "es", null, null, null, _ => true, _ => true).Viewer!);

        var projector = new MatchStreamProjector(_matchId, _detector);
        var minorIds = _events.Take(600).Select(projector.Apply).Where(x => x.Minor != null).Select(x => x.Minor!.Moment.Id).ToHashSet();

        foreach (var messages in new[] { liveMessages, [.. replay.Backlog], [.. spanish.Backlog] })
        {
            Moments(messages).Should().NotContain(m => IsMinor(m));
            Cards(messages).Should().OnlyContain(c => c.Priority != "low" && c.Payload.Subtitle == null);
            Lines(messages).Select(l => l.MomentId).Should().NotContain(minorIds);
        }

        minorIds.Should().NotBeEmpty();
    }

    [Test]
    public void A_reconnect_replay_is_shaped_like_the_live_stream_with_no_duplicates()
    {
        var hub = NewHub();
        using var live = hub.Subscribe(after: null, ViewerOf(ViewingMode.Analyst));
        foreach (var matchEvent in _events.Take(500))
        {
            hub.OnEvent(matchEvent);
        }

        var seen = Drain(live);
        var after = long.Parse(seen.Where(m => m.Event == StreamEvents.Overlay).ElementAt(4).Id!, CultureInfo.InvariantCulture);
        using var reconnect = hub.Subscribe(after, ViewerOf(ViewingMode.Analyst));

        static bool Shaped(LiveMessage m) => m.Event is StreamEvents.Overlay or StreamEvents.Moment or StreamEvents.Commentary;
        var expected = seen.Where(m => Shaped(m) && long.Parse(m.Id!, CultureInfo.InvariantCulture) > after).ToList();
        var replayed = reconnect.Backlog.Where(Shaped).ToList();

        replayed.Select(m => (m.Event, m.Id)).Should().Equal(expected.Select(m => (m.Event, m.Id)));
        Cards(replayed).Should().Equal(Cards(expected));
        Moments(replayed).Select(m => m.Id).Should().Equal(Moments(expected).Select(m => m.Id));
        Cards(replayed).Select(c => c.Id).Should().OnlyHaveUniqueItems();
        Moments(replayed).Select(m => m.Id).Should().OnlyHaveUniqueItems();
        Lines(replayed).Select(l => l.MomentId).Should().OnlyHaveUniqueItems();
        Cards(replayed).Should().Contain(c => c.Priority == "low");
    }

    [Test]
    public async Task The_demo_source_shapes_an_analyst_and_a_casual_viewer_differently()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero));
        var source = new DemoMatchSource(
            _events, _teams, _detector, Options.Create(new DemoOptions { Speed = 1, LapGap = TimeSpan.FromMinutes(1) }), time);
        time.Advance(TimeSpan.FromHours(3));

        var casual = await source.FollowAsync("demo-1", 0, Viewer.Default, CancellationToken.None).ToListAsync();
        var analyst = await source.FollowAsync("demo-1", 0, ViewerOf(ViewingMode.Analyst), CancellationToken.None).ToListAsync();
        var casualMoments = await source.GetMomentsAsync("demo-1", Viewer.Default, CancellationToken.None);
        var analystMoments = await source.GetMomentsAsync("demo-1", ViewerOf(ViewingMode.Analyst), CancellationToken.None);

        Cards(casual).Should().OnlyContain(c => c.Payload.Subtitle == null && c.Priority != "low");
        Cards(analyst).Where(c => c.Priority == "low").Should().NotBeEmpty();
        Cards(analyst).Where(c => c.Type == "goal").Should().OnlyContain(c => c.Payload.Subtitle!.StartsWith("xG ", StringComparison.Ordinal));
        Cards(analyst).Where(c => c.Priority != "low").Select(c => (c.Id, c.At, c.DurationMs))
            .Should().Equal(Cards(casual).Select(c => (c.Id, c.At, c.DurationMs)), "ID, time and duration are kept");
        Moments(casual).Should().NotContain(m => IsMinor(m));
        Moments(analyst).Count(IsMinor).Should().Be(55);
        casualMoments.Should().HaveCount(25).And.NotContain(m => IsMinor(m));
        analystMoments.Should().HaveCount(80);
        analystMoments.Where(IsMinor).Should().OnlyContain(m => m.Commentary != null && m.Commentary.Mode == ViewingMode.Analyst);
        Lines(casual).Select(l => l.MomentId).Should().NotContain(analystMoments.Where(IsMinor).Select(m => m.Id));
    }

    [TestCase(ViewingMode.Casual)]
    [TestCase(ViewingMode.Analyst)]
    public void A_stream_that_overflows_never_holds_part_of_an_event(ViewingMode mode)
    {
        var hub = NewHub(buffer: 20);
        using var slow = hub.Subscribe(after: null, ViewerOf(mode));

        foreach (var matchEvent in _events.Take(200))
        {
            hub.OnEvent(matchEvent);
        }

        var seen = Drain(slow);
        var ids = seen.Select(m => m.Id).Distinct().Where(id => id != seen[0].Id).ToList();

        seen.Should().HaveCountLessThanOrEqualTo(20);
        ids.Should().NotBeEmpty().And.OnlyContain(id => seen.Any(m => m.Event == StreamEvents.State && m.Id == id), "every event it holds has its state, the last message of the event");
        slow.Live.Completion.IsCompleted.Should().BeTrue();
    }

    [Test]
    public void A_slow_analyst_overflows_and_ends_while_a_casual_subscriber_still_gets_everything_and_the_analyst_replays_correctly()
    {
        var hub = NewHub(buffer: 64);
        using var baseline = hub.Subscribe(after: null, ViewerOf(ViewingMode.Analyst));
        using var slow = hub.Subscribe(after: null, ViewerOf(ViewingMode.Analyst));
        using var casual = hub.Subscribe(after: null, Viewer.Default);

        var baselineSeen = new List<LiveMessage>();
        var casualSeen = new List<LiveMessage>();
        foreach (var matchEvent in _events.Take(300))
        {
            hub.OnEvent(matchEvent);
            baselineSeen.AddRange(Drain(baseline));
            casualSeen.AddRange(Drain(casual));
        }

        var slowSeen = Drain(slow);
        slowSeen.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(64, "the slow stream held at most a full buffer and was then ended, not fed more");
        slow.Live.Completion.IsCompleted.Should().BeTrue("a full buffer ends the stream instead of dropping messages");
        casualSeen.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence)
            .Should().Equal(Enumerable.Range(1, 300).Select(i => (long)i), "the casual subscriber missed nothing");
        Cards(casualSeen).Should().OnlyContain(c => c.Priority != "low");

        // The client resumes from the true Last-Event-ID: the ID of the last message it received.
        var after = long.Parse(slowSeen[^1].Id!, CultureInfo.InvariantCulture);
        slowSeen.Count(m => m.Event == StreamEvents.State && m.Id == slowSeen[^1].Id).Should().Be(1, "the last event it received arrived whole, state included");
        using var reconnect = hub.Subscribe(after, ViewerOf(ViewingMode.Analyst));

        reconnect.Backlog.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence)
            .Should().Equal(Enumerable.Range((int)after + 1, 300 - (int)after).Select(i => (long)i), "the replay carries on exactly after the last event it received");
        reconnect.Backlog.Where(m => m.Event == StreamEvents.State).Select(m => m.Id).Distinct()
            .Should().HaveCount(300 - (int)after, "every replayed event comes with its state");

        static bool Shaped(LiveMessage m) => m.Event is StreamEvents.Overlay or StreamEvents.Moment or StreamEvents.Commentary;
        var expected = baselineSeen.Where(m => Shaped(m) && long.Parse(m.Id!, CultureInfo.InvariantCulture) > after).ToList();

        reconnect.Backlog.Where(Shaped).Select(m => (m.Event, m.Id)).Should().Equal(expected.Select(m => (m.Event, m.Id)));
        Cards(reconnect.Backlog).Should().Equal(Cards(expected));
        Cards(baselineSeen).Should().Contain(c => c.Priority == "low");
    }
}
