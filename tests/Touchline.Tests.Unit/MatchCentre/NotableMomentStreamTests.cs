using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;
using Touchline.MatchCentre.Live;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>Notable moments and overlay cards on the projector and the live hub (ADR-046, ADR-047).</summary>
[TestFixture]
public class NotableMomentStreamTests
{
    private static readonly KeyMomentDetector _detector = new(teams: TeamProfileCatalog.LoadEmbedded().Values);
    private static readonly IReadOnlyList<MatchEvent> _events = DemoRecording.Events;
    private static readonly string _matchId = _events[0].MatchId;

    private static LiveMatchHub NewHub(int buffer = 4096)
    {
        var hub = new LiveMatchHub(
            _matchId, _detector, new FakeTimeProvider(), buffer,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<MatchEvent>>([]),
            NullLogger.Instance);
        hub.Load([], []);
        return hub;
    }

    private static Viewer ViewerOf(ViewingMode mode, string language) =>
        Viewer.Create(mode, language, null, null, null, _ => true, _ => true).Viewer!;

    private static List<LiveMessage> Drain(Subscription subscription)
    {
        var messages = new List<LiveMessage>();
        while (subscription.Live.TryRead(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static IEnumerable<OverlayCard> Cards(IEnumerable<LiveMessage> messages) =>
        messages.Where(m => m.Event == StreamEvents.Overlay).Select(m => (OverlayCard)m.Data);

    [Test]
    public void Over_the_seeded_match_every_key_and_notable_moment_gets_one_card_and_a_minor_one_gets_none()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var projections = _events.Select(projector.Apply).ToList();

        var cards = Cards(projections.SelectMany(p => p.Messages)).ToList();

        cards.Should().HaveCount(30, "34 moments, less four notable cards held back by a key card just before");
        cards.Count(c => c.DurationMs == OverlayCardBuilder.KeyDurationMs).Should().Be(10);
        cards.Count(c => c.DurationMs == OverlayCardBuilder.NotableDurationMs).Should().Be(20);
        cards.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        cards.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Count()).Should().Contain("yellowcard", 6).And.Contain("corner", 10).And.Contain("substitution", 4);
        projections.Count(p => p.Notable is { IsMinor: true }).Should().Be(50, "42 throw-ins and 8 goal kicks are carried but give no card");
        projections.SelectMany(p => p.Messages).Count(m => m.Event == StreamEvents.Moment).Should().Be(34, "a skipped card does not skip the moment");
    }

    [Test]
    public void A_notable_card_is_held_back_when_a_key_card_went_out_within_three_events()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var projections = _events.Select(projector.Apply).ToList();

        var held = projections
            .Where(p => p.Notable is { IsMinor: false } && !p.Messages.Any(m => m.Event == StreamEvents.Overlay))
            .ToList();

        held.Should().HaveCount(4);
        foreach (var projection in held)
        {
            var sequence = projection.Notable!.Trigger.Sequence;
            projection.Messages.Should().Contain(m => m.Event == StreamEvents.Moment, "the moment itself still goes out");
            projections.Any(p => p.Moment is { } key && sequence - key.Trigger.Sequence is > 0 and <= MatchStreamProjector.NotableCardQuietEvents)
                .Should().BeTrue("a key card went out just before");
        }

        // Every other notable moment, including those 4 or more events after a key moment, kept its card.
        projections.Count(p => p.Notable is { IsMinor: false } && p.Messages.Any(m => m.Event == StreamEvents.Overlay)).Should().Be(20);
    }

    [Test]
    public void A_goal_followed_by_a_substitution_shows_one_card_the_key_one()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var projections = _events.Select(projector.Apply).ToList();

        var pair = projections.Zip(projections.Skip(1))
            .First(p => p.First.Moment is { Kind: KeyMomentKind.Goal } && p.Second.Notable is { Kind: NotableMomentKind.Substitution });

        Cards(pair.First.Messages).Should().ContainSingle().Which.Type.Should().Be("goal");
        Cards(pair.Second.Messages).Should().BeEmpty();
        pair.Second.Messages.Should().Contain(m => m.Event == StreamEvents.Moment);
    }

    [Test]
    public void A_notable_card_after_the_quiet_window_is_shown_again()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var key = _events.First(e => e.IsGoal);
        var yellow = _events.First(e => e.Sequence > key.Sequence + MatchStreamProjector.NotableCardQuietEvents && e.Card == CardColour.Yellow);

        var shown = _events.TakeWhile(e => e.Sequence <= yellow.Sequence).Select(projector.Apply).Last();

        Cards(shown.Messages).Should().ContainSingle().Which.Type.Should().Be("yellowcard");
    }

    [Test]
    public void Every_key_moment_is_about_the_side_the_rule_says()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var keys = _events.Select(projector.Apply).Where(p => p.Moment is not null).ToList();

        foreach (var projection in keys)
        {
            var key = projection.Moment!;
            var card = Cards(projection.Messages).Single();
            var view = projection.Messages.Where(m => m.Event == StreamEvents.Moment).Select(m => (MomentView)m.Data).Single();
            var expected = key.Kind == KeyMomentKind.PenaltyAwarded ? (key.Trigger.Team == TeamSide.Home ? TeamSide.Away : TeamSide.Home) : key.Trigger.Team;

            card.Payload.Team.Should().Be(expected);
            view.Team.Should().Be(expected);
            card.Payload.PlayerId.Should().Be(view.PlayerId);
        }
    }

    [Test]
    public void A_penalty_awarded_is_about_the_side_fouled_and_names_the_fouled_player()
    {
        var foul = new MatchEvent
        {
            Sequence = 9,
            MatchId = _matchId,
            PossessionId = 1,
            Time = MatchTime.At(MatchPeriod.SecondHalf, 70),
            Type = MatchEventType.Foul,
            Team = TeamSide.Home,
            PlayerId = "nbfc-04",
            FouledPlayerId = "tvu-09",
            Start = new Touchline.Domain.Pitch.PitchPosition(8, 34)
        };
        var moment = new KeyMoment(KeyMomentKind.PenaltyAwarded, foul, "penalty");

        moment.Side.Should().Be(TeamSide.Away);
        moment.FeaturedPlayerId.Should().Be("tvu-09");
        OverlayCardBuilder.For(moment, _matchId).Payload.Should().Be(new OverlayPayload("PENALTY", null, TeamSide.Away, "tvu-09"));
        MomentViews.From(moment, _matchId, null).Team.Should().Be(TeamSide.Away);
    }

    [TestCase(KeyMomentKind.Goal, TeamSide.Away, TeamSide.Away)]
    [TestCase(KeyMomentKind.BigChance, TeamSide.Home, TeamSide.Home)]
    [TestCase(KeyMomentKind.PenaltySaved, TeamSide.Home, TeamSide.Home)]
    [TestCase(KeyMomentKind.PenaltyMissed, TeamSide.Away, TeamSide.Away)]
    [TestCase(KeyMomentKind.ClearedOffLine, TeamSide.Home, TeamSide.Home)]
    [TestCase(KeyMomentKind.RedCard, TeamSide.Away, TeamSide.Away)]
    [TestCase(KeyMomentKind.PenaltyAwarded, TeamSide.Away, TeamSide.Home)]
    [TestCase(KeyMomentKind.PenaltyAwarded, TeamSide.Home, TeamSide.Away)]
    public void Each_key_kind_is_about_its_side(KeyMomentKind kind, TeamSide triggerTeam, TeamSide expected)
    {
        var trigger = _events.First() with { Type = MatchEventType.Pass, Team = triggerTeam };

        new KeyMoment(kind, trigger, "reason").Side.Should().Be(expected);
    }

    [Test]
    public void A_card_is_neutral_and_its_id_is_the_moments()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var messages = _events.SelectMany(e => projector.Apply(e).Messages).ToList();

        var yellow = Cards(messages).First(c => c.Type == "yellowcard");
        var goal = Cards(messages).First(c => c.Type == "goal");

        yellow.Payload.Title.Should().Be("YELLOW CARD");
        yellow.Payload.Subtitle.Should().BeNull();
        yellow.Payload.PlayerId.Should().NotBeNull();
        yellow.Id.Should().EndWith(":overlay").And.StartWith($"{_matchId}:");
        goal.DurationMs.Should().Be(6000);
        goal.Payload.Title.Should().Be("GOAL");
        messages.Where(m => m.Event == StreamEvents.Overlay).Should().OnlyContain(m => ((OverlayCard)m.Data).Id.StartsWith($"{_matchId}:{m.Id}:", StringComparison.Ordinal));
    }

    [Test]
    public void A_corner_card_is_about_the_side_that_wins_it_and_names_no_player()
    {
        var projector = new MatchStreamProjector(_matchId, _detector);
        var projections = _events.Select(projector.Apply).ToList();

        var corner = projections.First(p => p.Notable is { Kind: NotableMomentKind.Corner });
        var card = Cards(corner.Messages).Single();

        card.Payload.Team.Should().Be(corner.Notable!.Team).And.NotBe(corner.Notable.Trigger.Team);
        card.Payload.PlayerId.Should().BeNull();
    }

    [Test]
    public void The_hub_path_emits_an_overlay_for_a_key_moment_as_events_arrive()
    {
        var hub = NewHub();
        using var subscription = hub.Subscribe(after: null, Viewer.Default);
        var goal = _events.First(e => e.IsGoal);

        foreach (var matchEvent in _events.TakeWhile(e => e.Sequence < goal.Sequence))
        {
            hub.OnEvent(matchEvent);
        }

        Cards(Drain(subscription)).Where(c => c.Type == "goal").Should().BeEmpty();

        hub.OnEvent(goal);
        var card = Cards(Drain(subscription)).Single();

        card.Type.Should().Be("goal");
        card.Id.Should().Be($"{_matchId}:{goal.Sequence}:overlay");
        card.DurationMs.Should().Be(6000);
        card.Payload.PlayerId.Should().Be(goal.PlayerId);
    }

    [Test]
    public void A_replay_after_a_reconnect_has_each_overlay_once_and_none_the_client_already_saw()
    {
        var hub = NewHub();
        var live = hub.Subscribe(after: null, Viewer.Default);
        foreach (var matchEvent in _events.Take(400))
        {
            hub.OnEvent(matchEvent);
        }

        var seen = Drain(live).Where(m => m.Event == StreamEvents.Overlay).ToList();
        live.Dispose();
        var after = long.Parse(seen[2].Id!, System.Globalization.CultureInfo.InvariantCulture);
        using var reconnect = hub.Subscribe(after, Viewer.Default);

        var replayed = Cards(reconnect.Backlog).Select(c => c.Id).ToList();

        replayed.Should().OnlyHaveUniqueItems();
        replayed.Should().Equal(seen.Skip(3).Select(m => ((OverlayCard)m.Data).Id), "exactly the cards after the last one it saw, each once");
        reconnect.Backlog.Where(m => m.Event == StreamEvents.Overlay).Should().OnlyContain(m => long.Parse(m.Id!, System.Globalization.CultureInfo.InvariantCulture) > after);
    }

    [Test]
    public void A_notable_moment_sends_the_template_line_that_suits_each_viewer()
    {
        var hub = NewHub();
        using var english = hub.Subscribe(after: null, Viewer.Default);
        using var spanish = hub.Subscribe(after: null, ViewerOf(ViewingMode.Analyst, "es-MX"));
        using var german = hub.Subscribe(after: null, ViewerOf(ViewingMode.Casual, "de"));
        var card = _events.First(e => e.Card == CardColour.Yellow);

        foreach (var matchEvent in _events.TakeWhile(e => e.Sequence <= card.Sequence))
        {
            hub.OnEvent(matchEvent);
        }

        var momentId = MomentIds.For(_matchId, card.Sequence);
        var name = TeamProfileCatalog.LoadEmbedded().Values.SelectMany(t => t.Squad).Single(p => p.Id == card.PlayerId).Name;
        var inEnglish = Drain(english).Where(m => m.Event == StreamEvents.Commentary).Select(m => (CommentaryLine)m.Data).Single(l => l.MomentId == momentId);
        var inSpanish = Drain(spanish).Where(m => m.Event == StreamEvents.Commentary).Select(m => (CommentaryLine)m.Data).Single(l => l.MomentId == momentId);
        var inGerman = Drain(german).Where(m => m.Event == StreamEvents.Commentary).Select(m => (CommentaryLine)m.Data).Single(l => l.MomentId == momentId);

        inEnglish.Text.Should().Contain(name).And.Contain("goes into the book");
        inEnglish.Should().Match<CommentaryLine>(l => l.Tier == MomentTier.Notable && l.Source == CommentarySource.Template && l.Language == "en");
        inSpanish.Text.Should().Contain(name).And.Contain("tarjeta amarilla");
        inSpanish.Mode.Should().Be(ViewingMode.Analyst);
        inGerman.Language.Should().Be("en", "German falls back to English");
    }

    [Test]
    public void A_reconnect_replays_the_template_line_for_its_viewer_once()
    {
        var hub = NewHub();
        var card = _events.First(e => e.Card == CardColour.Yellow);
        foreach (var matchEvent in _events.TakeWhile(e => e.Sequence <= card.Sequence + 3))
        {
            hub.OnEvent(matchEvent);
        }

        using var reconnect = hub.Subscribe(card.Sequence - 1, ViewerOf(ViewingMode.Casual, "fr"));

        var lines = reconnect.Backlog.Where(m => m.Event == StreamEvents.Commentary).Select(m => (CommentaryLine)m.Data).ToList();
        lines.Should().ContainSingle().Which.Text.Should().Contain("carton jaune");
        reconnect.Backlog.Count(m => m.Event == StreamEvents.Overlay).Should().Be(1);
    }

    [Test]
    public void A_minor_restart_puts_nothing_on_the_stream_but_the_event_and_the_state()
    {
        var hub = NewHub();
        using var subscription = hub.Subscribe(after: null, Viewer.Default);
        var throwIn = _events.First(e => e.Restart == RestartKind.ThrowIn);

        foreach (var matchEvent in _events.TakeWhile(e => e.Sequence < throwIn.Sequence))
        {
            hub.OnEvent(matchEvent);
        }

        Drain(subscription);
        hub.OnEvent(throwIn);

        Drain(subscription).Select(m => m.Event).Should().NotContain([StreamEvents.Moment, StreamEvents.Overlay, StreamEvents.Commentary]);
    }
}
