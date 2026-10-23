using FluentAssertions;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>Which line of a moment's commentary a viewer sees.</summary>
[TestFixture]
public class CommentarySelectionTests
{
    private static readonly CommentaryLine _casualEsMx = Line(ViewingMode.Casual, "es-MX");
    private static readonly CommentaryLine _casualEs = Line(ViewingMode.Casual, "es");
    private static readonly CommentaryLine _casualEn = Line(ViewingMode.Casual, "en");
    private static readonly CommentaryLine _analystEn = Line(ViewingMode.Analyst, "en");
    private static readonly CommentaryLine _analystFr = Line(ViewingMode.Analyst, "fr");

    [Test]
    public void The_viewers_mode_in_their_exact_language_comes_first() =>
        CommentarySelection.Pick([_casualEn, _casualEs, _casualEsMx], ViewingMode.Casual, "es-MX").Should().Be(_casualEsMx);

    [Test]
    public void Then_the_same_mode_in_the_base_language() =>
        CommentarySelection.Pick([_casualEn, _casualEs], ViewingMode.Casual, "es-MX").Should().Be(_casualEs);

    [Test]
    public void Then_the_same_mode_in_English() =>
        CommentarySelection.Pick([_analystEn, _casualEn, _analystFr], ViewingMode.Casual, "es-MX").Should().Be(_casualEn);

    [Test]
    public void Then_any_mode_in_English() =>
        CommentarySelection.Pick([_analystFr, _analystEn], ViewingMode.Casual, "es-MX").Should().Be(_analystEn);

    [Test]
    public void A_line_in_another_mode_keeps_its_own_mode_so_the_UI_can_label_it() =>
        CommentarySelection.Pick([_analystEn], ViewingMode.Casual, "en")!.Mode.Should().Be(ViewingMode.Analyst);

    [Test]
    public void Otherwise_nothing() =>
        CommentarySelection.Pick([_analystFr], ViewingMode.Casual, "es-MX").Should().BeNull();

    [Test]
    public void No_lines_is_nothing() =>
        CommentarySelection.Pick([], ViewingMode.Analyst, "en").Should().BeNull();

    [Test]
    public void A_model_line_beats_a_template_line_for_the_same_key()
    {
        var model = _casualEn with { Source = CommentarySource.Model, Text = "model" };

        CommentarySelection.Pick([_casualEn, model], ViewingMode.Casual, "en").Should().Be(model);
        CommentarySelection.Pick([model, _casualEn], ViewingMode.Casual, "en").Should().Be(model);
    }

    [Test]
    public void A_template_in_a_better_step_beats_a_model_line_in_a_worse_one() =>
        CommentarySelection.Pick([_casualEn with { Source = CommentarySource.Model }, _casualEs], ViewingMode.Casual, "es").Should().Be(_casualEs);

    private static CommentaryLine Line(ViewingMode mode, string language) =>
        new("demo-3", "demo-3:412", 412, MomentTier.Key, CommentarySource.Template, $"{mode} {language}", mode, language);
}
