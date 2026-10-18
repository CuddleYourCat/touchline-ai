using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class MatchStateTests
{
    private const string _matchId = "nbk-tvu-0001";

    private static MatchEvent Event(long sequence, MatchEventType type, TeamSide team = TeamSide.Home) => new()
    {
        Sequence = sequence,
        MatchId = _matchId,
        PossessionId = 1,
        Time = MatchTime.At(MatchPeriod.SecondHalf, 63, 12),
        Type = type,
        Team = team,
        PlayerId = team == TeamSide.Home ? "nbfc-07" : "tvu-09",
        Start = new PitchPosition(94, 30)
    };

    private static MatchEvent ShotEvent(long sequence, ShotOutcome outcome, double xg, TeamSide team = TeamSide.Home)
    {
        var shot = new ShotDetail(xg, outcome, ShotSituation.OpenPlay);
        return Event(sequence, MatchEventType.Shot, team) with { Shot = shot, Successful = shot.OnTarget };
    }

    [Test]
    public void A_goal_counts_as_a_goal_a_shot_on_target_and_its_xg()
    {
        var state = MatchState.KickOff(_matchId).Apply(ShotEvent(1, ShotOutcome.Goal, 0.41));

        state.Home.Should().BeEquivalentTo(TeamStats.Empty with { Goals = 1, Shots = 1, ShotsOnTarget = 1, ExpectedGoals = 0.41 });
        state.Score.Should().Be("1–0");
    }

    [Test]
    public void A_save_is_on_target_but_a_block_is_not()
    {
        var state = MatchState.KickOff(_matchId)
            .Apply(ShotEvent(1, ShotOutcome.Saved, 0.2))
            .Apply(ShotEvent(2, ShotOutcome.Blocked, 0.1));

        state.Home.Shots.Should().Be(2);
        state.Home.ShotsOnTarget.Should().Be(1);
        state.Home.Goals.Should().Be(0);
    }

    [Test]
    public void Events_only_change_the_acting_team_s_totals()
    {
        var state = MatchState.KickOff(_matchId).Apply(ShotEvent(1, ShotOutcome.Goal, 0.3, TeamSide.Away));

        state.Home.Should().Be(TeamStats.Empty);
        state.Score.Should().Be("0–1");
    }

    [Test]
    public void Pass_accuracy_counts_only_completed_passes()
    {
        var state = MatchState.KickOff(_matchId)
            .Apply(Event(1, MatchEventType.Pass))
            .Apply(Event(2, MatchEventType.Pass) with { Successful = false });

        state.Home.PassAccuracy.Should().Be(0.5);
    }

    [Test]
    public void Applying_an_event_leaves_the_previous_state_unchanged()
    {
        var before = MatchState.KickOff(_matchId);

        var after = before.Apply(ShotEvent(1, ShotOutcome.Goal, 0.5));

        before.Home.Goals.Should().Be(0);
        after.Home.Goals.Should().Be(1);
        after.LastSequence.Should().Be(1);
        after.Time.ToString().Should().Be("63:12");
    }

    [Test]
    public void An_event_from_another_match_is_rejected()
    {
        var apply = () => MatchState.KickOff(_matchId).Apply(Event(1, MatchEventType.Pass) with { MatchId = "other" });

        apply.Should().Throw<ArgumentException>();
    }

    [TestCase(1, TestName = "An event applied twice is rejected")]
    [TestCase(3, TestName = "An event after a gap is rejected")]
    public void Events_out_of_sequence_are_rejected(long secondSequence)
    {
        var state = MatchState.KickOff(_matchId).Apply(Event(1, MatchEventType.Pass));

        var apply = () => state.Apply(Event(secondSequence, MatchEventType.Pass));

        apply.Should().Throw<InvalidOperationException>();
    }

    [TestCase(-0.01)]
    [TestCase(1.01)]
    public void Expected_goals_outside_zero_to_one_are_rejected(double xg)
    {
        var create = () => new ShotDetail(xg, ShotOutcome.Saved, ShotSituation.OpenPlay);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void A_shot_without_its_detail_is_rejected_rather_than_silently_uncounted()
    {
        var apply = () => MatchState.KickOff(_matchId).Apply(Event(1, MatchEventType.Shot));

        apply.Should().Throw<ArgumentException>().WithMessage("*no shot detail*");
    }

    [Test]
    public void A_card_event_without_its_colour_is_rejected()
    {
        var apply = () => MatchState.KickOff(_matchId).Apply(Event(1, MatchEventType.Card));

        apply.Should().Throw<ArgumentException>().WithMessage("*no card colour*");
    }

    [Test]
    public void A_missed_shot_marked_successful_is_rejected()
    {
        var contradictory = ShotEvent(1, ShotOutcome.OffTarget, 0.05) with { Successful = true };

        var apply = () => MatchState.KickOff(_matchId).Apply(contradictory);

        apply.Should().Throw<ArgumentException>().WithMessage("*Successful = True*OffTarget*");
    }

    [Test]
    public void Expected_goals_that_are_not_a_number_are_rejected()
    {
        var create = () => new ShotDetail(double.NaN, ShotOutcome.Saved, ShotSituation.OpenPlay);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(MatchPeriod.SecondHalf, 10, TestName = "A second-half clock before 45:00 is rejected")]
    [TestCase(MatchPeriod.FirstHalf, 80, TestName = "A first-half clock of 60:00 or later is rejected")]
    public void Clock_readings_outside_their_half_are_rejected(MatchPeriod period, int minute)
    {
        var create = () => MatchTime.At(period, minute);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void First_half_added_time_sorts_before_the_second_half()
    {
        var addedTime = MatchTime.At(MatchPeriod.FirstHalf, 45, 30);
        var secondHalfKickOff = MatchTime.At(MatchPeriod.SecondHalf, 45);

        addedTime.CompareTo(secondHalfKickOff).Should().BeNegative();
    }
}
