using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Moments;

[TestFixture]
public class MilestoneDetectorTests
{
    private const string _matchId = "nbk-tvu-0003";
    private const string _homeStriker = "nbfc-07";
    private const string _homeMidfielder = "nbfc-05";
    private const string _awayStriker = "tvu-09";

    private MilestoneDetector _detector = null!;
    private long _sequence;

    [SetUp]
    public void SetUp()
    {
        _detector = new MilestoneDetector();
        _sequence = 10;
    }

    private static double ProfileTopSpeed(string playerId) =>
        TeamProfileCatalog.LoadEmbedded().Values.SelectMany(team => team.Squad).Single(p => p.Id == playerId).TopSpeedKmh;

    private MatchEvent Next(MatchEventType type, TeamSide team, string? playerId, MatchTime time, PitchPosition at) => new MatchEvent
    {
        Sequence = ++_sequence,
        MatchId = _matchId,
        PossessionId = 1,
        Time = time,
        Type = type,
        Team = team,
        PlayerId = playerId,
        Start = at
    }.WithRequiredDetail();

    private MatchEvent Goal(string playerId, TeamSide team = TeamSide.Home, int minute = 30)
    {
        var shot = TestEvents.Shot(0.35, ShotOutcome.Goal);
        var at = team == TeamSide.Home ? new PitchPosition(94, 32) : new PitchPosition(11, 34);
        return Next(MatchEventType.Shot, team, playerId, MatchTime.At(MatchPeriod.FirstHalf, minute), at) with
        {
            Shot = shot,
            Successful = shot.OnTarget,
            End = TestEvents.EndFor(ShotOutcome.Goal, team)
        };
    }

    private MatchEvent Pass(string playerId, bool completed = true, TeamSide team = TeamSide.Home) =>
        Next(MatchEventType.Pass, team, playerId, MatchTime.At(MatchPeriod.FirstHalf, 20), new PitchPosition(50, 30)) with
        {
            Successful = completed
        };

    private MatchEvent Card(CardColour colour, string playerId = _awayStriker, TeamSide team = TeamSide.Away) =>
        Next(MatchEventType.Card, team, playerId, MatchTime.At(MatchPeriod.FirstHalf, 38), new PitchPosition(60, 20)) with
        {
            Card = colour
        };

    private MatchEvent Carry(string playerId, double speedKmh, TeamSide team = TeamSide.Home) =>
        Next(MatchEventType.Carry, team, playerId, MatchTime.At(MatchPeriod.SecondHalf, 70), new PitchPosition(40, 50)) with
        {
            Carry = new CarryDetail(speedKmh),
            End = new PitchPosition(55, 52)
        };

    private MatchEvent PeriodEnd(MatchPeriod period) =>
        Next(MatchEventType.PeriodEnd, TeamSide.Home, null,
            period == MatchPeriod.FirstHalf ? MatchTime.At(MatchPeriod.FirstHalf, 47) : MatchTime.At(MatchPeriod.SecondHalf, 93),
            new PitchPosition(52.5, 34));

    private void Feed(IEnumerable<MatchEvent> events)
    {
        foreach (var matchEvent in events)
        {
            _detector.Observe(matchEvent);
        }
    }

    [Test]
    public void A_null_event_is_rejected()
    {
        var act = () => _detector.Observe(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void An_event_that_contradicts_itself_is_rejected()
    {
        var pass = Pass(_homeMidfielder) with { Shot = TestEvents.Shot(0.1, ShotOutcome.Saved) };

        var act = () => _detector.Observe(pass);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void An_event_from_another_match_is_rejected()
    {
        _detector.Observe(Pass(_homeMidfielder));
        var stranger = Pass(_homeMidfielder) with { MatchId = "nbk-tvu-9999" };

        var act = () => _detector.Observe(stranger);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void An_ordinary_event_reaches_no_milestone()
    {
        _detector.Observe(Pass(_homeMidfielder)).Should().BeEmpty();
    }

    [Test]
    public void The_first_goal_of_the_match_is_a_milestone_for_the_scorer()
    {
        var goal = Goal(_homeStriker);

        var milestones = _detector.Observe(goal);

        milestones.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = MilestoneKind.FirstGoal,
            Trigger = goal,
            Side = TeamSide.Home,
            PlayerId = _homeStriker
        });
    }

    [Test]
    public void The_first_goal_can_belong_to_the_away_side()
    {
        var milestone = _detector.Observe(Goal(_awayStriker, TeamSide.Away)).Should().ContainSingle().Subject;

        milestone.Kind.Should().Be(MilestoneKind.FirstGoal);
        milestone.Side.Should().Be(TeamSide.Away);
    }

    [Test]
    public void A_second_goal_by_a_different_player_is_no_milestone()
    {
        _detector.Observe(Goal(_homeStriker));

        _detector.Observe(Goal(_awayStriker, TeamSide.Away, minute: 40)).Should().BeEmpty();
    }

    [Test]
    public void A_players_second_goal_is_a_brace()
    {
        _detector.Observe(Goal(_homeStriker));
        _detector.Observe(Goal(_awayStriker, TeamSide.Away, minute: 35));

        var milestone = _detector.Observe(Goal(_homeStriker, minute: 41)).Should().ContainSingle().Subject;

        milestone.Kind.Should().Be(MilestoneKind.Brace);
        milestone.PlayerId.Should().Be(_homeStriker);
    }

    [Test]
    public void A_players_third_goal_is_a_hat_trick_and_a_fourth_is_nothing()
    {
        _detector.Observe(Goal(_homeStriker, minute: 10));
        _detector.Observe(Goal(_homeStriker, minute: 20));

        _detector.Observe(Goal(_homeStriker, minute: 30)).Should().ContainSingle()
            .Which.Kind.Should().Be(MilestoneKind.HatTrick);
        _detector.Observe(Goal(_homeStriker, minute: 40)).Should().BeEmpty();
    }

    [Test]
    public void A_players_fiftieth_completed_pass_is_a_milestone()
    {
        var before = Enumerable.Range(1, MilestoneDetector.PassMilestone - 1)
            .SelectMany(_ => _detector.Observe(Pass(_homeMidfielder)))
            .ToList();

        var fiftieth = _detector.Observe(Pass(_homeMidfielder));

        before.Should().BeEmpty();
        fiftieth.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = MilestoneKind.FiftiethPass,
            Side = TeamSide.Home,
            PlayerId = _homeMidfielder
        });
    }

    [Test]
    public void Misplaced_passes_do_not_count_towards_fifty()
    {
        Feed(Enumerable.Range(1, MilestoneDetector.PassMilestone - 1).Select(_ => Pass(_homeMidfielder)));

        var misplaced = Enumerable.Range(1, 5).SelectMany(_ => _detector.Observe(Pass(_homeMidfielder, completed: false))).ToList();
        var fiftieth = _detector.Observe(Pass(_homeMidfielder));

        misplaced.Should().BeEmpty();
        fiftieth.Should().ContainSingle().Which.Kind.Should().Be(MilestoneKind.FiftiethPass);
    }

    [Test]
    public void The_fifty_first_pass_is_no_milestone()
    {
        Feed(Enumerable.Range(1, MilestoneDetector.PassMilestone).Select(_ => Pass(_homeMidfielder)));

        _detector.Observe(Pass(_homeMidfielder)).Should().BeEmpty();
    }

    [Test]
    public void Passes_are_counted_per_player()
    {
        var milestones = Enumerable.Range(1, 30)
            .SelectMany(_ => _detector.Observe(Pass(_homeMidfielder)).Concat(_detector.Observe(Pass(_homeStriker))))
            .ToList();

        milestones.Should().BeEmpty();
    }

    [Test]
    public void Goalless_at_half_time_is_a_clean_sheet_for_both_sides_home_first()
    {
        var milestones = _detector.Observe(PeriodEnd(MatchPeriod.FirstHalf));

        milestones.Select(m => (m.Kind, m.Side, m.PlayerId)).Should().Equal(
            (MilestoneKind.CleanSheetAtHalfTime, TeamSide.Home, (string?)null),
            (MilestoneKind.CleanSheetAtHalfTime, TeamSide.Away, (string?)null));
    }

    [Test]
    public void Only_the_side_that_has_not_conceded_keeps_a_clean_sheet()
    {
        _detector.Observe(Goal(_homeStriker));

        var milestone = _detector.Observe(PeriodEnd(MatchPeriod.FirstHalf)).Should().ContainSingle().Subject;

        milestone.Kind.Should().Be(MilestoneKind.CleanSheetAtHalfTime);
        milestone.Side.Should().Be(TeamSide.Home);
    }

    [Test]
    public void No_clean_sheet_when_both_sides_have_scored()
    {
        _detector.Observe(Goal(_homeStriker));
        _detector.Observe(Goal(_awayStriker, TeamSide.Away, minute: 40));

        _detector.Observe(PeriodEnd(MatchPeriod.FirstHalf)).Should().BeEmpty();
    }

    [Test]
    public void The_end_of_the_second_half_is_no_clean_sheet_milestone()
    {
        _detector.Observe(PeriodEnd(MatchPeriod.FirstHalf));

        _detector.Observe(PeriodEnd(MatchPeriod.SecondHalf)).Should().BeEmpty();
    }

    [Test]
    public void The_first_card_is_the_first_booking_and_later_cards_are_not()
    {
        var yellow = Card(CardColour.Yellow);

        _detector.Observe(yellow).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = MilestoneKind.FirstBooking,
            Trigger = yellow,
            Side = TeamSide.Away,
            PlayerId = _awayStriker
        });
        _detector.Observe(Card(CardColour.Red, _homeStriker, TeamSide.Home)).Should().BeEmpty();
    }

    [Test]
    public void A_straight_red_can_be_the_first_booking()
    {
        _detector.Observe(Card(CardColour.Red)).Should().ContainSingle()
            .Which.Kind.Should().Be(MilestoneKind.FirstBooking);
    }

    [Test]
    public void A_carry_below_the_profile_top_speed_is_no_personal_best()
    {
        _detector.Observe(Carry(_homeStriker, ProfileTopSpeed(_homeStriker) - 0.5)).Should().BeEmpty();
    }

    [Test]
    public void A_carry_above_the_profile_top_speed_is_a_personal_best()
    {
        var carry = Carry(_homeStriker, ProfileTopSpeed(_homeStriker) + 0.4);

        _detector.Observe(carry).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = MilestoneKind.PersonalBestSpeed,
            Trigger = carry,
            Side = TeamSide.Home,
            PlayerId = _homeStriker
        });
    }

    [Test]
    public void A_personal_best_has_to_beat_the_fastest_carry_so_far_this_match()
    {
        var top = ProfileTopSpeed(_homeStriker);
        _detector.Observe(Carry(_homeStriker, top + 1.0));

        var slowerThanBest = _detector.Observe(Carry(_homeStriker, top + 0.5));
        var equalToBest = _detector.Observe(Carry(_homeStriker, top + 1.0));
        var newBest = _detector.Observe(Carry(_homeStriker, top + 1.3));

        slowerThanBest.Should().BeEmpty();
        equalToBest.Should().BeEmpty();
        newBest.Should().ContainSingle().Which.Kind.Should().Be(MilestoneKind.PersonalBestSpeed);
    }

    [Test]
    public void Personal_bests_are_tracked_per_player()
    {
        var homeTop = ProfileTopSpeed(_homeStriker);
        var awayTop = ProfileTopSpeed(_awayStriker);
        _detector.Observe(Carry(_homeStriker, homeTop + 2.0));

        _detector.Observe(Carry(_awayStriker, awayTop + 0.2, TeamSide.Away)).Should().ContainSingle()
            .Which.PlayerId.Should().Be(_awayStriker);
    }

    [Test]
    public void A_player_with_no_profile_never_sets_a_personal_best()
    {
        _detector.Observe(Carry("fan-unknown-p1", 39.0)).Should().BeEmpty();
    }

    [Test]
    public void The_profile_top_speed_comes_from_the_teams_it_was_given()
    {
        var catalogue = TeamProfileCatalog.LoadEmbedded().Values.ToList();
        var slowed = catalogue
            .Select(team => team with { Squad = team.Squad.Select(p => p.Id == _homeStriker ? p with { TopSpeedKmh = 20.0 } : p).ToList() })
            .ToList();
        _detector = new MilestoneDetector(slowed);

        _detector.Observe(Carry(_homeStriker, 21.0)).Should().ContainSingle()
            .Which.Kind.Should().Be(MilestoneKind.PersonalBestSpeed);
    }

    [Test]
    public void A_redelivered_event_is_ignored()
    {
        var first = Goal(_homeStriker, minute: 10);
        _detector.Observe(first);

        var redelivered = _detector.Observe(first);
        var second = _detector.Observe(Goal(_homeStriker, minute: 20));

        redelivered.Should().BeEmpty();
        second.Should().ContainSingle().Which.Kind.Should().Be(MilestoneKind.Brace);
    }

    [Test]
    public void A_reason_starts_with_the_match_time_and_names_the_player_and_team()
    {
        var goal = Goal(_homeStriker);

        var reason = _detector.Observe(goal).Should().ContainSingle().Subject.Reason;

        reason.Should().StartWith($"{goal.Time}: ").And.Contain("Reggie Ashworth (NBK)");
    }

    [Test]
    public void A_clean_sheet_reason_names_the_team_from_the_match_metadata()
    {
        _detector.Observe(Next(MatchEventType.MatchMetadata, TeamSide.Home, null, MatchTime.At(MatchPeriod.FirstHalf, 0), new PitchPosition(52.5, 34)))
            .Should().BeEmpty();

        var reasons = _detector.Observe(PeriodEnd(MatchPeriod.FirstHalf)).Select(m => m.Reason).ToList();

        reasons.Should().HaveCount(2);
        reasons[0].Should().Contain("NBK");
        reasons[1].Should().Contain("TVU");
    }

    [Test]
    public void Without_match_metadata_a_clean_sheet_reason_says_home_or_away()
    {
        var reasons = _detector.Observe(PeriodEnd(MatchPeriod.FirstHalf)).Select(m => m.Reason).ToList();

        reasons.Should().HaveCount(2);
        reasons[0].Should().ContainEquivalentOf("home");
        reasons[1].Should().ContainEquivalentOf("away");
    }
}
