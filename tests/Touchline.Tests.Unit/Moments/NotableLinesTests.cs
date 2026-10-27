using FluentAssertions;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Demo;

namespace Touchline.Tests.Unit.Moments;

[TestFixture]
public class NotableLinesTests
{
    private static readonly NotableMomentDetector _detector = new(new KeyMomentDetector());

    private static NotableMoment Moment(NotableMomentKind kind, string playerId = "nbfc-07")
    {
        var trigger = new MatchEvent
        {
            Sequence = 5,
            MatchId = "nbk-tvu-0001",
            PossessionId = 1,
            Time = MatchTime.At(MatchPeriod.FirstHalf, 12),
            Type = MatchEventType.Card,
            Team = TeamSide.Home,
            PlayerId = playerId,
            Start = new PitchPosition(50, 34)
        };
        return new NotableMoment(kind, trigger, "reason", TeamSide.Home, "Reggie Ashworth", "NBK",
            kind == NotableMomentKind.Substitution ? "Dario Pellwood" : null);
    }

    [TestCase(NotableMomentKind.YellowCard, "en", "Reggie Ashworth (NBK) goes into the book.")]
    [TestCase(NotableMomentKind.YellowCard, "es", "Reggie Ashworth (NBK) ve la tarjeta amarilla.")]
    [TestCase(NotableMomentKind.YellowCard, "fr", "Reggie Ashworth (NBK) prend un carton jaune.")]
    [TestCase(NotableMomentKind.Corner, "en", "Corner. It came off Reggie Ashworth (NBK).")]
    [TestCase(NotableMomentKind.Corner, "es", "Saque de esquina. El último en tocarlo fue Reggie Ashworth (NBK).")]
    [TestCase(NotableMomentKind.Corner, "fr", "Corner. Dernier contact de Reggie Ashworth (NBK).")]
    [TestCase(NotableMomentKind.Woodwork, "fr", "Sur le poteau ! Reggie Ashworth (NBK) est repoussé par le cadre.")]
    [TestCase(NotableMomentKind.Substitution, "en", "Substitution: Dario Pellwood comes on for Reggie Ashworth (NBK).")]
    [TestCase(NotableMomentKind.Substitution, "es", "Cambio: entra Dario Pellwood por Reggie Ashworth (NBK).")]
    [TestCase(NotableMomentKind.Substitution, "fr", "Remplacement : Dario Pellwood entre à la place de Reggie Ashworth (NBK).")]
    public void A_line_names_the_player_and_club_in_the_language(NotableMomentKind kind, string language, string expected)
    {
        NotableLines.Text(Moment(kind), language).Should().Be(expected);
    }

    [TestCase("es-MX", "es")]
    [TestCase("ES", "es")]
    [TestCase("fr-CA", "fr")]
    public void A_regional_or_upper_case_tag_uses_its_base_language(string tag, string language)
    {
        NotableLines.Text(Moment(NotableMomentKind.Corner), tag).Should().Be(NotableLines.Text(Moment(NotableMomentKind.Corner), language));
    }

    [TestCase("de")]
    [TestCase("zz-ZZ")]
    [TestCase("")]
    public void An_unknown_language_falls_back_to_english(string tag)
    {
        NotableLines.Text(Moment(NotableMomentKind.YellowCard), tag).Should().Be(NotableLines.Text(Moment(NotableMomentKind.YellowCard), "en"));
    }

    [TestCase(NotableMomentKind.YellowCard, "en", "A player goes into the book.")]
    [TestCase(NotableMomentKind.YellowCard, "es", "Un jugador ve la tarjeta amarilla.")]
    [TestCase(NotableMomentKind.YellowCard, "fr", "Un joueur prend un carton jaune.")]
    [TestCase(NotableMomentKind.Corner, "es", "Saque de esquina. El último en tocarlo fue un jugador.")]
    [TestCase(NotableMomentKind.Corner, "fr", "Corner. Dernier contact d'un joueur.")]
    [TestCase(NotableMomentKind.GoalKick, "es-MX", "Saque de puerta. Un jugador la mandó fuera.")]
    public void A_player_no_squad_has_is_a_localised_a_player_with_no_bracketed_club(NotableMomentKind kind, string language, string expected)
    {
        var unknown = Moment(kind) with { PlayerName = null, PlayerTeam = null };

        NotableLines.Text(unknown, language).Should().Be(expected);
    }

    [Test]
    public void Every_kind_has_an_anonymous_line_in_every_language_with_no_bracket_or_id()
    {
        foreach (var kind in Enum.GetValues<NotableMomentKind>())
        {
            foreach (var language in NotableLines.Languages)
            {
                NotableLines.Text(Moment(kind, "zz-99") with { PlayerName = null, PlayerTeam = null }, language)
                    .Should().NotContain("(").And.NotContain("zz-99").And.NotContain("{");
            }
        }
    }

    [Test]
    public void Every_kind_has_a_line_in_every_language_that_names_the_player_and_club()
    {
        foreach (var kind in Enum.GetValues<NotableMomentKind>())
        {
            foreach (var language in NotableLines.Languages)
            {
                NotableLines.Text(Moment(kind), language).Should().Contain("Reggie Ashworth").And.Contain("(NBK)");
            }
        }
    }

    [Test]
    public void A_moment_gets_a_template_line_for_each_language_and_mode_and_none_are_stored_models()
    {
        var lines = NotableLines.For(Moment(NotableMomentKind.YellowCard), "demo-2");

        lines.Should().HaveCount(6);
        lines.Select(l => (l.Language, l.Mode)).Should().BeEquivalentTo(
            NotableLines.Languages.SelectMany(language => Enum.GetValues<ViewingMode>().Select(mode => (language, mode))));
        lines.Should().OnlyContain(l => l.MatchId == "demo-2" && l.MomentId == "demo-2:5" && l.Sequence == 5
            && l.Tier == MomentTier.Notable && l.Source == CommentarySource.Template);
    }

    [Test]
    public void Over_the_seeded_match_every_line_names_the_player_from_the_roster_and_nobody_else()
    {
        var roster = TeamProfileCatalog.LoadEmbedded().Values.SelectMany(t => t.Squad).ToDictionary(p => p.Id, p => p.Name);
        var state = MatchState.KickOff(DemoRecording.Events[0].MatchId);
        var checkedLines = 0;
        foreach (var matchEvent in DemoRecording.Events)
        {
            if (_detector.Detect(matchEvent, state) is { } moment)
            {
                foreach (var line in NotableLines.For(moment, moment.Trigger.MatchId))
                {
                    // A substitution names the player coming on too (ADR-064), and nobody else.
                    string[] named = matchEvent.ReplacementPlayerId is { } on
                        ? [roster[matchEvent.PlayerId!], roster[on]]
                        : [roster[matchEvent.PlayerId!]];
                    line.Text.Should().ContainAll(named);
                    roster.Values.Where(name => line.Text.Contains(name, StringComparison.Ordinal)).Should().BeEquivalentTo(named);
                    checkedLines++;
                }
            }

            state = state.Apply(matchEvent);
        }

        checkedLines.Should().Be(74 * 6, "every notable moment, minor ones included, has a line in each language and mode");
    }

    private static readonly NotableMomentKind[] _varied = [NotableMomentKind.Corner, NotableMomentKind.ThrowIn, NotableMomentKind.GoalKick];

    [Test]
    public void Every_language_has_as_many_variants_of_each_kind_as_english_and_the_restarts_have_three()
    {
        foreach (var kind in Enum.GetValues<NotableMomentKind>())
        {
            var english = NotableLines.VariantCount(kind, "en");
            english.Should().Be(_varied.Contains(kind) ? 3 : 1, kind.ToString());
            foreach (var language in NotableLines.Languages)
            {
                NotableLines.VariantCount(kind, language).Should().Be(english, $"{kind} in {language}");
            }
        }
    }

    [Test]
    public void Consecutive_moments_of_a_kind_never_read_the_same_in_any_language()
    {
        foreach (var kind in _varied)
        {
            foreach (var language in NotableLines.Languages)
            {
                var count = NotableLines.VariantCount(kind, language);
                for (var ordinal = 0; ordinal < count * 3; ordinal++)
                {
                    var now = NotableLines.Text(Moment(kind) with { Ordinal = ordinal }, language);
                    var next = NotableLines.Text(Moment(kind) with { Ordinal = ordinal + 1 }, language);
                    now.Should().NotBe(next, $"{kind} {language} ordinal {ordinal}");
                }
            }
        }
    }

    [Test]
    public void The_pick_is_deterministic_ordinal_zero_is_todays_line_and_the_variants_cycle()
    {
        NotableLines.Text(Moment(NotableMomentKind.Corner), "en").Should().Be("Corner. It came off Reggie Ashworth (NBK).");
        var second = NotableLines.Text(Moment(NotableMomentKind.Corner) with { Ordinal = 1 }, "en");
        NotableLines.Text(Moment(NotableMomentKind.Corner) with { Ordinal = 1 }, "en").Should().Be(second);
        NotableLines.Text(Moment(NotableMomentKind.Corner) with { Ordinal = 4 }, "en").Should().Be(second, "three variants cycle");
        NotableLines.Text(Moment(NotableMomentKind.Corner) with { Ordinal = 3 }, "en").Should().Be(NotableLines.Text(Moment(NotableMomentKind.Corner), "en"));
    }

    [Test]
    public void Every_variant_fills_both_placeholders_and_leaves_no_brace()
    {
        foreach (var kind in _varied)
        {
            foreach (var language in NotableLines.Languages)
            {
                for (var ordinal = 0; ordinal < NotableLines.VariantCount(kind, language); ordinal++)
                {
                    var text = NotableLines.Text(Moment(kind) with { Ordinal = ordinal }, language);
                    text.Should().Contain("Reggie Ashworth").And.Contain("NBK").And.NotContain("{").And.NotContain("}");
                    NotableLines.Text(Moment(kind) with { Ordinal = ordinal, PlayerName = null }, language).Should().NotContain("{");
                }
            }
        }
    }

    [Test]
    public void Ordinals_count_per_kind_in_the_order_the_moments_arrive()
    {
        var ordinals = new NotableOrdinals();

        ordinals.Number(Moment(NotableMomentKind.Corner)).Ordinal.Should().Be(0);
        ordinals.Number(Moment(NotableMomentKind.ThrowIn)).Ordinal.Should().Be(0);
        ordinals.Number(Moment(NotableMomentKind.Corner)).Ordinal.Should().Be(1);
        ordinals.Number(Moment(NotableMomentKind.Corner)).Ordinal.Should().Be(2);
        ordinals.Number(Moment(NotableMomentKind.ThrowIn)).Ordinal.Should().Be(1);
    }

    [Test]
    public void A_null_moment_is_refused()
    {
        ((Action)(() => NotableLines.Text(null!, "en"))).Should().Throw<ArgumentNullException>();
        ((Action)(() => NotableLines.For(null!, "demo-1"))).Should().Throw<ArgumentNullException>();
    }
}
