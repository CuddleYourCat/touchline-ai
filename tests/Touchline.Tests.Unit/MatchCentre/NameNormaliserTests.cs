using FluentAssertions;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class NameNormaliserTests
{
    [TestCase("Blocked United", "blocked united")]
    [TestCase("Blöcked Üníted", "blocked united")]
    [TestCase("Blосked Unіted", "blocked united")]
    [TestCase("ΒLOCKED UNITED", "blocked united")]
    [TestCase("Block.ed Un'ited", "blocked united")]
    [TestCase("Block’ed  Un-ited ", "blocked united")]
    [TestCase("Ｂlocked United", "blocked united")]
    [TestCase("Łukasz Ødegård", "lukasz odegard")]
    public void Look_alike_and_punctuated_spellings_reduce_to_the_plain_term(string typed, string expected)
    {
        NameNormaliser.Normalise(typed).Should().Be(expected);
    }

    [TestCase("Arlo Brinmere", true)]
    [TestCase("Арло Бринмер", true)]
    [TestCase("Αρλο Brinmere", false)]
    [TestCase("Arlo Бrinmere", false)]
    public void A_name_does_not_mix_Latin_letters_with_Cyrillic_or_Greek(string name, bool fine)
    {
        (NameRules.FormatProblem(name) is null).Should().Be(fine);
    }
}
