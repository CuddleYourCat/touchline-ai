using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Matches.Shape;
using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class ShapeModelTests
{
    private static readonly TeamProfile _homeProfile = TeamProfileCatalog.LoadEmbedded()["north-bank-fc"];
    private static readonly TeamProfile _awayProfile = TeamProfileCatalog.LoadEmbedded()["thames-valley-united"];
    private static readonly PitchPosition _centreSpot = new(PitchDimensions.Length / 2, PitchDimensions.Width / 2);

    // A 4-3-3 from the squad, goalkeeper first.
    private static ShapeTeam Team(TeamProfile profile)
    {
        var squad = profile.Squad;
        var picked = squad.Where(p => p.Position == PlayerPosition.Goalkeeper).Take(1)
            .Concat(squad.Where(p => p.Position == PlayerPosition.Defender).Take(4))
            .Concat(squad.Where(p => p.Position == PlayerPosition.Midfielder).Take(3))
            .Concat(squad.Where(p => p.Position == PlayerPosition.Forward).Take(3))
            .ToList();
        var eleven = picked.Concat(squad.Except(picked)).Take(11).Select(p => p.Id).ToList();
        return new ShapeTeam(new TeamSheet(profile.Id, profile.Name, profile.ShortName, eleven), profile);
    }

    private static ShapeTeam WithTendencies(ShapeTeam team, Func<TeamTendencies, TeamTendencies> change) =>
        team with { Tendencies = change(team.Tendencies) };

    private static IReadOnlyList<PlacedPlayer> Place(ShapeTeam home, ShapeTeam away, PitchPosition ball, TeamSide? inPossession) =>
        ShapeModel.Place(home, away, ball, inPossession);

    private static IEnumerable<PlacedPlayer> Outfield(IEnumerable<PlacedPlayer> placed, ShapeTeam team, TeamSide side) =>
        placed.Where(p => p.Team == side && p.PlayerId != team.Sheet.StartingEleven[0]);

    [Test]
    public void Every_one_of_the_22_players_is_placed_on_the_pitch()
    {
        var placed = Place(Team(_homeProfile), Team(_awayProfile), new PitchPosition(90, 60), TeamSide.Home);

        placed.Should().HaveCount(22);
        placed.Select(p => (p.Team, p.PlayerId)).Should().OnlyHaveUniqueItems();
        placed.Should().OnlyContain(p => p.Position.X >= 0 && p.Position.X <= PitchDimensions.Length
            && p.Position.Y >= 0 && p.Position.Y <= PitchDimensions.Width);
    }

    [Test]
    public void The_same_team_on_either_side_is_the_mirror_image_with_the_ball_on_the_centre_spot()
    {
        var team = Team(_homeProfile);

        var placed = Place(team, team, _centreSpot, inPossession: null);

        var home = placed.Where(p => p.Team == TeamSide.Home).ToList();
        var away = placed.Where(p => p.Team == TeamSide.Away).ToList();
        for (var i = 0; i < home.Count; i++)
        {
            away[i].Position.X.Should().BeApproximately(PitchDimensions.Length - home[i].Position.X, 1e-9);
            away[i].Position.Y.Should().BeApproximately(PitchDimensions.Width - home[i].Position.Y, 1e-9);
        }
    }

    [Test]
    public void A_team_in_possession_is_further_up_the_pitch_than_when_defending()
    {
        var home = Team(_homeProfile);
        var away = Team(_awayProfile);

        var attacking = Outfield(Place(home, away, _centreSpot, TeamSide.Home), home, TeamSide.Home).Average(p => p.Position.X);
        var defending = Outfield(Place(home, away, _centreSpot, TeamSide.Away), home, TeamSide.Home).Average(p => p.Position.X);

        attacking.Should().BeGreaterThan(defending + 5);
    }

    [Test]
    public void A_wider_team_spreads_further_across_the_pitch_with_the_ball()
    {
        var narrow = WithTendencies(Team(_homeProfile), t => t with { Width = 0.2 });
        var wide = WithTendencies(Team(_homeProfile), t => t with { Width = 0.8 });
        var away = Team(_awayProfile);

        double Spread(ShapeTeam team)
        {
            var ys = Outfield(Place(team, away, _centreSpot, TeamSide.Home), team, TeamSide.Home).Select(p => p.Position.Y).ToList();
            return ys.Max() - ys.Min();
        }

        Spread(wide).Should().BeGreaterThan(Spread(narrow));
    }

    [Test]
    public void A_team_that_presses_harder_holds_a_higher_line_without_the_ball()
    {
        var sitting = WithTendencies(Team(_homeProfile), t => t with { PressingIntensity = 0.1 });
        var pressing = WithTendencies(Team(_homeProfile), t => t with { PressingIntensity = 0.9 });
        var away = Team(_awayProfile);

        double Line(ShapeTeam team) =>
            Outfield(Place(team, away, _centreSpot, TeamSide.Away), team, TeamSide.Home).Average(p => p.Position.X);

        Line(pressing).Should().BeGreaterThan(Line(sitting));
    }

    [TestCase(10.0, 5.0)]
    [TestCase(95.0, 60.0)]
    [TestCase(52.5, 34.0)]
    public void The_goalkeepers_stay_near_their_own_goals(double x, double y)
    {
        var home = Team(_homeProfile);
        var away = Team(_awayProfile);

        var placed = Place(home, away, new PitchPosition(x, y), TeamSide.Home);

        var homeKeeper = placed.Single(p => p.Team == TeamSide.Home && p.PlayerId == home.Sheet.StartingEleven[0]).Position;
        var awayKeeper = placed.Single(p => p.Team == TeamSide.Away && p.PlayerId == away.Sheet.StartingEleven[0]).Position;
        homeKeeper.X.Should().BeLessThan(PitchDimensions.PenaltyAreaDepth);
        awayKeeper.X.Should().BeGreaterThan(PitchDimensions.Length - PitchDimensions.PenaltyAreaDepth);
        Math.Abs(homeKeeper.Y - (PitchDimensions.Width / 2)).Should().BeLessThan(PitchDimensions.PenaltyAreaWidth / 2);
        Math.Abs(awayKeeper.Y - (PitchDimensions.Width / 2)).Should().BeLessThan(PitchDimensions.PenaltyAreaWidth / 2);
    }

    [Test]
    public void The_same_input_always_gives_exactly_the_same_shape()
    {
        var ball = new PitchPosition(71.3, 22.8);

        var first = Place(Team(_homeProfile), Team(_awayProfile), ball, TeamSide.Away);
        var second = Place(Team(_homeProfile), Team(_awayProfile), ball, TeamSide.Away);

        second.Should().Equal(first);
    }

    [Test]
    public void The_hash_is_the_same_FNV_1a_on_every_run()
    {
        ShapeModel.Hash("a").Should().Be(0xAF63DC4C8601EC8CUL);
        ShapeModel.Hash("foobar").Should().Be(0x85944171F73967E8UL);
    }

    [Test]
    public void Every_nudge_stays_within_its_bound()
    {
        var ids = _homeProfile.Squad.Concat(_awayProfile.Squad).Select(p => p.Id).ToList();
        foreach (var id in ids)
        {
            var (depth, across) = ShapeModel.PersonalOffset(ShapeModel.Hash(id));
            Math.Abs(depth).Should().BeLessThanOrEqualTo(2);
            Math.Abs(across).Should().BeLessThanOrEqualTo(2);
            for (var x = 0; x <= 105; x += 15)
            {
                var (wobbleDepth, wobbleAcross) = ShapeModel.Wobble(ShapeModel.Hash(id), new PitchPosition(x, x / 2.0));
                Math.Abs(wobbleDepth).Should().BeLessThanOrEqualTo(0.5);
                Math.Abs(wobbleAcross).Should().BeLessThanOrEqualTo(0.5);
            }
        }

        ids.Select(id => ShapeModel.PersonalOffset(ShapeModel.Hash(id))).Distinct().Should().HaveCountGreaterThan(ids.Count / 2);
    }

    [Test]
    public void Players_in_the_same_line_no_longer_share_an_exact_depth()
    {
        var home = Team(_homeProfile);

        var placed = Place(home, Team(_awayProfile), _centreSpot, inPossession: null)
            .Where(p => p.Team == TeamSide.Home).ToList();

        var defenders = home.Slots.Where(s => s.Line == PlayerPosition.Defender)
            .Select(s => placed.Single(p => p.PlayerId == s.PlayerId).Position.X).ToList();
        defenders.Should().HaveCount(4);
        defenders.Distinct().Should().HaveCount(4);
    }

    [Test]
    public void The_wobble_changes_with_the_ball_but_only_by_the_wobble_bound()
    {
        var hash = ShapeModel.Hash("a-player");
        var first = ShapeModel.Wobble(hash, new PitchPosition(10, 10));
        var second = ShapeModel.Wobble(hash, new PitchPosition(11, 10));

        second.Should().NotBe(first);
        Math.Abs(second.Depth - first.Depth).Should().BeLessThanOrEqualTo(1.0);
        Math.Abs(second.Across - first.Across).Should().BeLessThanOrEqualTo(1.0);
        ShapeModel.Wobble(hash, new PitchPosition(10, 10)).Should().Be(first);
    }

    [Test]
    public void The_goalkeeper_has_no_depth_nudge()
    {
        var home = Team(_homeProfile);
        var keeper = home.Sheet.StartingEleven[0];

        var ball = Place(home, Team(_awayProfile), _centreSpot, TeamSide.Home).Single(p => p.PlayerId == keeper).Position;
        var nearby = Place(home, Team(_awayProfile), new PitchPosition(52.5, 34.3), TeamSide.Home).Single(p => p.PlayerId == keeper).Position;

        // His line is 5 m out, a tenth of the ball's depth from the centre (which is zero here).
        ball.X.Should().BeApproximately(5, 1e-9);
        nearby.X.Should().Be(ball.X);
    }

    [TestCase(0.0, 0.0)]
    [TestCase(105.0, 68.0)]
    [TestCase(0.0, 68.0)]
    [TestCase(105.0, 0.0)]
    public void No_one_leaves_the_pitch_with_the_ball_in_a_corner(double x, double y)
    {
        foreach (var possession in new TeamSide?[] { TeamSide.Home, TeamSide.Away, null })
        {
            var placed = Place(Team(_homeProfile), Team(_awayProfile), new PitchPosition(x, y), possession);

            placed.Should().OnlyContain(p => p.Position.X >= 0 && p.Position.X <= PitchDimensions.Length
                && p.Position.Y >= 0 && p.Position.Y <= PitchDimensions.Width);
        }
    }

    [TestCase(0.0, 0.0)]
    [TestCase(0.0, 68.0)]
    public void With_the_ball_in_their_own_corner_no_outfield_player_is_behind_his_keeper(double x, double y)
    {
        var home = Team(_homeProfile);
        var away = Team(_awayProfile);

        // The home team's own corner, then the away team's (the same corner turned half way round), the other team in possession.
        var homePlaced = Place(home, away, new PitchPosition(x, y), TeamSide.Away);
        var awayPlaced = Place(home, away, new PitchPosition(PitchDimensions.Length - x, PitchDimensions.Width - y), TeamSide.Home);

        foreach (var (placed, team, side) in new[] { (homePlaced, home, TeamSide.Home), (awayPlaced, away, TeamSide.Away) })
        {
            double Depth(PlacedPlayer p) => side == TeamSide.Home ? p.Position.X : PitchDimensions.Length - p.Position.X;
            var keeper = Depth(placed.Single(p => p.Team == side && p.PlayerId == team.Sheet.StartingEleven[0]));
            var depths = Outfield(placed, team, side).Select(Depth).ToList();

            // The ball pull can draw a player up to 30% back towards a ball on the goal line, after the floor.
            depths.Should().OnlyContain(d => d >= (4 * 0.7) - 1e-9);
            depths.Should().OnlyContain(d => d > keeper);
        }
    }

    [Test]
    public void Neighbours_in_a_crowded_line_never_meet_or_swap_whatever_the_ball_does()
    {
        var home = Team(_homeProfile);
        var eleven = home.Sheet.StartingEleven;
        var positions = eleven.Select((id, i) => (id, line: i == 0 ? PlayerPosition.Goalkeeper : i <= 7 ? PlayerPosition.Defender : PlayerPosition.Midfielder))
            .ToDictionary(p => p.id, p => p.line, StringComparer.Ordinal);
        var team = WithTendencies(new ShapeTeam(home.Sheet, home.Tendencies, positions), t => t with { Width = 0 });
        var defenders = team.Slots.Where(s => s.Line == PlayerPosition.Defender).OrderBy(s => s.Index).Select(s => s.PlayerId).ToList();
        defenders.Should().HaveCount(7);
        var away = Team(_awayProfile);

        // The narrowest the line gets is defending, with width 0: 48 * 0.6 / 6 = 4.8 m between neighbours.
        const double spacing = 4.8;
        for (var bx = 0; bx <= 105; bx += 7)
        {
            for (var by = 0; by <= 68; by += 4)
            {
                var placed = Place(team, away, new PitchPosition(bx, by), TeamSide.Away);
                var ys = defenders.Select(id => placed.Single(p => p.PlayerId == id).Position.Y).ToList();
                for (var i = 1; i < ys.Count; i++)
                {
                    // The nudge leaves at least 30% of the spacing; the ball pull can squeeze the line a little more.
                    (ys[i] - ys[i - 1]).Should().BeGreaterThan(0.2 * spacing, $"ball at ({bx}, {by}), pair {i}");
                }
            }
        }
    }

    [Test]
    public void The_nudge_for_a_known_player_and_ball_is_pinned()
    {
        var hash = ShapeModel.Hash("player-7");

        var offset = ShapeModel.PersonalOffset(hash);
        var wobble = ShapeModel.Wobble(hash, new PitchPosition(52.5, 34));

        offset.Depth.Should().BeApproximately(-1.3587190589293052, 1e-12);
        offset.Across.Should().BeApproximately(0.6293026971316547, 1e-12);
        wobble.Depth.Should().BeApproximately(0.39678679919535886, 1e-12);
        wobble.Across.Should().BeApproximately(-0.13799472576432026, 1e-12);
    }

    [Test]
    public void A_player_sent_off_is_left_off_the_pitch()
    {
        var home = Team(_homeProfile);
        var sentOff = home.Sheet.StartingEleven[5];

        var placed = ShapeModel.Place(home, Team(_awayProfile), _centreSpot, TeamSide.Home, dismissed: [sentOff]);

        placed.Should().HaveCount(21).And.NotContain(p => p.PlayerId == sentOff);
    }

    [Test]
    public void Players_move_towards_the_side_the_ball_is_on()
    {
        var home = Team(_homeProfile);
        var away = Team(_awayProfile);

        var low = Outfield(Place(home, away, new PitchPosition(52.5, 8), TeamSide.Home), home, TeamSide.Home).Average(p => p.Position.Y);
        var high = Outfield(Place(home, away, new PitchPosition(52.5, 60), TeamSide.Home), home, TeamSide.Home).Average(p => p.Position.Y);

        high.Should().BeGreaterThan(low + 5);
    }

    [Test]
    public void The_goalkeeper_is_the_first_profile_goalkeeper_on_the_sheet_and_any_other_plays_in_defence()
    {
        var team = Team(_homeProfile);
        var eleven = team.Sheet.StartingEleven;
        var otherKeeper = _homeProfile.Squad.First(p => p.Position == PlayerPosition.Goalkeeper && !eleven.Contains(p.Id)).Id;
        // A defender is listed first, then the keeper, then a second keeper in place of a forward.
        List<string> reordered = [eleven[1], eleven[0], .. eleven.Skip(2).Take(8), otherKeeper];
        var shuffled = team with { Sheet = new TeamSheet(team.Sheet.Id, team.Sheet.Name, team.Sheet.ShortName, reordered) };

        var placed = Place(shuffled, Team(_awayProfile), _centreSpot, TeamSide.Home);

        placed.Single(p => p.PlayerId == eleven[0]).Position.X.Should().BeLessThan(PitchDimensions.PenaltyAreaDepth);
        placed.Single(p => p.PlayerId == eleven[1]).Position.X.Should().BeGreaterThan(PitchDimensions.PenaltyAreaDepth);
        placed.Single(p => p.PlayerId == otherKeeper).Position.X.Should().BeGreaterThan(PitchDimensions.PenaltyAreaDepth);
    }

    [Test]
    public void A_team_with_no_profile_plays_a_4_4_2_from_sheet_order_with_average_tendencies()
    {
        var sheet = Team(_homeProfile).Sheet;

        var team = ShapeTeam.WithDefaults(sheet);
        var placed = Place(team, Team(_awayProfile), _centreSpot, TeamSide.Home);

        team.Tendencies.Should().Be(new TeamTendencies(0.5, 0.5, 0.5, 0.5));
        team.Slots.GroupBy(s => s.Line).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(new Dictionary<PlayerPosition, int>
        {
            [PlayerPosition.Goalkeeper] = 1,
            [PlayerPosition.Defender] = 4,
            [PlayerPosition.Midfielder] = 4,
            [PlayerPosition.Forward] = 2
        });
        team.Slots[0].Line.Should().Be(PlayerPosition.Goalkeeper);
        placed.Should().HaveCount(22);
    }

    [Test]
    public void The_formation_is_worked_out_once_per_team_and_afresh_for_a_copy()
    {
        var team = Team(_homeProfile);

        var first = team.Slots;
        var copy = team with { Tendencies = team.Tendencies with { Width = 0.9 } };

        team.Slots.Should().BeSameAs(first);
        copy.Slots.Should().NotBeSameAs(first).And.Equal(first);
    }

    [Test]
    public void A_shot_saved_or_blocked_gives_the_ball_away_and_a_goal_or_miss_stops_play()
    {
        var shots = Touchline.MatchCentre.Demo.DemoRecording.Events.Where(e => e.Shot is not null).ToList();

        shots.Should().Contain(e => e.Shot!.Outcome == ShotOutcome.Goal);
        foreach (var shot in shots)
        {
            var other = shot.Team == TeamSide.Home ? TeamSide.Away : TeamSide.Home;
            TeamSide? expected = shot.Shot!.Outcome is ShotOutcome.Saved or ShotOutcome.Blocked ? other : null;
            ShapeModel.PossessionAfter(shot).Should().Be(expected, $"a {shot.Shot.Outcome} shot");
        }
    }
}
