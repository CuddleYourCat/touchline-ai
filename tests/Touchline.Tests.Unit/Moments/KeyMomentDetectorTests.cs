using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Moments;

[TestFixture]
public class KeyMomentDetectorTests
{
    private const string _matchId = "nbk-tvu-0001";

    private readonly KeyMomentDetector _detector = new();

    private static MatchState KickOff => MatchState.KickOff(_matchId);

    private static MatchEvent Event(MatchEventType type, TeamSide team, PitchPosition at, long sequence = 1) => new MatchEvent
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

    private static MatchEvent Shot(ShotOutcome outcome, double xg, TeamSide team = TeamSide.Home, long sequence = 1)
    {
        var detail = new ShotDetail(xg, outcome, ShotSituation.OpenPlay, BodyPart.RightFoot, weakFoot: false, speedKmh: 90);
        var at = team == TeamSide.Home ? new PitchPosition(94, 32) : new PitchPosition(11, 32);
        return Event(MatchEventType.Shot, team, at, sequence) with { Shot = detail, Successful = detail.OnTarget };
    }

    private static MatchEvent Foul(TeamSide foulingTeam, double x, double y) =>
        Event(MatchEventType.Foul, foulingTeam, new PitchPosition(x, y));

    private static MatchEvent Card(CardColour colour) =>
        Event(MatchEventType.Card, TeamSide.Away, new PitchPosition(52.5, 34)) with { Card = colour };

    // Goals

    [Test]
    public void A_goal_is_a_key_moment_triggered_by_the_shot()
    {
        var goal = Shot(ShotOutcome.Goal, 0.41);

        var moment = _detector.Detect(goal, KickOff);

        moment.Should().NotBeNull();
        moment!.Kind.Should().Be(KeyMomentKind.Goal);
        moment.Trigger.Should().BeSameAs(goal);
    }

    [Test]
    public void A_goal_from_a_poor_chance_is_still_a_goal()
    {
        var moment = _detector.Detect(Shot(ShotOutcome.Goal, 0.02), KickOff);

        moment!.Kind.Should().Be(KeyMomentKind.Goal, "a goal is a goal however unlikely it was");
    }

    [Test]
    public void A_goal_s_reason_gives_the_score_after_it()
    {
        var moment = _detector.Detect(Shot(ShotOutcome.Goal, 0.41), KickOff);

        moment!.Reason.Should().Contain("1–0");
    }

    [Test]
    public void An_away_equaliser_s_reason_gives_the_level_score()
    {
        var oneNil = KickOff.Apply(Shot(ShotOutcome.Goal, 0.41, TeamSide.Home, sequence: 1));

        var moment = _detector.Detect(Shot(ShotOutcome.Goal, 0.12, TeamSide.Away, sequence: 2), oneNil);

        moment!.Kind.Should().Be(KeyMomentKind.Goal);
        moment.Reason.Should().Contain("1–1");
    }

    // Big chances

    [TestCase(ShotOutcome.Saved, 0.30, TestName = "A saved shot exactly at the threshold is a big chance")]
    [TestCase(ShotOutcome.OffTarget, 0.55, TestName = "A missed sitter is a big chance")]
    [TestCase(ShotOutcome.Blocked, 0.35, TestName = "A blocked good chance is a big chance")]
    public void Missed_shots_at_or_above_the_threshold_are_big_chances(ShotOutcome outcome, double xg)
    {
        var moment = _detector.Detect(Shot(outcome, xg), KickOff);

        moment!.Kind.Should().Be(KeyMomentKind.BigChance);
    }

    [Test]
    public void A_missed_shot_just_below_the_threshold_is_not_a_key_moment()
    {
        _detector.Detect(Shot(ShotOutcome.Saved, 0.29), KickOff).Should().BeNull();
    }

    [Test]
    public void The_big_chance_threshold_can_be_tuned()
    {
        var strict = new KeyMomentDetector(bigChanceThreshold: 0.5);

        strict.Detect(Shot(ShotOutcome.Saved, 0.4), KickOff).Should().BeNull();
        strict.Detect(Shot(ShotOutcome.Saved, 0.5), KickOff)!.Kind.Should().Be(KeyMomentKind.BigChance);
    }

    // Penalties: a foul by the defending team inside the penalty area it defends.
    // The home team defends the goal at X = 0, the away team the goal at X = 105.
    // Each area is 16.5 m deep and 40.32 m wide, centred on Y = 34.

    [TestCase(TeamSide.Home, 10, 34, TestName = "A home foul in the home penalty area is a penalty")]
    [TestCase(TeamSide.Away, 95, 34, TestName = "An away foul in the away penalty area is a penalty")]
    [TestCase(TeamSide.Home, 16.5, 34, TestName = "A foul on the line of the area is a penalty")]
    [TestCase(TeamSide.Away, 99, 13.9, TestName = "A foul just inside the side of the area is a penalty")]
    public void A_defending_foul_inside_its_own_penalty_area_is_a_penalty(TeamSide foulingTeam, double x, double y)
    {
        var moment = _detector.Detect(Foul(foulingTeam, x, y), KickOff);

        moment!.Kind.Should().Be(KeyMomentKind.PenaltyAwarded);
    }

    [TestCase(TeamSide.Home, 16.6, 34, TestName = "A foul just outside the area is not a penalty")]
    [TestCase(TeamSide.Home, 10, 13.8, TestName = "A foul wide of the area is not a penalty")]
    [TestCase(TeamSide.Away, 99, 5, TestName = "An away foul wide of its own area is not a penalty")]
    [TestCase(TeamSide.Home, 95, 5, TestName = "A home foul wide of the opposition area is not a penalty")]
    [TestCase(TeamSide.Home, 95, 34, TestName = "An attacker's foul in the opposition area is not a penalty")]
    [TestCase(TeamSide.Away, 52.5, 34, TestName = "A foul in midfield is not a penalty")]
    public void Other_fouls_are_not_key_moments(TeamSide foulingTeam, double x, double y)
    {
        _detector.Detect(Foul(foulingTeam, x, y), KickOff).Should().BeNull();
    }

    // Cards

    [Test]
    public void A_red_card_is_a_key_moment()
    {
        _detector.Detect(Card(CardColour.Red), KickOff)!.Kind.Should().Be(KeyMomentKind.RedCard);
    }

    [Test]
    public void A_yellow_card_is_not_a_key_moment()
    {
        _detector.Detect(Card(CardColour.Yellow), KickOff).Should().BeNull();
    }

    // Everything else

    [TestCase(MatchEventType.MatchMetadata)]
    [TestCase(MatchEventType.KickOff)]
    [TestCase(MatchEventType.Pass)]
    [TestCase(MatchEventType.Carry)]
    [TestCase(MatchEventType.Pressure)]
    [TestCase(MatchEventType.Tackle)]
    [TestCase(MatchEventType.Interception)]
    [TestCase(MatchEventType.PeriodEnd)]
    public void Routine_events_are_not_key_moments(MatchEventType type)
    {
        var routine = Event(type, TeamSide.Home, new PitchPosition(100, 34));

        _detector.Detect(routine, KickOff).Should().BeNull("only the moments that matter reach a model (ADR-004)");
    }

    [TestCaseSource(nameof(KeyEvents))]
    public void Every_key_moment_explains_itself(MatchEvent keyEvent)
    {
        var moment = _detector.Detect(keyEvent, KickOff);

        moment!.Reason.Should().NotBeNullOrWhiteSpace("the reason goes into logs and the agents' context");
    }

    private static IEnumerable<MatchEvent> KeyEvents()
    {
        yield return Shot(ShotOutcome.Goal, 0.41);
        yield return Shot(ShotOutcome.Saved, 0.6);
        yield return Foul(TeamSide.Home, 8, 30);
        yield return Card(CardColour.Red);
    }

    // Added after code review

    [Test]
    public void A_goal_is_detected_even_when_the_state_is_not_exactly_one_event_behind()
    {
        var afterThirtyEvents = KickOff with { LastSequence = 30 };

        var moment = _detector.Detect(Shot(ShotOutcome.Goal, 0.41, sequence: 1), afterThirtyEvents);

        moment!.Kind.Should().Be(KeyMomentKind.Goal, "only the score is read from the state");
        moment.Reason.Should().Contain("1–0");
    }

    [Test]
    public void A_missing_state_is_rejected_with_its_parameter_named()
    {
        var detect = () => _detector.Detect(Card(CardColour.Red), null!);

        detect.Should().Throw<ArgumentNullException>().WithParameterName("before");
    }

    [Test]
    public void An_event_that_contradicts_itself_is_rejected_not_classified()
    {
        var passWithShotDetail = Event(MatchEventType.Pass, TeamSide.Home, new PitchPosition(94, 32))
            with
        { Shot = new ShotDetail(0.5, ShotOutcome.Saved, ShotSituation.OpenPlay, BodyPart.RightFoot, weakFoot: false, speedKmh: 90) };

        var detect = () => _detector.Detect(passWithShotDetail, KickOff);

        detect.Should().Throw<ArgumentException>().WithMessage("*Pass event has shot detail*");
    }

    [Test]
    public void Reasons_name_the_player_and_team()
    {
        _detector.Detect(Shot(ShotOutcome.Goal, 0.41), KickOff)!.Reason.Should().Contain("Reggie Ashworth (NBK)");
        _detector.Detect(Card(CardColour.Red), KickOff)!.Reason.Should().Contain("Kieran Moss (TVU)");
    }

    [Test]
    public void A_penalty_names_who_was_fouled_and_by_whom()
    {
        var foul = Foul(TeamSide.Away, 99, 34) with { PlayerId = "tvu-05", FouledPlayerId = "nbfc-07" };

        _detector.Detect(foul, KickOff)!.Reason.Should().Contain("Foul on Reggie Ashworth (NBK) by Joel Mbeki (TVU)");
    }

    [Test]
    public void A_goal_names_the_assist()
    {
        var goal = Shot(ShotOutcome.Goal, 0.41) with { AssistPlayerId = "nbfc-10" };

        _detector.Detect(goal, KickOff)!.Reason.Should().Contain("assisted by Elias Brook (NBK)");
    }

    [Test]
    public void A_player_missing_from_the_squads_is_named_by_id()
    {
        var unknown = Card(CardColour.Red) with { PlayerId = "guest-99" };

        _detector.Detect(unknown, KickOff)!.Reason.Should().Contain("guest-99");
    }

    [Test]
    [SetCulture("de-DE")]
    public void Expected_goals_in_a_reason_use_a_full_stop_and_two_decimals()
    {
        var moment = _detector.Detect(Shot(ShotOutcome.Saved, 0.41000000000000003), KickOff);

        moment!.Reason.Should().Contain("xG 0.41,");
    }

    // Already given: the constructor

    [TestCase(-0.1)]
    [TestCase(1.1)]
    [TestCase(double.NaN)]
    public void An_impossible_threshold_is_rejected(double threshold)
    {
        var create = () => new KeyMomentDetector(threshold);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
