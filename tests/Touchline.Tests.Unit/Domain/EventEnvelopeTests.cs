using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class EventEnvelopeTests
{
    private static readonly MatchEvent _pass = new MatchEvent
    {
        Sequence = 12,
        MatchId = "nbk-tvu-42",
        PossessionId = 3,
        Time = MatchTime.At(MatchPeriod.FirstHalf, 4, 10),
        Type = MatchEventType.Pass,
        Team = TeamSide.Home,
        PlayerId = "nbfc-05",
        Start = new PitchPosition(40, 30)
    }.WithRequiredDetail();

    private static Dictionary<string, object> EnvelopeWith(string property, object? value)
    {
        var envelope = new Dictionary<string, object>(EventEnvelope.For(_pass));
        if (value is null)
        {
            envelope.Remove(property);
        }
        else
        {
            envelope[property] = value;
        }

        return envelope;
    }

    [Test]
    public void An_event_s_own_envelope_is_accepted_with_its_match_and_sequence()
    {
        var check = EventEnvelope.Check(EventEnvelope.For(_pass));

        check.Accepted.Should().BeTrue();
        check.MatchId.Should().Be("nbk-tvu-42");
        check.Sequence.Should().Be(12);
    }

    [Test]
    public void An_unknown_event_type_is_skipped_but_still_placed_in_its_match()
    {
        var check = EventEnvelope.Check(EnvelopeWith(EventEnvelope.TypeProperty, "Injury"));

        check.Skipped.Should().Be(SkipReason.UnknownType, "a tolerant reader skips what it doesn't know rather than failing");
        check.MatchId.Should().Be("nbk-tvu-42", "so the match's sequence can still move past it");
        check.Sequence.Should().Be(12);
    }

    [Test]
    public void An_event_type_sent_as_a_number_is_treated_as_unknown()
    {
        EventEnvelope.Check(EnvelopeWith(EventEnvelope.TypeProperty, "6")).Skipped.Should().Be(SkipReason.UnknownType);
    }

    [TestCase("2.0", SkipReason.NewerSchema, TestName = "A newer major schema version is skipped")]
    [TestCase("1.7", null, TestName = "A newer minor schema version is still read")]
    [TestCase("banana", SkipReason.MissingEnvelope, TestName = "A malformed schema version is skipped")]
    public void The_schema_version_s_major_number_decides(string version, SkipReason? expected)
    {
        EventEnvelope.Check(EnvelopeWith(EventEnvelope.SchemaVersionProperty, version)).Skipped.Should().Be(expected);
    }

    [TestCase(EventEnvelope.MatchIdProperty, TestName = "A message without a match ID is skipped and unplaceable")]
    [TestCase(EventEnvelope.SequenceProperty, TestName = "A message without a sequence is skipped and unplaceable")]
    public void A_message_without_its_match_or_sequence_is_skipped(string missing)
    {
        var check = EventEnvelope.Check(EnvelopeWith(missing, null));

        check.Skipped.Should().Be(SkipReason.MissingEnvelope);
        check.MatchId.Should().BeNull();
    }

    [TestCase(12)]
    [TestCase("12")]
    public void A_sequence_that_comes_back_as_another_type_is_still_read(object sequence)
    {
        EventEnvelope.Check(EnvelopeWith(EventEnvelope.SequenceProperty, sequence)).Sequence.Should().Be(12);
    }
}
