using FluentAssertions;
using FluentAssertions.Execution;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class FanTeamProfilesTests
{
    private static readonly IReadOnlyList<Player> _builtInPlayers =
        [.. TeamProfileCatalog.LoadEmbedded().Values.SelectMany(t => t.Squad)];

    private static readonly IReadOnlyList<TeamTendencies> _builtInTendencies =
        [.. TeamProfileCatalog.LoadEmbedded().Values.Select(t => t.Tendencies)];

    [Test]
    public void Every_archetype_rating_lies_inside_the_built_in_range_for_its_position()
    {
        using var scope = new AssertionScope();
        foreach (var archetype in Enum.GetValues<PlayerArchetype>())
        {
            var ratings = FanTeamProfiles.RatingsOf(archetype);
            var peers = _builtInPlayers.Where(p => p.Position == Archetypes.PositionOf(archetype)).ToList();

            InRange(ratings.TopSpeedKmh, peers.Select(p => p.TopSpeedKmh), $"{archetype} top speed");
            InRange(ratings.Finishing, peers.Select(p => p.Finishing), $"{archetype} finishing");
            InRange(ratings.Passing, peers.Select(p => p.Passing), $"{archetype} passing");
            InRange(ratings.Discipline, peers.Select(p => p.Discipline), $"{archetype} discipline");
            ratings.Role.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void Every_manager_style_lies_inside_the_built_in_range_of_each_tendency()
    {
        using var scope = new AssertionScope();
        foreach (var style in Enum.GetValues<ManagerStyle>())
        {
            var tendencies = FanTeamProfiles.TendenciesOf(style);

            InRange(tendencies.PressingIntensity, _builtInTendencies.Select(t => t.PressingIntensity), $"{style} pressing");
            InRange(tendencies.Width, _builtInTendencies.Select(t => t.Width), $"{style} width");
            InRange(tendencies.Directness, _builtInTendencies.Select(t => t.Directness), $"{style} directness");
            InRange(tendencies.ShotAppetite, _builtInTendencies.Select(t => t.ShotAppetite), $"{style} shot appetite");
        }
    }

    [Test]
    public void The_styles_are_all_different()
    {
        Enum.GetValues<ManagerStyle>().Select(FanTeamProfiles.TendenciesOf).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void A_built_team_keeps_the_fans_choices_and_takes_its_ratings_from_the_tables()
    {
        var team = FanTeamProfiles.Build(TestFanTeams.Standard("fan-build", ManagerStyle.Possession));

        team.Should().BeEquivalentTo(new
        {
            Id = "fan-build",
            Name = "Test Rovers",
            ShortName = "TRV",
            Venue = "Test Park",
            Colours = new TeamColours("#123456", "#ffffff"),
            Tendencies = FanTeamProfiles.TendenciesOf(ManagerStyle.Possession)
        });
        team.Squad[9].Should().Be(new Player(
            "fan-build-p10", "Test Player 10", 10, PlayerPosition.Forward, "poacher who lives in the box", PreferredFoot.Left,
            TopSpeedKmh: 33.0, Finishing: 0.66, Passing: 0.38, Discipline: 0.5));
        team.Squad.Should().OnlyContain(p => p.PenaltyAim == null && p.Dive == null, "fan players take the league-average penalty duel");
    }

    [Test]
    public void The_same_spec_always_builds_the_same_profile()
    {
        var (first, _) = TestFanTeams.AllOutAttack();
        var (second, _) = TestFanTeams.AllOutAttack();

        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    [Test]
    public void A_team_the_catalog_would_reject_is_refused()
    {
        var spec = TestFanTeams.Spec("fan-short", ManagerStyle.Balanced, PlayerArchetype.Poacher, PlayerArchetype.Poacher);

        var build = () => FanTeamProfiles.Build(spec);

        build.Should().Throw<ArgumentException>().WithMessage("*fan-short*no goalkeeper*");
    }

    [Test]
    public void Duplicate_shirt_numbers_are_refused()
    {
        var spec = TestFanTeams.Spec("fan-dupe", ManagerStyle.Balanced, Enumerable.Repeat(PlayerArchetype.ShotStopper, 11).ToArray());
        spec = spec with { Players = [.. spec.Players.Select(p => p with { ShirtNumber = 1 })] };

        var build = () => FanTeamProfiles.Build(spec);

        build.Should().Throw<ArgumentException>().WithMessage("*duplicate shirt number '1'*");
    }

    [Test]
    public void A_team_id_without_the_fan_prefix_is_refused()
    {
        var spec = TestFanTeams.Standard("fan-ok", ManagerStyle.Balanced) with { Id = "north-bank-fc" };

        var build = () => FanTeamProfiles.Build(spec);

        build.Should().Throw<ArgumentException>().WithMessage("*must start with 'fan-'*");
    }

    [Test]
    public void A_squad_breaking_the_squad_rules_is_refused_with_every_problem()
    {
        var standard = TestFanTeams.Standard("fan-rules", ManagerStyle.Balanced);
        // Thirteen players, one short, and the last forward wearing 100.
        var spec = standard with
        {
            Players = [.. standard.Players.Take(13).Select(p => p.ShirtNumber == 11 ? p with { ShirtNumber = 100 } : p)]
        };

        var build = () => FanTeamProfiles.Build(spec);

        build.Should().Throw<ArgumentException>()
            .WithMessage("*14 to 25 players; this one has 13*")
            .WithMessage("*Shirt number 100 isn't 1 to 99*");
    }

    [Test]
    public void A_squad_short_of_a_position_is_refused()
    {
        var standard = TestFanTeams.Standard("fan-few", ManagerStyle.Balanced);
        var spec = standard with { Players = [.. standard.Players.Select(p => p.Archetype == PlayerArchetype.ShotStopper ? p with { Archetype = PlayerArchetype.Poacher } : p)] };

        var build = () => FanTeamProfiles.Build(spec);

        build.Should().Throw<ArgumentException>().WithMessage("*at least 2 of position Goalkeeper; this one has 1*");
    }

    [Test]
    public void The_largest_squad_the_rules_allow_builds()
    {
        var standard = TestFanTeams.Standard("fan-big", ManagerStyle.Balanced);
        var extra = Enumerable.Range(15, SquadRules.MaxPlayers - 14)
            .Select(n => new FanPlayerSpec($"fan-big-p{n}", $"Test Player {n}", n, PreferredFoot.Right, PlayerArchetype.BoxToBox));
        var spec = standard with { Players = [.. standard.Players, .. extra] };

        FanTeamProfiles.Build(spec).Squad.Should().HaveCount(SquadRules.MaxPlayers);
        ((Action)(() => FanTeamProfiles.Build(spec with { Players = [.. spec.Players, spec.Players[0] with { Id = "fan-big-p99", ShirtNumber = 99 }] })))
            .Should().Throw<ArgumentException>().WithMessage("*14 to 25 players; this one has 26*");
    }

    [Test]
    public void An_empty_player_entry_names_the_players_list()
    {
        var standard = TestFanTeams.Standard("fan-null", ManagerStyle.Balanced);
        var spec = standard with { Players = [.. standard.Players, null!] };

        var build = () => FanTeamProfiles.Build(spec);

        build.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("spec.Players");
    }

    [Test]
    public void An_undefined_archetype_or_style_is_refused()
    {
        var badArchetype = TestFanTeams.Spec("fan-bad", ManagerStyle.Balanced, (PlayerArchetype)99);
        var badStyle = TestFanTeams.Spec("fan-bad", (ManagerStyle)99, PlayerArchetype.Poacher);

        ((Action)(() => FanTeamProfiles.Build(badArchetype))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => FanTeamProfiles.Build(badStyle))).Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void InRange(double value, IEnumerable<double> builtIn, string because)
    {
        var values = builtIn.ToList();
        value.Should().BeInRange(values.Min(), values.Max(), because);
    }
}
