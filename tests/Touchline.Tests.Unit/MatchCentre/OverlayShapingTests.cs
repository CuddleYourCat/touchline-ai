using System.Globalization;
using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The pure per-viewer shaping of overlay cards (ADR-055).</summary>
[TestFixture]
public class OverlayShapingTests
{
    private static readonly ClockReading _at = ClockReading.From(MatchTime.At(MatchPeriod.FirstHalf, 12));
    private static readonly OverlayContext _shot = new(0.3449, 1.2, 0.8);
    private static readonly OverlayContext _noShot = new(null, 1.2, 0.8);

    private static readonly OverlayCard _goal = new("m:12:overlay", _at, "goal", OverlayCardBuilder.KeyDurationMs,
        new OverlayPayload("GOAL", null, TeamSide.Home, "p-1"), OverlayCardBuilder.HighPriority);

    private static readonly OverlayCard _corner = new("m:20:overlay", _at, "corner", OverlayCardBuilder.NotableDurationMs,
        new OverlayPayload("CORNER", null, TeamSide.Away, null), OverlayCardBuilder.NormalPriority);

    private static readonly OverlayCard _throwIn = new("m:30:overlay", _at, "throwin", OverlayCardBuilder.MinorDurationMs,
        new OverlayPayload("THROW-IN", null, TeamSide.Home, null), OverlayCardBuilder.LowPriority);

    private static Viewer ViewerOf(ViewingMode mode, string? metric = null) =>
        Viewer.Create(mode, null, null, null, metric, _ => true, _ => true).Viewer!;

    [Test]
    public void A_casual_viewer_gets_a_plain_title_with_no_subtitle_even_when_the_neutral_card_had_one()
    {
        var neutral = _goal with { Payload = _goal.Payload with { Subtitle = "anything" } };

        var shaped = OverlayShaping.Shape(neutral, _shot, ViewerOf(ViewingMode.Casual, "xG"));

        shaped!.Payload.Title.Should().Be("Goal!");
        shaped.Payload.Subtitle.Should().BeNull();
        shaped.Id.Should().Be(_goal.Id);
    }

    [TestCase("goal", "GOAL", "Goal!")]
    [TestCase("bigchance", "BIG CHANCE", "Big chance")]
    [TestCase("penaltyawarded", "PENALTY", "Penalty")]
    [TestCase("redcard", "RED CARD", "Red card")]
    [TestCase("penaltysaved", "PENALTY SAVED", "Penalty saved")]
    [TestCase("penaltymissed", "PENALTY MISSED", "Penalty missed")]
    [TestCase("clearedoffline", "OFF THE LINE", "Cleared off the line")]
    [TestCase("yellowcard", "YELLOW CARD", "Yellow card")]
    [TestCase("corner", "CORNER", "Corner")]
    [TestCase("woodwork", "WOODWORK", "Off the woodwork")]
    public void A_casual_viewer_gets_a_friendly_sentence_case_title_and_an_analyst_keeps_the_broadcast_one(string type, string neutralTitle, string friendly)
    {
        var neutral = _corner with { Type = type, Payload = _corner.Payload with { Title = neutralTitle } };
        var copy = neutral with { };

        OverlayShaping.Shape(neutral, _noShot, ViewerOf(ViewingMode.Casual))!.Payload.Title.Should().Be(friendly);
        OverlayShaping.Shape(neutral, _noShot, ViewerOf(ViewingMode.Analyst))!.Payload.Title.Should().Be(neutralTitle);
        neutral.Should().Be(copy, "the neutral card is never changed");
    }

    [Test]
    public void A_card_type_with_no_friendly_title_keeps_its_neutral_title_for_a_casual_viewer()
    {
        var unknown = _corner with { Type = "milestone", Payload = _corner.Payload with { Title = "FIRST BOOKING" } };

        OverlayShaping.Shape(unknown, _noShot, ViewerOf(ViewingMode.Casual))!.Payload.Title.Should().Be("FIRST BOOKING");
    }

    [Test]
    public void A_casual_viewer_gets_no_low_priority_card()
    {
        OverlayShaping.Shape(_throwIn, _noShot, ViewerOf(ViewingMode.Casual)).Should().BeNull();
    }

    [Test]
    public void An_analyst_gets_the_shots_xg_as_a_subtitle_whatever_their_favourite_metric()
    {
        OverlayShaping.Shape(_goal, _shot, ViewerOf(ViewingMode.Analyst))!.Payload.Subtitle.Should().Be("xG 0.34");
        OverlayShaping.Shape(_goal, _shot, ViewerOf(ViewingMode.Analyst, "xG"))!.Payload.Subtitle.Should().Be("xG 0.34", "the shot's own xG wins over the match totals");
    }

    [Test]
    public void The_xg_subtitle_does_not_depend_on_the_current_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            OverlayShaping.Shape(_goal, _shot, ViewerOf(ViewingMode.Analyst))!.Payload.Subtitle.Should().Be("xG 0.34");
            OverlayShaping.Shape(_corner, _noShot, ViewerOf(ViewingMode.Analyst, "xG"))!.Payload.Subtitle.Should().Be("xG 1.20 - 0.80");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [TestCase("xG")]
    [TestCase("xg")]
    [TestCase("XG")]
    public void A_non_shot_card_shows_the_match_xg_only_for_an_analyst_whose_favourite_metric_is_xg(string metric)
    {
        OverlayShaping.Shape(_corner, _noShot, ViewerOf(ViewingMode.Analyst, metric))!.Payload.Subtitle.Should().Be("xG 1.20 - 0.80");
    }

    [TestCase(null)]
    [TestCase("passes")]
    public void A_non_shot_card_has_no_subtitle_for_any_other_favourite_metric(string? metric)
    {
        OverlayShaping.Shape(_corner, _noShot, ViewerOf(ViewingMode.Analyst, metric))!.Payload.Subtitle.Should().BeNull();
    }

    [Test]
    public void A_non_shot_card_has_no_subtitle_when_the_match_xg_is_not_known()
    {
        OverlayShaping.Shape(_corner, OverlayContext.None, ViewerOf(ViewingMode.Analyst, "xG"))!.Payload.Subtitle.Should().BeNull("a metric is never invented");
        OverlayShaping.Shape(_corner, new OverlayContext(null, 1.2, null), ViewerOf(ViewingMode.Analyst, "xG"))!.Payload.Subtitle.Should().BeNull();
    }

    [Test]
    public void An_analyst_keeps_the_low_priority_card()
    {
        var shaped = OverlayShaping.Shape(_throwIn, _noShot, ViewerOf(ViewingMode.Analyst));

        shaped.Should().NotBeNull();
        shaped!.Priority.Should().Be(OverlayCardBuilder.LowPriority);
    }

    [Test]
    public void Shaping_keeps_the_id_time_duration_type_and_priority_and_never_changes_the_neutral_card()
    {
        var copy = _goal with { };

        var shaped = OverlayShaping.Shape(_goal, _shot, ViewerOf(ViewingMode.Analyst))!;

        shaped.Should().BeEquivalentTo(_goal, options => options.Excluding(c => c.Payload));
        shaped.Payload.Should().BeEquivalentTo(_goal.Payload, options => options.Excluding(p => p.Subtitle));
        _goal.Should().Be(copy);
        _goal.Payload.Subtitle.Should().BeNull();
    }

    [Test]
    public void The_builder_marks_key_notable_and_minor_cards_high_normal_and_low()
    {
        var events = DemoRecording.Events;
        var detector = new KeyMomentDetector(teams: TeamProfileCatalog.LoadEmbedded().Values);
        var projector = new MatchStreamProjector(events[0].MatchId, detector);
        var projections = events.Select(projector.Apply).ToList();

        var cards = projections.SelectMany(p => p.Messages).Where(m => m.Event == StreamEvents.Overlay).Select(m => (OverlayCard)m.Data).ToList();
        var minor = projections.Where(p => p.Minor?.Card is not null).Select(p => p.Minor!.Card!).ToList();

        cards.Where(c => c.DurationMs == OverlayCardBuilder.KeyDurationMs).Should().OnlyContain(c => c.Priority == "high");
        cards.Where(c => c.DurationMs == OverlayCardBuilder.NotableDurationMs).Should().OnlyContain(c => c.Priority == "normal");
        minor.Should().NotBeEmpty().And.OnlyContain(c => c.Priority == "low" && c.DurationMs == OverlayCardBuilder.MinorDurationMs);
    }

    [Test]
    public void Apply_gives_a_casual_viewer_the_shared_messages_untouched_when_there_is_no_overlay()
    {
        var shared = new List<LiveMessage> { new(StreamEvents.Event, "30", new object()), new(StreamEvents.State, "30", new object()) };
        var minor = new MinorExtras(MomentOf("30"), _throwIn);

        OverlayShaping.Apply(shared, _noShot, minor, ViewerOf(ViewingMode.Casual)).Should().BeSameAs(shared);
    }

    [Test]
    public void Apply_puts_the_minor_moment_and_card_before_the_state_for_an_analyst_only()
    {
        var shared = new List<LiveMessage> { new(StreamEvents.Event, "30", new object()), new(StreamEvents.State, "30", new object()) };
        var minor = new MinorExtras(MomentOf("30"), _throwIn);

        OverlayShaping.Apply(shared, _noShot, minor, ViewerOf(ViewingMode.Analyst)).Select(m => m.Event)
            .Should().Equal(StreamEvents.Event, StreamEvents.Moment, StreamEvents.Overlay, StreamEvents.State);
        shared.Should().HaveCount(2, "the shared list is never changed");
    }

    [Test]
    public void Apply_sends_a_minor_moment_without_a_card_when_the_card_was_held_back()
    {
        var shared = new List<LiveMessage> { new(StreamEvents.Event, "30", new object()), new(StreamEvents.State, "30", new object()) };

        OverlayShaping.Apply(shared, _noShot, new MinorExtras(MomentOf("30"), null), ViewerOf(ViewingMode.Analyst)).Select(m => m.Event)
            .Should().Equal(StreamEvents.Event, StreamEvents.Moment, StreamEvents.State);
    }

    [Test]
    public void Apply_shapes_the_shared_overlay_for_each_viewer_without_changing_the_message_ids()
    {
        var shared = new List<LiveMessage> { new(StreamEvents.Event, "12", new object()), new(StreamEvents.Overlay, "12", _goal), new(StreamEvents.State, "12", new object()) };

        var casual = OverlayShaping.Apply(shared, _shot, null, ViewerOf(ViewingMode.Casual));
        var analyst = OverlayShaping.Apply(shared, _shot, null, ViewerOf(ViewingMode.Analyst));

        ((OverlayCard)casual[1].Data).Payload.Subtitle.Should().BeNull();
        ((OverlayCard)analyst[1].Data).Payload.Subtitle.Should().Be("xG 0.34");
        casual.Concat(analyst).Select(m => m.Id).Should().OnlyContain(id => id == "12");
    }

    private static MomentView MomentOf(string sequence) =>
        new($"m:{sequence}", MomentTier.Minor, "ThrowIn", long.Parse(sequence, CultureInfo.InvariantCulture), _at, TeamSide.Home, null, "reason", null);
}
