using FluentAssertions;
using Touchline.Domain.Viewing;

namespace Touchline.Tests.Unit.Domain;

[TestFixture]
public sealed class LanguageTagsTests
{
    [TestCase("es-mx", "es-MX")]
    [TestCase("ES-MX", "es-MX")]
    [TestCase("Es-mX", "es-MX")]
    [TestCase("FR", "fr")]
    [TestCase("zh-hant-tw", "zh-hant-TW")]
    public void A_tag_is_normalised_to_a_lower_case_base_and_an_upper_case_region(string tag, string expected) =>
        LanguageTags.Normalise(tag).Should().Be(expected);

    [TestCase("ja-JP", "en")]
    [TestCase("es-MX", "es")]
    [TestCase("fr", "fr")]
    [TestCase("FR-ca", "fr")]
    [TestCase(null, "en")]
    [TestCase(" ", "en")]
    public void The_closest_default_language_is_chosen(string? tag, string expected) =>
        LanguageTags.Closest(tag, ["en", "es", "fr"]).Should().Be(expected);

    [Test]
    public void A_configured_regional_tag_beats_its_base_language() =>
        LanguageTags.Closest("es-mx", ["en", "es", "ES-MX"]).Should().Be("es-MX");
}
