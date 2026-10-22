using System.Xml.Linq;
using FluentAssertions;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class EditKeysTests
{
    [Test]
    public void A_new_key_matches_its_own_hash_and_no_other()
    {
        var (key, hash) = EditKeys.Create();
        var (otherKey, _) = EditKeys.Create();

        EditKeys.Matches(key, hash).Should().BeTrue();
        EditKeys.Matches(otherKey, hash).Should().BeFalse();
        hash.Should().MatchRegex("^[0-9a-f]{64}$").And.NotContain(key);
    }

    [TestCase(null)]
    [TestCase("")]
    public void A_missing_key_never_matches(string? key) => EditKeys.Matches(key, EditKeys.Hash("anything")).Should().BeFalse();

    [Test]
    public void A_read_only_team_never_matches() => EditKeys.Matches("anything", null).Should().BeFalse();
}

[TestFixture]
public class TeamDraftValidatorTests
{
    [Test]
    public void A_valid_draft_has_no_problems_and_sends_every_name_to_the_guard()
    {
        var (problems, names) = TeamDraftValidator.Check(TeamDrafts.Valid(), new HashSet<string>());

        problems.Should().BeEmpty();
        names.Should().HaveCount(21, "team, venue, short name, crest initials, manager and 16 players");
        names.Select(n => n.Path).Should().Contain(["name", "venue", "shortName", "crest.initials", "manager.name", "squad[15].name"]);
        names.Single(n => n.Path == "crest.initials").Name.Kind.Should().Be(NameKind.Abbreviation);
    }

    [Test]
    public void Problems_are_keyed_by_the_path_of_the_field_at_fault()
    {
        var draft = TeamDrafts.Valid() with
        {
            ShortName = "rr",
            Colours = new TeamColours("#ffffff", "#FFFFFF"),
            Squad = [.. TeamDrafts.Valid().Squad.Select((p, i) => i == 3 ? p with { Name = "Player 4", ShirtNumber = 100 } : p)]
        };

        var (problems, _) = TeamDraftValidator.Check(draft, new HashSet<string>());

        problems.Keys.Should().BeEquivalentTo("shortName", "colours.secondary", "squad[3].name", "squad[3].shirtNumber");
    }

    [Test]
    public void A_squad_short_of_goalkeepers_says_how_many_it_has()
    {
        var draft = TeamDrafts.Valid() with
        {
            Squad = [.. TeamDrafts.Valid().Squad.Select(p => p.Archetype == PlayerArchetype.SweeperKeeper ? p with { Archetype = PlayerArchetype.Poacher } : p)]
        };

        var (problems, _) = TeamDraftValidator.Check(draft, new HashSet<string>());

        problems["squad"].Should().ContainSingle().Which.Should().Be("A squad needs at least 2 goalkeepers; this one has 1.");
    }

    [Test]
    public void Shirt_numbers_must_be_unique()
    {
        var draft = TeamDrafts.Valid() with { Squad = [.. TeamDrafts.Valid().Squad.Select(p => p.ShirtNumber == 2 ? p with { ShirtNumber = 1 } : p)] };

        TeamDraftValidator.Check(draft, new HashSet<string>()).Problems["squad"].Should().Contain("Shirt number 1 is used more than once.");
    }

    [Test]
    public void An_update_may_only_name_players_already_in_the_team()
    {
        var draft = TeamDrafts.Valid() with { Squad = [.. TeamDrafts.Valid().Squad.Select((p, i) => i == 0 ? p with { Id = "someone-else" } : p)] };

        TeamDraftValidator.Check(draft, new HashSet<string> { "fan-x-p1" }).Problems.Should().ContainKey("squad[0].id");
    }

    [Test]
    public void Undefined_enum_values_are_refused()
    {
        var draft = TeamDrafts.Valid() with { Manager = new ManagerSpec("Hal Penmarrow", (ManagerStyle)42) };

        TeamDraftValidator.Check(draft, new HashSet<string>()).Problems.Should().ContainKey("manager.style");
    }
}

[TestFixture]
public class TeamViewsTests
{
    [Test]
    public void A_new_team_gets_a_fan_id_from_its_name_and_numbered_players()
    {
        var id = TeamViews.NewTeamId("Riverside Rovers!");
        var team = TeamViews.FromDraft(TeamDrafts.Valid(), id, previous: null);

        id.Should().MatchRegex("^fan-riverside-rovers-[a-z2-9]{6}$");
        team.Version.Should().Be(1);
        team.Team.Origin.Should().Be(TeamOrigin.Fan);
        team.Squad.Select(p => p.Id).Should().BeEquivalentTo(Enumerable.Range(1, 16).Select(n => $"{id}-p{n}"));
        team.Squad.First(p => p.Archetype == PlayerArchetype.Poacher).Position.Should().Be(PlayerPosition.Forward);
    }

    [Test]
    public void An_update_keeps_existing_ids_and_never_reuses_a_departed_players_number()
    {
        var first = TeamViews.FromDraft(TeamDrafts.Valid(), "fan-x", previous: null);
        var kept = first.Squad.Where(p => p.Id != "fan-x-p16").Select(p => new PlayerDraft(p.Name, p.ShirtNumber, p.Archetype!.Value, p.PreferredFoot, p.Id));

        var second = TeamViews.FromDraft(
            TeamDrafts.Valid() with { Squad = [.. kept, new PlayerDraft("Tobiah Wexford", 30, PlayerArchetype.Poacher, PreferredFoot.Left)] },
            "fan-x",
            first);

        second.Version.Should().Be(2);
        second.Squad.Select(p => p.Id).Should().Contain("fan-x-p1").And.Contain("fan-x-p17").And.NotContain("fan-x-p16");
    }

    [Test]
    public void A_built_in_team_is_read_only_with_a_crest_from_its_colours()
    {
        var profile = TeamProfileCatalog.LoadEmbedded()["north-bank-fc"];

        var team = TeamViews.FromProfile(profile);

        team.Team.Origin.Should().Be(TeamOrigin.BuiltIn);
        team.Crest.Initials.Should().Be("NBK");
        team.Crest.Primary.Should().Be(profile.Colours.Primary);
        team.Team.CrestUrl.Should().Be("/api/teams/north-bank-fc/crest.svg");
    }
}

[TestFixture]
public class CrestSvgTests
{
    private static IEnumerable<TestCaseData> EveryShapeAndEmblem() =>
        from shape in Enum.GetValues<CrestShape>()
        from emblem in Enum.GetValues<CrestEmblem>()
        select new TestCaseData(shape, emblem);

    [TestCaseSource(nameof(EveryShapeAndEmblem))]
    public void Every_crest_is_well_formed_svg_with_nothing_that_could_run(CrestShape shape, CrestEmblem emblem)
    {
        var svg = CrestSvg.Render(new CrestSpec(shape, emblem, "ABC", "#112233", "#ddeeff"));

        var document = XDocument.Parse(svg);
        document.Root!.Name.LocalName.Should().Be("svg");
        svg.Should().NotContainAny("<script", "href", "url(").And.NotMatchRegex(@"\son[a-z]+=", "no event-handler attributes");
    }
}

[TestFixture]
public class NameSuggesterTests
{
    [TestCase(NameKind.Team)]
    [TestCase(NameKind.Venue)]
    [TestCase(NameKind.Manager)]
    [TestCase(NameKind.Player)]
    public void Suggestions_are_distinct_and_meet_the_name_format(NameKind kind)
    {
        var names = new NameSuggester(new Random(7)).Suggest(kind, NameSuggester.MaxCount);

        names.Should().HaveCount(NameSuggester.MaxCount).And.OnlyHaveUniqueItems();
        names.Select(NameRules.FormatProblem).Should().OnlyContain(problem => problem == null);
    }
}
