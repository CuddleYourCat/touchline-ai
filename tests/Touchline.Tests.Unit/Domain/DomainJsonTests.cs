using System.Text.Json;
using FluentAssertions;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class DomainJsonTests
{
    private static readonly MatchEvent _goal = new()
    {
        Sequence = 1184,
        MatchId = "nbk-tvu-0001",
        PossessionId = 212,
        Time = MatchTime.At(MatchPeriod.SecondHalf, 63, 12),
        Type = MatchEventType.Shot,
        Team = TeamSide.Home,
        PlayerId = "nbfc-07",
        Start = new PitchPosition(94.2, 29.5),
        End = new PitchPosition(105, 33.1),
        Successful = true,
        Shot = new ShotDetail(0.41, ShotOutcome.Goal, ShotSituation.Counter)
    };

    [Test]
    public void A_match_event_survives_a_json_round_trip_unchanged()
    {
        var json = JsonSerializer.Serialize(_goal, DomainJson.Options);

        var back = JsonSerializer.Deserialize<MatchEvent>(json, DomainJson.Options);

        back.Should().BeEquivalentTo(_goal);
        back!.Start.Should().Be(new PitchPosition(94.2, 29.5), "struct positions must not deserialize to (0, 0)");
        back.Time.ToString().Should().Be("63:12", "struct times must not deserialize to 00:00");
        back.IsGoal.Should().BeTrue();
    }

    [Test]
    public void Enums_are_written_as_names_and_computed_properties_are_left_out()
    {
        var json = JsonSerializer.Serialize(_goal, DomainJson.Options);

        json.Should().Contain("\"type\":\"Shot\"").And.Contain("\"outcome\":\"Goal\"");
        json.Should().NotContain("isGoal").And.NotContain("onTarget");
    }

    [Test]
    public void An_off_pitch_position_in_json_is_rejected_on_read()
    {
        var json = JsonSerializer.Serialize(_goal, DomainJson.Options).Replace("\"x\":94.2", "\"x\":140", StringComparison.Ordinal);

        var read = () => JsonSerializer.Deserialize<MatchEvent>(json, DomainJson.Options);

        read.Should().Throw<ArgumentOutOfRangeException>("the constructor's validation also runs during deserialization");
    }

    [Test]
    public void Match_state_survives_a_json_round_trip()
    {
        var state = MatchState.KickOff(_goal.MatchId).Apply(_goal with { Sequence = 1 });

        var back = JsonSerializer.Deserialize<MatchState>(JsonSerializer.Serialize(state, DomainJson.Options), DomainJson.Options);

        back.Should().BeEquivalentTo(state);
    }
}
