using FluentAssertions;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class TeamProfileCatalogTests
{
    // Surnames of real players at the club North Bank FC is loosely inspired by. If one
    // ever turns up in a fictional squad, it's an IP problem (ADR-002), so the build fails.
    private static readonly string[] _realSurnames =
    [
        "Saka", "Odegaard", "Ødegaard", "Rice", "Saliba", "Gabriel", "Raya", "Martinelli", "Havertz",
        "Trossard", "White", "Timber", "Calafiori", "Merino", "Partey", "Jesus", "Zinchenko",
        "Kiwior", "Nwaneri", "Lewis-Skelly", "Gyokeres", "Gyökeres", "Zubimendi", "Madueke", "Eze",
        "Mosquera", "Hincapie", "Hincapié", "Arrizabalaga", "Norgaard", "Nørgaard"
    ];

    private IReadOnlyDictionary<string, TeamProfile> _profiles = null!;

    [OneTimeSetUp]
    public void LoadProfiles() => _profiles = TeamProfileCatalog.LoadEmbedded();

    [Test]
    public void Both_clubs_are_embedded()
    {
        _profiles.Keys.Should().BeEquivalentTo("north-bank-fc", "thames-valley-united");
        _profiles["north-bank-fc"].Name.Should().Be("North Bank FC");
        _profiles["thames-valley-united"].Name.Should().Be("Thames Valley United");
    }

    [Test]
    public void Every_squad_can_field_a_team()
    {
        foreach (var profile in _profiles.Values)
        {
            profile.Squad.Should().HaveCountGreaterThanOrEqualTo(11, profile.Name);
            profile.Squad.Should().Contain(p => p.Position == PlayerPosition.Goalkeeper, profile.Name);
        }
    }

    [Test]
    public void Player_ids_are_unique_across_both_squads()
    {
        var ids = _profiles.Values.SelectMany(p => p.Squad).Select(p => p.Id);

        ids.Should().OnlyHaveUniqueItems("events identify players by ID alone");
    }

    [Test]
    public void No_squad_contains_a_real_player_s_surname()
    {
        var surnames = _profiles.Values
            .SelectMany(p => p.Squad)
            .Select(p => p.Name.Split(' ')[^1]);

        surnames.Should().NotIntersectWith(_realSurnames, "clubs and players are fictional (ADR-002)");
    }

    [Test]
    public void Profiles_are_loaded_once_and_shared()
    {
        TeamProfileCatalog.LoadEmbedded().Should().BeSameAs(_profiles);
    }

    [Test]
    public void A_player_without_a_role_is_rejected_with_the_source_named()
    {
        var json = ValidProfileJson("missing-role").Replace("\"role\": \"keeper\"", "\"role\": null", StringComparison.Ordinal);

        var parse = () => TeamProfileCatalog.Parse([("missing-role.json", ToStream(json))]);

        parse.Should().Throw<InvalidDataException>().WithMessage("*missing-role.json*squad entry 1's role is missing*");
    }

    [Test]
    public void Two_files_with_the_same_team_id_are_rejected_with_both_named()
    {
        var parse = () => TeamProfileCatalog.Parse(
        [
            ("first.json", ToStream(ValidProfileJson("same-id"))),
            ("second.json", ToStream(ValidProfileJson("same-id")))
        ]);

        parse.Should().Throw<InvalidDataException>().WithMessage("*'same-id'*first.json*second.json*");
    }

    [Test]
    public void Malformed_json_is_reported_as_invalid_data()
    {
        var parse = () => TeamProfileCatalog.Parse([("broken.json", ToStream("{ \"id\": "))]);

        parse.Should().Throw<InvalidDataException>().WithMessage("*broken.json*");
    }

    [Test]
    public void A_player_can_be_found_by_id()
    {
        var winger = _profiles["north-bank-fc"].FindPlayer("nbfc-07");

        winger.Should().NotBeNull();
        winger!.Role.Should().Contain("left-footed");
        _profiles["north-bank-fc"].FindPlayer("tvu-07").Should().BeNull();
    }

    // Added in the richer-events slice (ADR-019)

    [Test]
    public void A_role_that_names_a_foot_agrees_with_the_preferred_foot()
    {
        var squads = _profiles.Values.SelectMany(p => p.Squad).ToList();

        squads.Where(p => p.Role.Contains("left-footed", StringComparison.Ordinal))
            .Should().OnlyContain(p => p.PreferredFoot == PreferredFoot.Left);
        squads.Where(p => p.Role.Contains("two-footed", StringComparison.Ordinal))
            .Should().NotBeEmpty().And.OnlyContain(p => p.PreferredFoot == PreferredFoot.Both);
    }

    [Test]
    public void Squad_ratings_average_close_to_an_average_player()
    {
        var squads = _profiles.Values.SelectMany(p => p.Squad).Where(p => p.Position != PlayerPosition.Goalkeeper).ToList();

        // 0.5 is average by definition, so xG and xPass stay honest across a whole league.
        squads.Average(p => p.Finishing).Should().BeInRange(0.42, 0.58);
        squads.Average(p => p.Passing).Should().BeInRange(0.45, 0.55);
    }

    [TestCase("\"topSpeedKmh\": 32.5", "\"topSpeedKmh\": 9.0", "top speed is 9 km/h", TestName = "A top speed in m/s is rejected")]
    [TestCase("\"finishing\": 0.5", "\"finishing\": 5", "finishing is 5", TestName = "A rating above 1 is rejected")]
    [TestCase("\"preferredFoot\": \"Right\", ", "", "preferredFoot", TestName = "A player without a preferred foot is rejected")]
    [TestCase("\"venue\": \"Test Ground\",", "", "venue", TestName = "A team without a venue is rejected")]
    public void A_profile_with_a_bad_or_missing_trait_is_rejected(string find, string replaceWith, string expected)
    {
        var json = ValidProfileJson("bad-trait").Replace(find, replaceWith, StringComparison.Ordinal);

        var parse = () => TeamProfileCatalog.Parse([("bad-trait.json", ToStream(json))]);

        parse.Should().Throw<InvalidDataException>().WithMessage($"*bad-trait.json*{expected}*");
    }

    private static MemoryStream ToStream(string json) => new(System.Text.Encoding.UTF8.GetBytes(json));

    private static string ValidProfileJson(string id)
    {
        var players = Enumerable.Range(1, 11).Select(n =>
            $$"""{ "id": "{{id}}-{{n}}", "name": "Player {{n}}", "shirtNumber": {{n}}, "position": "{{(n == 1 ? "Goalkeeper" : "Midfielder")}}", "role": "{{(n == 1 ? "keeper" : "runner")}}", "preferredFoot": "Right", "topSpeedKmh": 32.5, "finishing": 0.5, "passing": 0.5 }""");

        return $$"""
            {
              "id": "{{id}}", "name": "Test Club", "shortName": "TST", "venue": "Test Ground",
              "colours": { "primary": "#000", "secondary": "#fff" },
              "tendencies": { "pressingIntensity": 0.5, "width": 0.5, "directness": 0.5, "shotAppetite": 0.5 },
              "squad": [ {{string.Join(", ", players)}} ]
            }
            """;
    }
}
