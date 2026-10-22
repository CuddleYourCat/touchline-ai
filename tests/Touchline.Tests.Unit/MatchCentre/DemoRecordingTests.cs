extern alias simulator;
using System.Text.Json;
using FluentAssertions;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Demo;

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
    public void The_recording_is_the_three_two_with_two_penalties_the_demo_promises()
    {
        var final = DemoRecording.Events.Aggregate(MatchState.KickOff(DemoRecording.Events[0].MatchId), (state, e) => state.Apply(e));

        final.FullTime.Should().BeTrue();
        final.Score.Should().Be("3–2");
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
