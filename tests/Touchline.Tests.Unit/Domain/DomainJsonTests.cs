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
        End = new PitchPosition(105.8, 33.1),
        Successful = true,
        UnderPressure = true,
        Shot = TestEvents.Shot(0.41, ShotOutcome.Goal, ShotSituation.Counter, BodyPart.LeftFoot, weakFoot: true, speedKmh: 88.4)
    };

    // Added in the richer-events slice (ADR-019)

    [Test]
    public void Pass_carry_and_metadata_events_survive_a_json_round_trip_unchanged()
    {
        var events = new[]
        {
            _goal with { Type = MatchEventType.Pass, Shot = null, Pass = new PassDetail(0.62, 71.3, weakFoot: true, PassHeight.Ground) },
            _goal with { Type = MatchEventType.Carry, Shot = null, Carry = new CarryDetail(33.1) },
            _goal with { Type = MatchEventType.MatchMetadata, Shot = null, UnderPressure = false, Metadata = TestEvents.Metadata }
        };

        foreach (var original in events)
        {
            var back = JsonSerializer.Deserialize<MatchEvent>(JsonSerializer.Serialize(original, DomainJson.Options), DomainJson.Options);

            back.Should().BeEquivalentTo(original, original.Type.ToString());
        }
    }

    [Test]
    public void A_penalty_off_the_post_and_a_fixture_s_metadata_survive_a_json_round_trip()
    {
        var penalty = _goal with
        {
            Successful = false,
            Shot = new ShotDetail(0.76, ShotOutcome.OffTarget, ShotSituation.Penalty, BodyPart.LeftFoot, false, 96.2, new GoalMouthPosition(-3.66, 1.2), true, DiveDirection.Right)
        };
        var fixture = new MatchMetadata(
            TestEvents.Metadata.Home, TestEvents.Metadata.Away, "Northgate Stadium", DateTimeOffset.UnixEpoch, 9,
            TestEvents.Referee, MatchKind.Fixture, "2026-27", 4);
        var metadataEvent = _goal with { Type = MatchEventType.MatchMetadata, Shot = null, UnderPressure = false, PlayerId = null, End = null, Metadata = fixture };

        foreach (var original in new[] { penalty, metadataEvent })
        {
            var back = JsonSerializer.Deserialize<MatchEvent>(JsonSerializer.Serialize(original, DomainJson.Options), DomainJson.Options);

            back.Should().BeEquivalentTo(original, original.Type.ToString());
        }
    }

    [Test]
    public void A_body_part_sent_as_an_undefined_number_is_rejected_on_read()
    {
        var json = JsonSerializer.Serialize(_goal, DomainJson.Options).Replace("\"bodyPart\":\"LeftFoot\"", "\"bodyPart\":7", StringComparison.Ordinal);

        var read = () => JsonSerializer.Deserialize<MatchEvent>(json, DomainJson.Options);

        read.Should().Throw<ArgumentOutOfRangeException>("JSON accepts any number for an enum, so the constructor checks it");
    }

    [Test]
    public void A_detail_with_a_missing_constructor_value_is_rejected_on_read()
    {
        var json = JsonSerializer.Serialize(_goal, DomainJson.Options).Replace(",\"speedKmh\":88.4", "", StringComparison.Ordinal);

        var read = () => JsonSerializer.Deserialize<MatchEvent>(json, DomainJson.Options);

        read.Should().Throw<JsonException>("a missing speed must not quietly become 0").WithMessage("*speedKmh*");
    }

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
