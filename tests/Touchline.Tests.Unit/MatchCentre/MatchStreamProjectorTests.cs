using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Matches.Shape;
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

        moments.Count(m => m.Tier == MomentTier.Key).Should().Be(10);
        moments.Count(m => m.Tier == MomentTier.Notable).Should().Be(24);
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
    public void A_substitute_takes_the_departing_player_s_place_in_the_shape_from_his_substitution_on()
    {
        var events = DemoRecording.Events;
        var substitution = events.First(e => e.Type == MatchEventType.Substitution);
        var projector = new MatchStreamProjector(events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded());

        var shapes = events.SelectMany(e => projector.Apply(e).Messages)
            .Where(m => m.Event == StreamEvents.Shape)
            .Select(m => (ShapeView)m.Data)
            .ToList();

        var atSubstitution = shapes.Single(s => s.Sequence == substitution.Sequence);
        var before = shapes.Last(s => s.Sequence < substitution.Sequence);
        atSubstitution.Positions.Should().HaveCount(22, "a substitution has a shape of its own, so the pitch shows the new player at once");
        atSubstitution.Positions.Should().Contain(p => p.PlayerId == substitution.ReplacementPlayerId)
            .And.NotContain(p => p.PlayerId == substitution.PlayerId);
        var slot = before.Positions.ToList().FindIndex(p => p.PlayerId == substitution.PlayerId);
        atSubstitution.Positions[slot].PlayerId.Should().Be(substitution.ReplacementPlayerId, "he plays in the departing player's slot");
        shapes.Where(s => s.Sequence > substitution.Sequence).Should().OnlyContain(s => s.Positions.All(p => p.PlayerId != substitution.PlayerId));
    }

    // The demo up to its first substitution, which is replaced by the one given.
    private static List<MatchEvent> UpToFirstSubstitution(Func<MatchEvent, MatchEvent> replace)
    {
        var events = DemoRecording.Events;
        var first = events.First(e => e.Type == MatchEventType.Substitution);
        return [.. events.TakeWhile(e => e.Sequence < first.Sequence), replace(first)];
    }

    [Test]
    public void A_forward_who_replaces_a_defender_plays_up_front_and_the_defence_has_one_fewer()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var defender = Metadata.Home.StartingEleven[1];
        var events = UpToFirstSubstitution(s => s with { Team = TeamSide.Home, PlayerId = defender, ReplacementPlayerId = "nbfc-14" });
        var projector = new MatchStreamProjector(events[0].MatchId, _detector, teams);

        var shape = (ShapeView)events.Select(projector.Apply).ToList()[^1].Messages.Single(m => m.Event == StreamEvents.Shape).Data;

        teams["north-bank-fc"].FindPlayer(defender)!.Position.Should().Be(PlayerPosition.Defender);
        var eleven = Metadata.Home.StartingEleven.Select(id => id == defender ? "nbfc-14" : id).ToList();
        var expected = ShapeModel.Place(
            new ShapeTeam(new TeamSheet(Metadata.Home.Id, Metadata.Home.Name, Metadata.Home.ShortName, eleven), teams["north-bank-fc"]),
            new ShapeTeam(Metadata.Away, teams["thames-valley-united"]),
            ShapeModel.BallAfter(events[^2])!.Value,
            null);
        shape.Positions.Should().Equal(ShapeView.From(shape.Sequence, expected).Positions);
        var home = new ShapeTeam(new TeamSheet(Metadata.Home.Id, Metadata.Home.Name, Metadata.Home.ShortName, eleven), teams["north-bank-fc"]);
        home.Slots.Single(s => s.PlayerId == "nbfc-14").Line.Should().Be(PlayerPosition.Forward, "he plays in his own line, not the defender's");
        home.Slots.Count(s => s.Line == PlayerPosition.Defender).Should().Be(
            new ShapeTeam(Metadata.Home, teams["north-bank-fc"]).Slots.Count(s => s.Line == PlayerPosition.Defender) - 1);
    }

    [Test]
    public void A_half_time_substitution_is_drawn_with_the_ball_on_the_centre_spot()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var all = DemoRecording.Events;
        var halfTime = all.First(e => e is { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf });
        var off = Metadata.Home.StartingEleven[6];
        var substitution = halfTime with
        {
            Sequence = halfTime.Sequence + 1,
            Type = MatchEventType.Substitution,
            Time = MatchTime.At(MatchPeriod.SecondHalf, 45),
            PlayerId = off,
            ReplacementPlayerId = "nbfc-16",
            Start = new Touchline.Domain.Pitch.PitchPosition(52.5, 0)
        };
        var projector = new MatchStreamProjector(all[0].MatchId, _detector, teams);

        var shape = (ShapeView)all.TakeWhile(e => e.Sequence <= halfTime.Sequence).Append(substitution).Select(projector.Apply).ToList()[^1]
            .Messages.Single(m => m.Event == StreamEvents.Shape).Data;

        var eleven = Metadata.Home.StartingEleven.Select(id => id == off ? "nbfc-16" : id).ToList();
        var expected = ShapeModel.Place(
            new ShapeTeam(new TeamSheet(Metadata.Home.Id, Metadata.Home.Name, Metadata.Home.ShortName, eleven), teams["north-bank-fc"]),
            new ShapeTeam(Metadata.Away, teams["thames-valley-united"]),
            new Touchline.Domain.Pitch.PitchPosition(52.5, 34),
            null,
            all.Where(e => e.Sequence < halfTime.Sequence && e.Card == CardColour.Red).Select(e => e.PlayerId!).ToList());
        shape.Positions.Should().Equal(ShapeView.From(shape.Sequence, expected).Positions);
    }

    [Test]
    public void A_substitution_is_a_notable_moment_with_a_card_of_its_own()
    {
        var events = DemoRecording.Events;
        var projector = new MatchStreamProjector(events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded());

        var projections = events.Select(projector.Apply).Where(p => p.Notable is { Kind: NotableMomentKind.Substitution }).ToList();

        projections.Should().HaveCount(events.Count(e => e.Type == MatchEventType.Substitution));
        projections.Should().OnlyContain(p => p.Messages.Any(m => m.Event == StreamEvents.Moment) && p.Minor == null);
        var card = projections.SelectMany(p => p.Messages).Where(m => m.Event == StreamEvents.Overlay).Select(m => (OverlayCard)m.Data).First();
        card.Type.Should().Be("substitution");
        card.Payload.Title.Should().Be("SUBSTITUTION");
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

    [Test]
    public void A_hub_replay_keeps_every_shape_in_place()
    {
        var hub = HubOf(200);
        var projector = new MatchStreamProjector(DemoRecording.Events[0].MatchId, _detector, TeamProfileCatalog.LoadEmbedded());
        var expected = DemoRecording.Events.Take(200).SelectMany(e => projector.Apply(e).Messages).ToList();

        using var subscription = hub.Subscribe(after: 100, Viewer.Default, new StreamReplay(KeepShapes: true));

        var shapes = subscription.Backlog.Where(m => m.Event == StreamEvents.Shape).Select(m => ((ShapeView)m.Data).Sequence).ToList();
        shapes.Should().Equal(expected.Where(m => m.Event == StreamEvents.Shape).Select(m => ((ShapeView)m.Data).Sequence).Where(s => s > 100));
        shapes.Should().HaveCountGreaterThan(1);
    }

    [Test]
    public void A_hub_replay_from_a_match_time_starts_two_events_before_the_first_event_at_or_after_it()
    {
        var hub = HubOf(DemoRecording.Events.Count);
        var at = MatchTime.At(MatchPeriod.SecondHalf, 45);
        var first = DemoRecording.Events.First(e => e.Time.CompareTo(at) >= 0);

        using var subscription = hub.Subscribe(after: null, Viewer.Default, new StreamReplay(KeepShapes: true, at));

        var events = subscription.Backlog.Where(m => m.Event == StreamEvents.Event).Select(m => ((MatchEvent)m.Data).Sequence).ToList();
        events[0].Should().Be(first.Sequence - 1);
        subscription.Backlog.Should().NotContain(m => m.Event == StreamEvents.State && ((LiveState)m.Data).LastSequence < first.Sequence - 1);
    }

    [Test]
    public void A_hub_replay_from_a_time_not_reached_yet_starts_from_now()
    {
        var hub = HubOf(100);

        using var subscription = hub.Subscribe(after: null, Viewer.Default, new StreamReplay(KeepShapes: true, MatchTime.At(MatchPeriod.SecondHalf, 80)));

        subscription.Backlog.Should().ContainSingle().Which.Event.Should().Be(StreamEvents.State);
    }

    private static LiveMatchHub HubOf(int events)
    {
        var hub = new LiveMatchHub(
            DemoRecording.Events[0].MatchId, _detector, new FakeTimeProvider(), 8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<MatchEvent>>([]), NullLogger.Instance, TeamProfileCatalog.LoadEmbedded());
        hub.Load(DemoRecording.Events.Take(events), []);
        return hub;
    }
}
