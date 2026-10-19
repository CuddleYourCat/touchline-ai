using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class MatchStateTests
{
    private const string _matchId = "nbk-tvu-0001";

    private static MatchEvent Event(long sequence, MatchEventType type, TeamSide team = TeamSide.Home) => new MatchEvent
    {
        Sequence = sequence,
        MatchId = _matchId,
        PossessionId = 1,
        Time = MatchTime.At(MatchPeriod.SecondHalf, 63, 12),
        Type = type,
        Team = team,
        PlayerId = team == TeamSide.Home ? "nbfc-07" : "tvu-09",
        Start = new PitchPosition(94, 30)
    }.WithRequiredDetail();

    private static MatchEvent ShotEvent(long sequence, ShotOutcome outcome, double xg, TeamSide team = TeamSide.Home)
    {
        var shot = new ShotDetail(xg, outcome, ShotSituation.OpenPlay, BodyPart.RightFoot, weakFoot: false, speedKmh: 90);
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
        var create = () => new ShotDetail(xg, ShotOutcome.Saved, ShotSituation.OpenPlay, BodyPart.RightFoot, weakFoot: false, speedKmh: 90);

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
    public void Second_player_fields_are_only_valid_on_their_own_event_type()
    {
        var recipientOnShot = ShotEvent(1, ShotOutcome.Saved, 0.2) with { RecipientPlayerId = "nbfc-09" };
        var assistOnPass = Event(1, MatchEventType.Pass) with { AssistPlayerId = "nbfc-10" };
        var fouledOnTackle = Event(1, MatchEventType.Tackle) with { FouledPlayerId = "tvu-09" };

        foreach (var invalid in new[] { recipientOnShot, assistOnPass, fouledOnTackle })
        {
            invalid.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>();
        }

        var valid = () =>
        {
            (Event(1, MatchEventType.Pass) with { RecipientPlayerId = "nbfc-09" }).EnsureValid();
            (ShotEvent(1, ShotOutcome.Goal, 0.4) with { AssistPlayerId = "nbfc-10" }).EnsureValid();
            (Event(1, MatchEventType.Foul) with { FouledPlayerId = "tvu-09" }).EnsureValid();
        };
        valid.Should().NotThrow();
    }

    // Added in the richer-events slice (ADR-019)

    [TestCase(MatchEventType.Pass, TestName = "A pass without pass detail is rejected")]
    [TestCase(MatchEventType.Carry, TestName = "A carry without carry detail is rejected")]
    [TestCase(MatchEventType.MatchMetadata, TestName = "A metadata event without metadata is rejected")]
    public void An_event_missing_its_required_detail_is_rejected(MatchEventType type)
    {
        var bare = Event(1, type) with { Pass = null, Carry = null, Metadata = null };

        bare.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>().WithMessage($"*a {type} event has no*");
    }

    [Test]
    public void A_detail_on_the_wrong_event_type_is_rejected()
    {
        var wrong = new[]
        {
            ShotEvent(1, ShotOutcome.Saved, 0.2) with { Pass = new PassDetail(0.8, 40, weakFoot: false) },
            Event(1, MatchEventType.Pass) with { Carry = new CarryDetail(30) },
            Event(1, MatchEventType.KickOff) with { Metadata = TestEvents.Metadata }
        };

        foreach (var invalid in wrong)
        {
            invalid.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>();
        }
    }

    [TestCase(MatchEventType.Pass, true)]
    [TestCase(MatchEventType.Carry, true)]
    [TestCase(MatchEventType.Tackle, false)]
    [TestCase(MatchEventType.Foul, false)]
    [TestCase(MatchEventType.KickOff, false)]
    public void Only_passes_carries_and_shots_can_be_under_pressure(MatchEventType type, bool allowed)
    {
        var pressed = Event(1, type) with { UnderPressure = true };

        if (allowed)
        {
            pressed.Invoking(e => e.EnsureValid()).Should().NotThrow();
        }
        else
        {
            pressed.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>().WithMessage("*under pressure*");
        }
    }

    [Test]
    public void A_header_cannot_be_on_the_weaker_foot()
    {
        var create = () => new ShotDetail(0.2, ShotOutcome.Saved, ShotSituation.SetPiece, BodyPart.Head, weakFoot: true, speedKmh: 55);

        create.Should().Throw<ArgumentException>().WithMessage("*header*");
    }

    [TestCase(-0.1)]
    [TestCase(1.1)]
    [TestCase(double.NaN)]
    public void An_xpass_outside_0_to_1_is_rejected(double xPass)
    {
        var create = () => new PassDetail(xPass, 40, weakFoot: false);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(0)]
    [TestCase(-5)]
    [TestCase(300)]
    [TestCase(double.PositiveInfinity)]
    public void An_implausible_speed_is_rejected(double speedKmh)
    {
        new Action(() => _ = new CarryDetail(speedKmh)).Should().Throw<ArgumentOutOfRangeException>();
        new Action(() => _ = new PassDetail(0.8, speedKmh, weakFoot: false)).Should().Throw<ArgumentOutOfRangeException>();
        new Action(() => _ = new ShotDetail(0.1, ShotOutcome.Saved, ShotSituation.OpenPlay, BodyPart.RightFoot, weakFoot: false, speedKmh))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void The_metadata_event_changes_no_totals()
    {
        var state = MatchState.KickOff(_matchId).Apply(Event(1, MatchEventType.MatchMetadata));

        state.Home.Should().Be(TeamStats.Empty);
        state.Away.Should().Be(TeamStats.Empty);
        state.LastSequence.Should().Be(1);
    }

    // Added after code review

    [Test]
    public void A_metadata_event_that_names_a_player_or_an_end_is_rejected()
    {
        var withPlayer = Event(1, MatchEventType.MatchMetadata) with { PlayerId = "nbfc-07" };
        var withEnd = Event(1, MatchEventType.MatchMetadata) with { End = new PitchPosition(60, 34) };

        withPlayer.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>().WithMessage("*names a player*");
        withEnd.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>().WithMessage("*end position*");
    }

    private static readonly string[] _elevenPlayers = [.. Enumerable.Range(1, 11).Select(n => $"p-{n}")];

    [TestCase(10, false, TestName = "A starting eleven of ten players is rejected")]
    [TestCase(11, true, TestName = "A starting eleven with a repeated player is rejected")]
    public void A_starting_eleven_must_be_eleven_different_players(int count, bool repeatOne)
    {
        var players = _elevenPlayers.Take(count).ToList();
        if (repeatOne)
        {
            players[10] = players[0];
        }

        var create = () => new TeamSheet("north-bank-fc", "North Bank FC", "NBK", players);

        create.Should().Throw<ArgumentException>().WithMessage("*11 different player IDs*");
    }

    [Test]
    public void Metadata_without_a_starting_eleven_venue_or_second_team_is_rejected()
    {
        var sheet = TestEvents.Metadata.Home;

        new Action(() => _ = new TeamSheet("x", "X", "XXX", null!)).Should().Throw<ArgumentException>();
        new Action(() => _ = new TeamSheet(" ", "X", "XXX", _elevenPlayers)).Should().Throw<ArgumentException>().WithMessage("*id*");
        new Action(() => _ = new MatchMetadata(sheet, TestEvents.Metadata.Away, "", DateTimeOffset.UnixEpoch, 1))
            .Should().Throw<ArgumentException>().WithMessage("*venue*");
        new Action(() => _ = new MatchMetadata(sheet, null!, "Ground", DateTimeOffset.UnixEpoch, 1)).Should().Throw<ArgumentNullException>();
        new Action(() => _ = new MatchMetadata(sheet, sheet, "Ground", DateTimeOffset.UnixEpoch, 1))
            .Should().Throw<ArgumentException>().WithMessage("*can't play itself*");
    }

    [Test]
    public void Team_sheets_with_the_same_players_are_equal_even_as_different_lists()
    {
        var first = new TeamSheet("north-bank-fc", "North Bank FC", "NBK", _elevenPlayers.ToList());
        var second = new TeamSheet("north-bank-fc", "North Bank FC", "NBK", _elevenPlayers.ToArray());
        var reordered = new TeamSheet("north-bank-fc", "North Bank FC", "NBK", Enumerable.Reverse(_elevenPlayers).ToList());

        second.Should().Be(first);
        second.GetHashCode().Should().Be(first.GetHashCode());
        reordered.Should().NotBe(first, "the order matters: the goalkeeper comes first");
    }

    [Test]
    public void Undefined_enum_values_are_rejected()
    {
        var create = () => new ShotDetail(0.2, ShotOutcome.Saved, ShotSituation.OpenPlay, (BodyPart)7, weakFoot: false, speedKmh: 80);
        var unknownType = Event(1, (MatchEventType)42);
        var unknownTeam = Event(1, MatchEventType.Tackle, (TeamSide)3);

        create.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*BodyPart*");
        unknownType.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>().WithMessage("*event type 42*");
        unknownTeam.Invoking(e => e.EnsureValid()).Should().Throw<ArgumentException>().WithMessage("*team 3*");
    }

    [Test]
    public void Expected_goals_that_are_not_a_number_are_rejected()
    {
        var create = () => new ShotDetail(double.NaN, ShotOutcome.Saved, ShotSituation.OpenPlay, BodyPart.RightFoot, weakFoot: false, speedKmh: 90);

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
