using FluentAssertions;
using Touchline.Domain.Officials;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public class RefereeCatalogTests
{
    // Surnames of real Premier League referees, past and present. If one turns up in the
    // fictional catalog, it's an IP problem (ADR-002), so the build fails.
    private static readonly string[] _realRefereeSurnames =
    [
        "Oliver", "Taylor", "Attwell", "Kavanagh", "Pawson", "Tierney", "England", "Gillett", "Brooks", "Madley",
        "Hooper", "Salisbury", "Jones", "Bankes", "Robinson", "Barrott", "Bramall", "Coote", "Atkinson", "Dean",
        "Marriner", "Moss", "Webb", "Clattenburg", "Friend", "Scott", "Smith", "Harrington", "Allison", "Donohue",
        "Kirk", "Burnett", "Halsey", "Foy", "Riley", "Poll", "Durkin", "Probert", "Swarbrick", "Mason"
    ];

    [Test]
    public void Four_referees_range_from_strict_to_lenient()
    {
        var referees = RefereeCatalog.LoadEmbedded();

        referees.Should().HaveCount(4);
        referees.Select(r => r.Id).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        referees.Max(r => r.Strictness).Should().BeGreaterThan(0.8);
        referees.Min(r => r.Strictness).Should().BeLessThan(0.2);
        referees.Average(r => r.Strictness).Should().BeApproximately(0.5, 0.05, "an average referee is 0.5");
        referees.Average(r => r.Whistle).Should().BeApproximately(0.5, 0.05);
    }

    [Test]
    public void No_referee_has_a_real_referee_s_surname()
    {
        RefereeCatalog.LoadEmbedded().Select(r => r.Name.Split(' ')[^1].Split('-')[0])
            .Should().NotIntersectWith(_realRefereeSurnames, "officials are fictional (ADR-002)");
    }

    [Test]
    public void A_seed_always_picks_the_same_referee_and_every_referee_gets_matches()
    {
        RefereeCatalog.ForSeed(42).Should().BeSameAs(RefereeCatalog.ForSeed(42));
        RefereeCatalog.ForSeed(-7).Should().NotBeNull("negative seeds are valid seeds");
        Enumerable.Range(0, 4).Select(RefereeCatalog.ForSeed).Distinct().Should().HaveCount(4);
    }

    [TestCase("\"strictness\": 0.5", "\"strictness\": 2", "strictness is 2", TestName = "A referee trait above 1 is rejected")]
    [TestCase("\"style\": \"steady\", ", "", "style", TestName = "A referee without a style is rejected")]
    public void An_invalid_referee_is_rejected_with_the_source_named(string find, string replaceWith, string expected)
    {
        var json = "[ { \"id\": \"ref-test\", \"name\": \"Test Ref\", \"style\": \"steady\", \"strictness\": 0.5, \"whistle\": 0.5, \"advantage\": 0.5 } ]"
            .Replace(find, replaceWith, StringComparison.Ordinal);

        var parse = () => RefereeCatalog.Parse("refs.json", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

        parse.Should().Throw<InvalidDataException>().WithMessage($"*refs.json*{expected}*");
    }

    [Test]
    public void An_empty_referee_list_is_rejected()
    {
        var parse = () => RefereeCatalog.Parse("empty.json", new MemoryStream("[]"u8.ToArray()));

        parse.Should().Throw<InvalidDataException>().WithMessage("*empty.json*at least one referee*");
    }
}
