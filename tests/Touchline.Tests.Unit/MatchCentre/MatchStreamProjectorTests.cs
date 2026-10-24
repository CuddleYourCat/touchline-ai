using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
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

        moments.Count(m => m.Tier == MomentTier.Key).Should().Be(9);
        moments.Count(m => m.Tier == MomentTier.Notable).Should().Be(16);
        moments.Count(m => m.Kind == nameof(KeyMomentKind.Goal)).Should().Be(6);
        moments.Should().OnlyContain(m => m.Id == $"{DemoRecording.Events[0].MatchId}:{m.Sequence}");
    }

    [Test]
    public void Full_time_is_sent_once_and_last_with_the_final_score()
    {
        var messages = ProjectWholeMatch();

        messages.Count(m => m.Event == StreamEvents.FullTime).Should().Be(1);
        messages[^1].Event.Should().Be(StreamEvents.FullTime);
        ((LiveState)messages[^1].Data).Score.Should().Be(new Score(3, 3));
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
    public void A_shape_of_all_22_players_follows_each_event_with_the_ball_in_it()
    {
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded());
        projector.Apply(DemoRecording.Events[0]);
        var ballEvent = DemoRecording.Events.Skip(1).First(e => e.Type == MatchEventType.Pass);
        foreach (var earlier in DemoRecording.Events.Skip(1).TakeWhile(e => e.Sequence < ballEvent.Sequence))
        {
            projector.Apply(earlier);
        }

        var messages = projector.Apply(ballEvent).Messages;

        messages.Select(m => m.Event).Should().ContainInOrder(StreamEvents.Event, StreamEvents.Shape, StreamEvents.State);
        var shape = (ShapeView)messages.Single(m => m.Event == StreamEvents.Shape).Data;
        shape.Sequence.Should().Be(ballEvent.Sequence);
        shape.Positions.Should().HaveCount(22);
        shape.Positions.Should().OnlyContain(p => Math.Round(p.X, 1) == p.X && Math.Round(p.Y, 1) == p.Y);
    }

    [Test]
    public void No_shape_is_sent_for_an_event_without_the_ball_in_it()
    {
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded());

        var byType = DemoRecording.Events.Select(e => (e.Type, projector.Apply(e).Messages)).ToList();

        byType.Where(p => p.Type is MatchEventType.MatchMetadata or MatchEventType.PeriodEnd)
            .Should().NotBeEmpty().And.OnlyContain(p => p.Messages.All(m => m.Event != StreamEvents.Shape));
        byType.Count(p => p.Messages.Any(m => m.Event == StreamEvents.Shape))
            .Should().Be(byType.Count(p => p.Type is not (MatchEventType.MatchMetadata or MatchEventType.PeriodEnd)));
    }

    [Test]
    public void Momentum_is_sent_for_each_window_once_it_closes_and_agrees_with_the_whole_match_reading()
    {
        var sent = ProjectWholeMatch().Where(m => m.Event == StreamEvents.Momentum).Select(m => (MomentumReading)m.Data).ToList();

        sent.Should().Equal(Momentum.Read(DemoRecording.Events));
    }

    private static MatchMetadata Metadata => DemoRecording.Events[0].Metadata!;

    [Test]
    public void Shapes_start_without_the_metadata_event_when_the_line_ups_are_given()
    {
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded(), lineups: Metadata);
        var firstBallEvent = DemoRecording.Events.Skip(1).First(e => e.Type != MatchEventType.PeriodEnd);

        var messages = projector.Apply(firstBallEvent).Messages;

        ((ShapeView)messages.Single(m => m.Event == StreamEvents.Shape).Data).Positions.Should().HaveCount(22);
    }

    [Test]
    public void A_team_missing_from_the_profiles_still_gets_a_shape()
    {
        var onlyHome = TeamProfileCatalog.LoadEmbedded().Where(p => p.Key == Metadata.Home.Id).ToDictionary();
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector, onlyHome, lineups: Metadata);
        var firstBallEvent = DemoRecording.Events.Skip(1).First(e => e.Type != MatchEventType.PeriodEnd);

        var shape = (ShapeView)projector.Apply(firstBallEvent).Messages.Single(m => m.Event == StreamEvents.Shape).Data;

        shape.Positions.Should().HaveCount(22);
    }

    [Test]
    public void A_red_card_takes_the_player_out_of_every_later_shape()
    {
        var events = DemoRecording.Events;
        var sentOff = Metadata.Home.StartingEleven[3];
        var redAt = events.First(e => e.Type == MatchEventType.Pass && e.Sequence > 10);
        var red = redAt with { Type = MatchEventType.Card, Card = CardColour.Red, PlayerId = sentOff, Team = TeamSide.Home, Pass = null, RecipientPlayerId = null, FoulSequence = redAt.Sequence - 1 };
        var projector = new MatchStreamProjector(events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded());

        var shapes = events.Select(e => e.Sequence == red.Sequence ? red : e)
            .SelectMany(e => projector.Apply(e).Messages)
            .Where(m => m.Event == StreamEvents.Shape)
            .Select(m => (ShapeView)m.Data)
            .ToList();

        var later = shapes.Where(s => s.Sequence >= red.Sequence).ToList();
        later.Should().NotBeEmpty().And.OnlyContain(s => s.Positions.Count == 21 && s.Positions.All(p => p.PlayerId != sentOff));
        shapes.Where(s => s.Sequence < red.Sequence).Should().OnlyContain(s => s.Positions.Count == 22);
    }

    [Test]
    public void A_hub_replay_after_a_sequence_sends_only_the_latest_shape_after_the_last_event()
    {
        var hub = new LiveMatchHub(
            DemoRecording.Events[0].MatchId, _detector, new FakeTimeProvider(), 8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<MatchEvent>>([]), NullLogger.Instance, TeamProfileCatalog.LoadEmbedded());
        hub.Load(DemoRecording.Events.Take(200), []);

        using var subscription = hub.Subscribe(after: 100, Viewer.Default);

        var backlog = subscription.Backlog.ToList();
        var shape = backlog.Should().ContainSingle(m => m.Event == StreamEvents.Shape).Subject;
        backlog.IndexOf(shape).Should().Be(backlog.FindLastIndex(m => m.Event == StreamEvents.Event) + 1);
        var lastBallEvent = DemoRecording.Events.Take(200).Last(e => e.Type is not (MatchEventType.MatchMetadata or MatchEventType.PeriodEnd));
        ((ShapeView)shape.Data).Sequence.Should().Be(lastBallEvent.Sequence);
        backlog.Count(m => m.Event == StreamEvents.Event).Should().Be(100);
    }
}
