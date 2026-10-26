using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.MatchCentre.Demo;

namespace Touchline.Tests.Unit.Moments;

[TestFixture]
public class NotableMomentDetectorTests
{
    private const string _matchId = "nbk-tvu-0001";

    private static readonly KeyMomentDetector _key = new();
    private readonly NotableMomentDetector _detector = new(_key);

    private static MatchState KickOff => MatchState.KickOff(_matchId);

    private static MatchEvent Event(MatchEventType type, TeamSide team, PitchPosition at, long sequence = 5) => new MatchEvent
    {
        Sequence = sequence,
        MatchId = _matchId,
        PossessionId = 1,
        Time = MatchTime.At(MatchPeriod.SecondHalf, 63, 12),
        Type = type,
        Team = team,
        PlayerId = team == TeamSide.Home ? "nbfc-07" : "tvu-09",
        Start = at
    }.WithRequiredDetail();

    private static MatchEvent Card(CardColour colour, TeamSide team = TeamSide.Away) =>
        Event(MatchEventType.Card, team, new PitchPosition(52.5, 34)) with { Card = colour };

    private static MatchEvent Out(RestartKind restart, TeamSide lastTouch, PitchPosition at) =>
        Event(MatchEventType.BallOutOfPlay, lastTouch, at) with { Restart = restart };

    private static MatchEvent Shot(ShotOutcome outcome, double xg, bool woodwork, ShotSituation situation = ShotSituation.OpenPlay)
    {
        var mouth = outcome switch
        {
            ShotOutcome.Goal or ShotOutcome.Saved => new GoalMouthPosition(1.5, 0.4),
            _ => new GoalMouthPosition(3.66, 1.0)
        };
        var detail = new ShotDetail(
            xg, outcome, situation, BodyPart.RightFoot, weakFoot: false, speedKmh: 90, mouth, woodwork,
            situation == ShotSituation.Penalty ? DiveDirection.Left : null);
        return Event(MatchEventType.Shot, TeamSide.Home, new PitchPosition(94, 32)) with
        {
            Shot = detail,
            Successful = detail.OnTarget,
            End = TestEvents.EndFor(outcome, TeamSide.Home)
        };
    }

    [Test]
    public void A_yellow_card_is_notable_and_about_the_cautioned_side()
    {
        var card = Card(CardColour.Yellow);

        var moment = _detector.Detect(card, KickOff);

        moment.Should().NotBeNull();
        moment!.Kind.Should().Be(NotableMomentKind.YellowCard);
        moment.Team.Should().Be(TeamSide.Away);
        moment.Trigger.Should().BeSameAs(card);
        moment.IsMinor.Should().BeFalse();
        moment.Reason.Should().Be("63:12: Yellow card for Kieran Moss (TVU).");
    }

    [Test]
    public void A_substitution_is_notable_names_both_players_and_features_the_one_coming_on()
    {
        var substitution = Event(MatchEventType.Substitution, TeamSide.Home, new PitchPosition(52.5, 0));

        var moment = _detector.Detect(substitution, KickOff);

        moment.Should().NotBeNull();
        moment!.Kind.Should().Be(NotableMomentKind.Substitution);
        moment.Team.Should().Be(TeamSide.Home);
        moment.IsMinor.Should().BeFalse("a substitution is for every viewer");
        moment.FeaturedPlayerId.Should().Be("nbfc-14");
        moment.ReplacementName.Should().Be("Idris Bello");
        moment.PlayerTeam.Should().Be("NBK");
        moment.Reason.Should().StartWith("63:12: Substitution for the home team: Idris Bello (NBK) comes on for ");
        NotableLines.Text(moment, "en").Should().Be($"Substitution: Idris Bello comes on for {moment.PlayerName} (NBK).");
    }

    [Test]
    public void A_substitution_whose_player_coming_on_no_squad_has_gets_the_anonymous_line()
    {
        var substitution = Event(MatchEventType.Substitution, TeamSide.Home, new PitchPosition(52.5, 0)) with { ReplacementPlayerId = "zz-99" };

        var moment = _detector.Detect(substitution, KickOff)!;

        moment.ReplacementName.Should().BeNull();
        NotableLines.Text(moment, "en").Should().Be("Substitution. A player comes on.");
    }

    [Test]
    public void A_red_card_is_a_key_moment_and_so_not_notable()
    {
        _detector.Detect(Card(CardColour.Red), KickOff).Should().BeNull();
    }

    [Test]
    public void A_corner_is_notable_and_goes_to_the_side_that_did_not_touch_it_last()
    {
        var corner = Out(RestartKind.Corner, TeamSide.Away, new PitchPosition(106, 0));

        var moment = _detector.Detect(corner, KickOff);

        moment!.Kind.Should().Be(NotableMomentKind.Corner);
        moment.Team.Should().Be(TeamSide.Home);
        moment.FeaturedPlayerId.Should().BeNull("the last toucher belongs to the side that conceded it");
        moment.IsMinor.Should().BeFalse();
        moment.PlayerName.Should().Be("Kieran Moss");
    }

    [Test]
    public void A_shot_off_the_woodwork_that_is_not_a_key_moment_is_notable()
    {
        var shot = Shot(ShotOutcome.OffTarget, 0.10, woodwork: true);

        var moment = _detector.Detect(shot, KickOff);

        moment!.Kind.Should().Be(NotableMomentKind.Woodwork);
        moment.Team.Should().Be(TeamSide.Home);
        moment.FeaturedPlayerId.Should().Be("nbfc-07");
    }

    [Test]
    public void A_shot_off_the_post_that_is_a_big_chance_stays_a_key_moment()
    {
        var shot = Shot(ShotOutcome.OffTarget, 0.55, woodwork: true);

        _key.Detect(shot, KickOff)!.Kind.Should().Be(KeyMomentKind.BigChance);
        _detector.Detect(shot, KickOff).Should().BeNull();
    }

    [Test]
    public void A_goal_in_off_the_post_stays_a_key_moment()
    {
        _detector.Detect(Shot(ShotOutcome.Goal, 0.10, woodwork: true), KickOff).Should().BeNull();
    }

    [Test]
    public void A_penalty_off_the_bar_stays_a_key_moment()
    {
        var penalty = Shot(ShotOutcome.OffTarget, 0.76, woodwork: true, ShotSituation.Penalty);

        _key.Detect(penalty, KickOff)!.Kind.Should().Be(KeyMomentKind.PenaltyMissed);
        _detector.Detect(penalty, KickOff).Should().BeNull();
    }

    [Test]
    public void A_throw_in_is_notable_but_minor()
    {
        var moment = _detector.Detect(Out(RestartKind.ThrowIn, TeamSide.Home, new PitchPosition(40, -1)), KickOff);

        moment!.Kind.Should().Be(NotableMomentKind.ThrowIn);
        moment.IsMinor.Should().BeTrue();
        moment.Team.Should().Be(TeamSide.Away);
    }

    [Test]
    public void A_goal_kick_is_notable_but_minor()
    {
        var moment = _detector.Detect(Out(RestartKind.GoalKick, TeamSide.Home, new PitchPosition(106, 20)), KickOff);

        moment!.Kind.Should().Be(NotableMomentKind.GoalKick);
        moment.IsMinor.Should().BeTrue();
        moment.Team.Should().Be(TeamSide.Away);
    }

    [Test]
    public void An_ordinary_shot_or_foul_is_not_notable()
    {
        _detector.Detect(Shot(ShotOutcome.Saved, 0.05, woodwork: false), KickOff).Should().BeNull();
        _detector.Detect(Event(MatchEventType.Foul, TeamSide.Home, new PitchPosition(60, 34)), KickOff).Should().BeNull();
    }

    [Test]
    public void An_unknown_player_gets_no_name_and_no_club_and_the_line_says_a_player()
    {
        var card = Card(CardColour.Yellow) with { PlayerId = "zz-99" };

        var moment = _detector.Detect(card, KickOff);

        moment!.PlayerName.Should().BeNull();
        NotableLines.Text(moment, "en").Should().Be("A player goes into the book.").And.NotContain("zz-99");
        moment.PlayerTeam.Should().BeNull();
    }

    [Test]
    public void Detecting_after_the_key_detector_gives_the_same_answer_without_asking_it_again()
    {
        var card = Card(CardColour.Yellow);
        var corner = Out(RestartKind.Corner, TeamSide.Away, new PitchPosition(106, 0));

        _detector.DetectAfterKey(card, KickOff)!.Kind.Should().Be(NotableMomentKind.YellowCard);
        _detector.DetectAfterKey(corner, KickOff)!.Should().BeEquivalentTo(_detector.Detect(corner, KickOff));
        _detector.DetectAfterKey(Event(MatchEventType.Pass, TeamSide.Home, new PitchPosition(50, 34)), KickOff).Should().BeNull();
    }

    [Test]
    public void The_self_checking_version_still_lets_a_key_moment_beat_a_notable_one()
    {
        var goalOffThePost = Shot(ShotOutcome.Goal, 0.10, woodwork: true);

        _detector.Detect(goalOffThePost, KickOff).Should().BeNull();
        _detector.DetectAfterKey(goalOffThePost, KickOff)!.Kind.Should().Be(NotableMomentKind.Woodwork, "the caller was meant to have ruled the key moment out first");
    }

    [Test]
    public void An_event_that_contradicts_itself_is_refused()
    {
        var pass = Event(MatchEventType.Pass, TeamSide.Home, new PitchPosition(50, 34)) with { Card = CardColour.Yellow };

        var detect = () => _detector.Detect(pass, KickOff);

        detect.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Null_arguments_are_refused()
    {
        var card = Card(CardColour.Yellow);

        ((Action)(() => _detector.Detect(null!, KickOff))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _detector.Detect(card, null!))).Should().Throw<ArgumentNullException>().WithParameterName("before");
    }

    [Test]
    public void Over_the_seeded_demo_match_every_event_is_at_most_one_moment_with_exact_counts_per_kind()
    {
        var state = MatchState.KickOff(DemoRecording.Events[0].MatchId);
        var notables = new List<NotableMoment>();
        foreach (var matchEvent in DemoRecording.Events)
        {
            var key = _key.Detect(matchEvent, state);
            var notable = _detector.Detect(matchEvent, state);
            (key is not null && notable is not null).Should().BeFalse($"event {matchEvent.Sequence} must be one moment at most");
            if (notable is not null)
            {
                notables.Add(notable);
            }

            state = state.Apply(matchEvent);
        }

        var expected = new Dictionary<NotableMomentKind, int>
        {
            [NotableMomentKind.YellowCard] = _yellowCards,
            [NotableMomentKind.Corner] = _corners,
            [NotableMomentKind.Woodwork] = _woodwork,
            [NotableMomentKind.ThrowIn] = _throwIns,
            [NotableMomentKind.GoalKick] = _goalKicks,
            [NotableMomentKind.Substitution] = _substitutions
        };
        notables.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(expected.Where(k => k.Value > 0));
        notables.Count(n => !n.IsMinor).Should().Be(_yellowCards + _corners + _woodwork + _substitutions);
    }

    [Test]
    public void The_seeded_counts_agree_with_a_count_straight_from_the_events()
    {
        var events = DemoRecording.Events;

        events.Count(e => e.Card == CardColour.Yellow).Should().Be(_yellowCards);
        events.Count(e => e.Restart == RestartKind.Corner).Should().Be(_corners);
        events.Count(e => e.Restart == RestartKind.ThrowIn).Should().Be(_throwIns);
        events.Count(e => e.Restart == RestartKind.GoalKick).Should().Be(_goalKicks);
        events.Count(e => e.Shot is { HitWoodwork: true } && _key.Detect(e, MatchState.KickOff(e.MatchId)) is null).Should().Be(_woodwork);
        events.Count(e => e.Type == MatchEventType.Substitution).Should().Be(_substitutions);
    }

    // The recording's counts, which a regenerated recording would change (the snapshot test says so).
    // Seed 35600 since substitutions (ADR-064).
    private const int _yellowCards = 6;
    private const int _corners = 11;
    private const int _woodwork = 0;
    private const int _throwIns = 42;
    private const int _goalKicks = 8;
    private const int _substitutions = 7;
}
