extern alias simulator;
using System.Text.Json;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class DemoRecordingTests
{
    [Test]
    public void The_recording_is_exactly_the_match_the_engine_plays_for_its_seed()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var played = new MatchSimulator(teams[DemoRecording.HomeTeamId], teams[DemoRecording.AwayTeamId], DemoRecording.Seed).Play().ToList();

        if (Snapshots.Updating)
        {
            using var file = File.Create(Snapshots.PathOf("src", "Touchline.MatchCentre", "Demo", DemoRecording.ResourceName));
            DemoRecording.Write(played, file);
            Assert.Pass("The demo recording was regenerated. Rebuild before running the tests again.");
        }

        Lines(DemoRecording.Events).Should().Equal(Lines(played), Snapshots.StaleMessage("The demo recording"));
    }

    [Test]
    public void The_recording_is_the_lively_three_three_the_demo_promises()
    {
        // Projected the way viewers get it, so the promise is about the timeline the demo shows.
        var projector = new MatchStreamProjector(
            DemoRecording.Events[0].MatchId, new KeyMomentDetector(teams: TeamProfileCatalog.LoadEmbedded().Values));
        var projections = DemoRecording.Events.Select(projector.Apply).ToList();
        var moments = projections.Select(p => p.Moment).OfType<KeyMoment>().ToList();
        var final = projections[^1].State;

        final.FullTime.Should().BeTrue();
        final.Score.Should().Be("3–3");
        moments.Should().NotBeEmpty();
        moments[0].Trigger.Time.Clock.Should().BeLessThan(TimeSpan.FromMinutes(2), "the demo should have action from the first minute");
        moments.Count(m => m.Trigger.Time.Period == MatchPeriod.FirstHalf).Should().BeGreaterThanOrEqualTo(4);
        moments.Count(m => m.Trigger.Time.Period == MatchPeriod.SecondHalf).Should().BeGreaterThanOrEqualTo(4);
        moments.Should().Contain(m => m.Kind == KeyMomentKind.PenaltyAwarded && m.Trigger.Time.Clock >= TimeSpan.FromMinutes(90));
        projections.SelectMany(p => p.Messages).Count(m => m.Event == StreamEvents.Moment)
            .Should().Be(moments.Count + projections.Count(p => p.Notable is { IsMinor: false }));
    }

    [Test]
    public void A_recording_round_trips_through_write_and_read()
    {
        using var buffer = new MemoryStream();
        DemoRecording.Write(DemoRecording.Events.Take(50), buffer);

        var read = DemoRecording.Read(new MemoryStream(buffer.ToArray()));

        Lines(read).Should().Equal(Lines(DemoRecording.Events.Take(50)));
    }

    private static List<string> Lines(IEnumerable<MatchEvent> events) =>
        [.. events.Select(e => JsonSerializer.Serialize(e, DomainJson.Options))];
}
