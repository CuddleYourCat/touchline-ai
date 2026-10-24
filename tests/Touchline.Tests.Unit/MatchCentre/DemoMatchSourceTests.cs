using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class DemoMatchSourceTests
{
    private static readonly TimeSpan _gap = TimeSpan.FromMinutes(1);
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private FakeTimeProvider _time = null!;
    private DemoMatchSource _source = null!;

    // At speed 1, a lap lasts the match's own length: about 95 minutes, with no half-time break.
    private static TimeSpan MatchLength
    {
        get
        {
            var firstHalf = DemoRecording.Events.Where(e => e.Time.Period == MatchPeriod.FirstHalf).Max(e => e.Time.Clock);
            return firstHalf + (DemoRecording.Events[^1].Time.Clock - TimeSpan.FromMinutes(45));
        }
    }

    [SetUp]
    public void StartTheDemoClock()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero));
        _source = new DemoMatchSource(
            DemoRecording.Events,
            _teams,
            new KeyMomentDetector(teams: _teams.Values),
            Options.Create(new DemoOptions { Speed = 1, LapGap = _gap }),
            _time);
    }

    [Test]
    public async Task Lap_one_kicks_off_as_soon_as_the_service_starts()
    {
        var matches = (await _source.ListMatchesAsync(null, null, CancellationToken.None)).Matches;

        matches.Should().ContainSingle();
        matches[0].Id.Should().Be("demo-1");
        matches[0].Status.Should().Be(MatchStatus.Live);
        matches[0].Score.Should().Be(new Score(0, 0));
        matches[0].KickOffUtc.Should().Be(_time.GetUtcNow());
        matches[0].DataSource.Should().Be("synthetic");
    }

    [Test]
    public async Task A_lap_reaches_full_time_with_the_recorded_score_and_moments()
    {
        _time.Advance(MatchLength);

        var match = await _source.GetMatchAsync("demo-1", CancellationToken.None);
        var moments = await _source.GetMomentsAsync("demo-1", Viewer.Default, CancellationToken.None);

        match!.Summary.Status.Should().Be(MatchStatus.FullTime);
        match.State.Score.Should().Be(new Score(3, 3));
        match.State.LastSequence.Should().Be(DemoRecording.Events.Count);
        moments.Should().HaveCount(9);
        moments!.Should().OnlyContain(m => m.Commentary!.Source == CommentarySource.Template && m.Id.StartsWith("demo-1:", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_next_lap_kicks_off_after_the_gap_and_the_last_one_stays_listed_as_finished()
    {
        _time.Advance(MatchLength + _gap);

        var matches = (await _source.ListMatchesAsync(null, null, CancellationToken.None)).Matches;

        matches.Select(m => (m.Id, m.Status)).Should().Equal(("demo-2", MatchStatus.Live), ("demo-1", MatchStatus.FullTime));
        (await _source.ListMatchesAsync(MatchStatus.Live, null, CancellationToken.None)).Matches.Should().ContainSingle(m => m.Id == "demo-2");
    }

    [TestCase("demo-2", Description = "a lap that hasn't kicked off")]
    [TestCase("demo-0")]
    [TestCase("demo-01", Description = "not the canonical spelling")]
    [TestCase("demo-x")]
    public async Task Anything_but_a_lap_that_has_kicked_off_is_no_match(string matchId)
    {
        (await _source.GetMatchAsync(matchId, CancellationToken.None)).Should().BeNull();
        (await _source.GetEventsAsync(matchId, 0, 10, CancellationToken.None)).Should().BeNull();
        (await _source.GetMomentumAsync(matchId, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public Task The_recordings_own_match_id_is_no_match() =>
        Anything_but_a_lap_that_has_kicked_off_is_no_match(DemoRecording.Events[0].MatchId);

    [Test]
    public async Task Events_come_in_pages_relabelled_with_the_lap_and_only_up_to_now()
    {
        _time.Advance(TimeSpan.FromMinutes(10));
        var played = (await _source.GetMatchAsync("demo-1", CancellationToken.None))!.State.LastSequence;

        var first = (await _source.GetEventsAsync("demo-1", 0, 100, CancellationToken.None))!;
        var last = (await _source.GetEventsAsync("demo-1", played - 5, 100, CancellationToken.None))!;

        first.Events.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, 100).Select(i => (long)i));
        first.Events.Should().OnlyContain(e => e.MatchId == "demo-1");
        first.NextAfter.Should().Be(100);
        last.Events.Should().HaveCount(5);
        last.NextAfter.Should().BeNull("there are no more events yet");
    }

    [Test]
    public async Task Following_from_now_starts_with_a_state_snapshot()
    {
        _time.Advance(MatchLength);

        var messages = await _source.FollowAsync("demo-1", null, Viewer.Default, CancellationToken.None).ToListAsync();

        messages.Select(m => m.Event).Should().Equal(StreamEvents.State, StreamEvents.FullTime);
        ((LiveState)messages[0].Data).FullTime.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase(100L)]
    [TestCase(5000L)]
    [TestCase(long.MaxValue)]
    public async Task Following_a_finished_lap_always_ends_with_the_final_whistle(long? after)
    {
        _time.Advance(MatchLength);
        var last = DemoRecording.Events[^1].Sequence;

        var messages = await _source.FollowAsync("demo-1", after, Viewer.Default, CancellationToken.None).ToListAsync();

        var fullTime = messages.Should().ContainSingle(m => m.Event == StreamEvents.FullTime).Subject;
        messages[^1].Should().BeSameAs(fullTime);
        ShouldCarryTheHighestId(messages, fullTime, last);
        var state = (LiveState)fullTime.Data;
        state.FullTime.Should().BeTrue();
        state.LastSequence.Should().Be(last);
        state.MatchId.Should().Be("demo-1");
    }

    [Test]
    public async Task A_live_follower_hears_the_final_whistle_when_the_lap_ends()
    {
        _time.Advance(MatchLength - TimeSpan.FromMinutes(2));
        var following = _source.FollowAsync("demo-1", null, Viewer.Default, CancellationToken.None).ToListAsync().AsTask();
        following.IsCompleted.Should().BeFalse("the lap is still being played");

        _time.Advance(TimeSpan.FromMinutes(3));
        var messages = await following;

        var fullTime = messages.Should().ContainSingle(m => m.Event == StreamEvents.FullTime).Subject;
        messages[^1].Should().BeSameAs(fullTime);
        ShouldCarryTheHighestId(messages, fullTime, DemoRecording.Events[^1].Sequence);
        ((LiveState)fullTime.Data).FullTime.Should().BeTrue();
    }

    // The client resumes from the last ID it saw, so full time must not carry an ID below one already sent.
    private static void ShouldCarryTheHighestId(IReadOnlyList<LiveMessage> messages, LiveMessage fullTime, long lastSequence)
    {
        var ids = messages.Where(m => m.Id is not null)
            .Select(m => long.Parse(m.Id!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        fullTime.Id.Should().Be(LiveMessage.IdFor(lastSequence));
        ids.Max().Should().Be(lastSequence);
    }

    [Test]
    public async Task Following_from_a_sequence_carries_on_after_it_with_commentary_and_an_overlay_per_moment()
    {
        _time.Advance(MatchLength);

        var messages = await _source.FollowAsync("demo-1", 100, Viewer.Default, CancellationToken.None).ToListAsync();

        messages.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence)
            .Should().Equal(Enumerable.Range(101, DemoRecording.Events.Count - 100).Select(i => (long)i));
        var moments = messages.Count(m => m.Event == StreamEvents.Moment);
        messages.Count(m => m.Event == StreamEvents.Commentary).Should().Be(moments);
        messages.Count(m => m.Event == StreamEvents.Overlay).Should().Be(moments);
        messages.Should().ContainSingle(m => m.Event == StreamEvents.FullTime);
    }

    [Test]
    public async Task Followers_who_join_at_different_times_get_the_same_messages_with_their_own_commentary()
    {
        _time.Advance(MatchLength);

        var early = await _source.FollowAsync("demo-1", 0, Viewer.Default, CancellationToken.None).ToListAsync();
        var (analyst, _) = Viewer.Create(ViewingMode.Analyst, null, null, null, null, _ => true, _ => true);
        var late = await _source.FollowAsync("demo-1", 1000, analyst!, CancellationToken.None).ToListAsync();

        late.Where(m => m.Event != StreamEvents.Commentary).Should().Equal(
            early.SkipWhile(m => long.Parse(m.Id!, System.Globalization.CultureInfo.InvariantCulture) <= 1000).Where(m => m.Event != StreamEvents.Commentary));
        late.Where(m => m.Event == StreamEvents.Commentary).Should().OnlyContain(m => ((CommentaryLine)m.Data).Mode == ViewingMode.Analyst);
    }

    [Test]
    public async Task A_live_follower_receives_each_event_when_its_time_comes()
    {
        var stream = _source.FollowAsync("demo-1", null, Viewer.Default, CancellationToken.None).GetAsyncEnumerator();
        await stream.MoveNextAsync();
        var snapshot = (LiveState)stream.Current.Data;

        var next = stream.MoveNextAsync().AsTask();
        next.IsCompleted.Should().BeFalse("the next event isn't due yet");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await next).Should().BeTrue();
        stream.Current.Id.Should().Be((snapshot.LastSequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await stream.DisposeAsync();
    }

    [Test]
    public void A_recording_that_does_not_start_with_its_metadata_is_refused()
    {
        var construct = () => new DemoMatchSource(
            DemoRecording.Events.Skip(1).ToList(),
            _teams,
            new KeyMomentDetector(),
            Options.Create(new DemoOptions()),
            _time);

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task A_replay_after_a_sequence_sends_only_the_latest_shape_after_the_last_event()
    {
        _time.Advance(MatchLength);

        var messages = await _source.FollowAsync("demo-1", 100, Viewer.Default, CancellationToken.None).ToListAsync();

        var shape = messages.Should().ContainSingle(m => m.Event == StreamEvents.Shape).Subject;
        messages.IndexOf(shape).Should().Be(messages.FindLastIndex(m => m.Event == StreamEvents.Event) + 1);
        var lastBallEvent = DemoRecording.Events.Last(e => e.Type is not (MatchEventType.MatchMetadata or MatchEventType.PeriodEnd));
        ((ShapeView)shape.Data).Sequence.Should().Be(lastBallEvent.Sequence);
        messages.Where(m => m.Event == StreamEvents.Event).Should().HaveCount(DemoRecording.Events.Count - 100);
    }
}
