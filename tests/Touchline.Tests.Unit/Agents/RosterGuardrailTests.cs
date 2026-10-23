using FluentAssertions;
using Touchline.Agents;

namespace Touchline.Tests.Unit.Agents;

[TestFixture]
public sealed class RosterGuardrailTests
{
    private static readonly string[] _roster = ["Tom Okafor", "Luca de Rossetti", "José Almeida", "Kwame Mensah"];
    private static readonly string[] _others = ["North Bank FC", "NBK", "Harbour Lane", "Riverside Athletic", "Dana Whitfield"];

    [TestCase("Tom Okafor fires it into the top corner!")]
    [TestCase("A cross from Okafor and Mensah heads it in.")]
    [TestCase("What a ball from Luca de Rossetti.")]
    [TestCase("Jose Almeida makes the save.")]
    [TestCase("Brilliant Okafor! Unbelievable.")]
    public void A_roster_name_passes(string text) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others).Should().BeEmpty();

    [TestCase("And John Smith scores for the visitors.", "John Smith")]
    [TestCase("A fine pass from Smith, and it's in.", "Smith")]
    [TestCase("Okafor squares it for Harry Bellingford.", "Harry Bellingford")]
    [TestCase("Tom Okafor and Okafor Smith combine.", "Okafor Smith")]
    public void An_invented_name_is_caught(string text, string expected) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others).Should().Equal(expected);

    [TestCase("North Bank FC lead at Harbour Lane.")]
    [TestCase("A late equaliser for Riverside Athletic, and NBK are stunned.")]
    [TestCase("The keeper for North Bank's defence was beaten.")]
    public void A_team_or_venue_name_is_not_flagged(string text) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others).Should().BeEmpty();

    [Test]
    public void The_referee_passed_in_and_football_words_are_not_flagged() =>
        RosterGuardrail.FindUnknownNames(
                "GOAL! Referee Dana Whitfield checks with VAR. Penalty to Riverside Athletic in the Second Half.",
                _roster,
                _others)
            .Should().BeEmpty();

    [Test]
    public void A_sentence_opener_followed_by_an_unknown_surname_is_caught() =>
        RosterGuardrail.FindUnknownNames("Okafor shoots. Stunning Bellingford save!", _roster, _others)
            .Should().Equal("Bellingford");

    [TestCase("Saka! Through on goal.", "Saka")]
    [TestCase("What a run. Bellingford scores.", "Bellingford")]
    public void A_lone_unknown_surname_opening_a_sentence_is_caught(string text, string expected) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others).Should().Equal(expected);

    [TestCase("Brilliant Okafor!")]
    [TestCase("Superb from Mensah. Increíble Almeida.")]
    public void A_common_opener_before_a_roster_name_passes(string text) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others).Should().BeEmpty();

    [Test]
    public void Each_unknown_name_is_reported_once() =>
        RosterGuardrail.FindUnknownNames("Pass to John Smith. Back to John Smith again.", _roster, _others)
            .Should().Equal("John Smith");

    [TestCase("Okafor, a Spanish midfielder, curls it in.")]
    [TestCase("The best Brazilian finish of the season from Mensah.")]
    [TestCase("Mensah has scored in Europe and Africa.")]
    public void A_nationality_or_continent_is_not_flagged(string text) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others).Should().BeEmpty();

    [TestCase("¡Qué golazo de Okafor!")]
    [TestCase("Quel but ! Superbe finition de Mensah.")]
    [TestCase("Increíble. Golazo de Okafor en Harbour Lane.")]
    public void A_translation_opening_with_an_ordinary_word_passes(string text) =>
        RosterGuardrail.FindUnknownNames(text, _roster, _others, ["What a goal from Okafor!"]).Should().BeEmpty();

    [Test]
    public void A_translation_may_use_capitalised_words_from_the_approved_lines() =>
        RosterGuardrail.FindUnknownNames(
                "Un remate de Okafor con xG, segundo palo para el Wanderers.",
                _roster,
                _others,
                ["Okafor's xG shot finds the far post for the Wanderers."])
            .Should().BeEmpty();

    [Test]
    public void A_translation_with_an_unknown_name_mid_sentence_is_caught() =>
        RosterGuardrail.FindUnknownNames("Gran pase de Haaland", _roster, _others, ["Great pass from Okafor."])
            .Should().Equal("Haaland");
}
