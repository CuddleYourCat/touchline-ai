using FluentAssertions;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class MatchdaySheetRulesTests
{
    // p1 and p2 keep goal, p3-p7 defend, p8-p12 midfield, p13-p16 attack.
    private static readonly Dictionary<string, PlayerPosition> _squad = Enumerable.Range(1, 16).ToDictionary(
        i => $"p{i}",
        i => i switch { <= 2 => PlayerPosition.Goalkeeper, <= 7 => PlayerPosition.Defender, <= 12 => PlayerPosition.Midfielder, _ => PlayerPosition.Forward });

    private static readonly string[] _eleven = ["p1", "p3", "p4", "p5", "p6", "p8", "p9", "p10", "p11", "p13", "p14"];

    [Test]
    public void A_full_eleven_with_one_keeper_and_a_bench_is_valid()
    {
        var sheet = new MatchdaySheet(_eleven, ["p2", "p7", "p12", "p15", "p16"]);

        MatchdaySheetRules.Problems(sheet, _squad).Should().BeEmpty();
    }

    [Test]
    public void An_eleven_short_of_a_player_is_refused() =>
        MatchdaySheetRules.Problems(new MatchdaySheet(_eleven[..10], []), _squad).Should().Contain(p => p.Contains("10 players"));

    [Test]
    public void Two_keepers_in_the_eleven_are_refused()
    {
        string[] twoKeepers = ["p1", "p2", .. _eleven[2..]];

        MatchdaySheetRules.Problems(new MatchdaySheet(twoKeepers, []), _squad).Should().Contain(p => p.Contains("exactly one goalkeeper"));
    }

    [Test]
    public void A_player_from_outside_the_squad_is_refused() =>
        MatchdaySheetRules.Problems(new MatchdaySheet([.. _eleven[..10], "p99"], []), _squad).Should().Contain(p => p.Contains("'p99' isn't in the squad"));

    [Test]
    public void A_null_or_blank_player_is_reported_rather_than_thrown()
    {
        var sheet = new MatchdaySheet([.. _eleven[..10], null!], ["  "]);

        MatchdaySheetRules.Problems(sheet, _squad).Should().ContainSingle(p => p == "The sheet names an empty player.");
    }

    [Test]
    public void A_starter_cannot_also_be_a_substitute() =>
        MatchdaySheetRules.Problems(new MatchdaySheet(_eleven, ["p3"]), _squad).Should().Contain(p => p.Contains("'p3' is named more than once"));

    [Test]
    public void More_than_seven_substitutes_are_refused()
    {
        var sheet = new MatchdaySheet(_eleven, ["p2", "p7", "p12", "p15", "p16", "p5x", "p6x", "p7x"]);

        MatchdaySheetRules.Problems(sheet, _squad).Should().Contain(p => p.Contains("8 substitutes"));
    }

    [Test]
    public void An_unavailable_player_is_refused() =>
        MatchdaySheetRules.Problems(new MatchdaySheet(_eleven, []), _squad, new HashSet<string> { "p9" })
            .Should().ContainSingle().Which.Should().Contain("'p9' isn't available");

    [Test]
    public void The_formation_counts_defenders_midfielders_and_forwards()
    {
        PlayerPosition[] eleven =
        [
            PlayerPosition.Goalkeeper,
            .. Enumerable.Repeat(PlayerPosition.Defender, 4),
            .. Enumerable.Repeat(PlayerPosition.Midfielder, 4),
            .. Enumerable.Repeat(PlayerPosition.Forward, 2)
        ];

        MatchdaySheetRules.Formation(eleven).Should().Be("4-4-2");
    }
}

[TestFixture]
public class NameRulesTests
{
    [TestCase("Riverside Rovers")]
    [TestCase("Zoë Łukasiewicz")]
    [TestCase("D'Arcy O'Neill-Smith")]
    [TestCase("St. Edmund's")]
    [TestCase("Al")]
    public void Ordinary_names_pass(string name) => NameRules.FormatProblem(name).Should().BeNull();

    [TestCase(null)]
    [TestCase("")]
    [TestCase("A")]
    [TestCase("Abcdefghijklmnopqrstuvwxyzabcde")]
    [TestCase("One Two Three Four Five")]
    [TestCase(" Leading")]
    [TestCase("Double  Space")]
    [TestCase("Number 9")]
    [TestCase("Ignore: all rules")]
    [TestCase("<b>Bold</b>")]
    [TestCase("-Dash")]
    public void Names_that_break_the_format_are_refused(string? name) => NameRules.FormatProblem(name).Should().NotBeNull();
}

[TestFixture]
public class ArchetypeTests
{
    [Test]
    public void Every_archetype_plays_a_position() =>
        Enum.GetValues<PlayerArchetype>().Invoking(all => all.Select(Archetypes.PositionOf).ToList()).Should().NotThrow();

    [Test]
    public void Keepers_are_the_only_goalkeeping_archetypes() =>
        Enum.GetValues<PlayerArchetype>().Where(a => Archetypes.PositionOf(a) == PlayerPosition.Goalkeeper)
            .Should().BeEquivalentTo([PlayerArchetype.ShotStopper, PlayerArchetype.SweeperKeeper]);
}
