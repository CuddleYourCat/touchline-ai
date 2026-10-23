using FluentAssertions;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class MatchStreamProjectorTests
{
    private static readonly KeyMomentDetector _detector = new(teams: TeamProfileCatalog.LoadEmbedded().Values);

    private static List<LiveMessage> ProjectWholeMatch()
    {
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector);
        return [.. DemoRecording.Events.SelectMany(e => projector.Apply(e).Messages)];
    }

    [Test]
    public void Every_event_is_sent_followed_by_the_state_it_leads_to()
    {
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector);

        var messages = projector.Apply(DemoRecording.Events[0]).Messages;

        messages.Select(m => m.Event).Should().Equal(StreamEvents.Event, StreamEvents.State);
        messages.Should().OnlyContain(m => m.Id == "1");
        ((LiveState)messages[1].Data).LastSequence.Should().Be(1);
    }

    [Test]
    public void Each_key_moment_is_sent_once_with_the_event_that_triggered_it()
    {
        var moments = ProjectWholeMatch().Where(m => m.Event == StreamEvents.Moment).Select(m => (MomentView)m.Data).ToList();

        moments.Should().HaveCount(8);
        moments.Count(m => m.Kind == nameof(KeyMomentKind.Goal)).Should().Be(5);
        moments.Should().OnlyContain(m => m.Id == $"{DemoRecording.Events[0].MatchId}:{m.Sequence}");
    }

    [Test]
    public void Full_time_is_sent_once_and_last_with_the_final_score()
    {
        var messages = ProjectWholeMatch();

        messages.Count(m => m.Event == StreamEvents.FullTime).Should().Be(1);
        messages[^1].Event.Should().Be(StreamEvents.FullTime);
        ((LiveState)messages[^1].Data).Score.Should().Be(new Score(3, 2));
    }

    [Test]
    public void A_match_with_lost_events_projects_with_the_gaps_skipped()
    {
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector);
        var withGaps = DemoRecording.Events.Where(e => e.Sequence is not (5 or 6 or 40)).ToList();

        var project = () => withGaps.SelectMany(e => projector.Apply(e).Messages).ToList();

        project.Should().NotThrow().Subject[^1].Event.Should().Be(StreamEvents.FullTime);
        projector.State.LastSequence.Should().Be(DemoRecording.Events[^1].Sequence);
    }

    [Test]
    public void Momentum_is_sent_for_each_window_once_it_closes_and_agrees_with_the_whole_match_reading()
    {
        var sent = ProjectWholeMatch().Where(m => m.Event == StreamEvents.Momentum).Select(m => (MomentumReading)m.Data).ToList();

        sent.Should().Equal(Momentum.Read(DemoRecording.Events));
    }
}
