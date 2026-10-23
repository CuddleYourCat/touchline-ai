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
