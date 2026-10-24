using System.Net;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Touchline.Agents;
using Touchline.Domain.Matches;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Squad;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The Match Centre reads recaps and never writes them (ADR-030, amended 23 Oct).</summary>
[TestFixture]
public class RecapReaderTests
{
    private const string _matchId = "m-7";

    private static readonly MatchFactSheet _sheet = new(
        _matchId, "North Bank FC", "Riverside Athletic", 2, 1, "90:00", FullTime: true, TeamStats.Empty, TeamStats.Empty,
        ["Tom Okafor"], ["Sam Hart"], ["North Bank FC", "Riverside Athletic"]);

    private static readonly MomentFact _goal = new("m-7:412", "23:00", "Goal", TeamSide.Home, "Goal: North Bank FC lead 1-0.");

    private Mock<IRecapStore> _store = null!;
    private Mock<ISquad> _squad = null!;
    private Mock<IMatchFacts> _facts = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Mock<IRecapStore>();
        _squad = new Mock<ISquad>(MockBehavior.Strict);
        _facts = new Mock<IMatchFacts>();
        _facts.Setup(f => f.GetSummaryAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync(_sheet);
        _facts.Setup(f => f.GetKeyMomentsAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync([_goal]);
    }

    [Test]
    public async Task A_stored_recap_in_the_viewers_language_is_served()
    {
        var stored = new MatchRecap(_matchId, "Gana North Bank FC", "Texto.", ViewingMode.Analyst, "es", CommentarySource.Model);
        _store.Setup(s => s.GetAsync(_matchId, ViewingMode.Analyst, "es", It.IsAny<CancellationToken>())).ReturnsAsync(stored);

        var recap = await Reader().GetAsync(_matchId, Viewer(ViewingMode.Analyst, "es"), CancellationToken.None);

        recap.Should().Be(stored);
    }

    [Test]
    public async Task A_stored_recap_saved_before_the_source_existed_is_marked_as_the_models()
    {
        var legacy = new MatchRecap(_matchId, "North Bank FC win", "Text.", ViewingMode.Casual, "en", default);
        _store.Setup(s => s.GetAsync(_matchId, ViewingMode.Casual, "en", It.IsAny<CancellationToken>())).ReturnsAsync(legacy);

        var recap = await Reader().GetAsync(_matchId, Viewer(ViewingMode.Casual, "en"), CancellationToken.None);

        recap.Should().Be(legacy with { Source = CommentarySource.Model });
    }

    [Test]
    public async Task A_regional_language_falls_back_to_its_base_language_then_to_English()
    {
        var english = new MatchRecap(_matchId, "North Bank FC win", "Text.", ViewingMode.Casual, "en", CommentarySource.Model);
        _store.Setup(s => s.GetAsync(_matchId, ViewingMode.Casual, "en", It.IsAny<CancellationToken>())).ReturnsAsync(english);

        var recap = await Reader().GetAsync(_matchId, Viewer(ViewingMode.Casual, "pt-BR"), CancellationToken.None);

        recap.Should().Be(english);
        _store.Verify(s => s.GetAsync(_matchId, ViewingMode.Casual, "pt-BR", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.GetAsync(_matchId, ViewingMode.Casual, "pt", It.IsAny<CancellationToken>()), Times.Once);
        RecapReader.Languages("es-MX").Should().Equal("es-MX", "es", "en");
    }

    [Test]
    public async Task Each_candidate_language_is_normalised_so_no_document_is_read_twice()
    {
        await Reader().GetAsync(_matchId, Viewer(ViewingMode.Casual, "es-MX"), CancellationToken.None);
        await Reader().GetAsync(_matchId, Viewer(ViewingMode.Analyst, "en-GB"), CancellationToken.None);

        _store.Verify(s => s.GetAsync(_matchId, ViewingMode.Casual, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _store.Verify(s => s.GetAsync(_matchId, ViewingMode.Analyst, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        RecapReader.Languages("EN-gb").Should().Equal("en-GB", "en");
        RecapReader.Languages("ES-mx").Should().Equal("es-MX", "es", "en");
    }

    [Test]
    public async Task With_nothing_stored_the_template_recap_is_served()
    {
        var viewer = Viewer(ViewingMode.Casual, "fr");

        var recap = await Reader().GetAsync(_matchId, viewer, CancellationToken.None);

        recap.Should().Be(ProducerRecap.Template(_sheet, [_goal], viewer));
        recap.Headline.Should().Be("North Bank FC 2–1 Riverside Athletic");
        recap.Source.Should().Be(CommentarySource.Template);
    }

    [Test]
    public async Task An_unavailable_store_gives_the_template_not_an_error()
    {
        _store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<ViewingMode>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Service unavailable", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));

        var recap = await Reader().GetAsync(_matchId, Viewer(ViewingMode.Casual, "en"), CancellationToken.None);

        recap.Should().Be(ProducerRecap.Template(_sheet, [_goal], Viewer(ViewingMode.Casual, "en")));
    }

    [Test]
    public async Task A_demo_match_keeps_the_squads_template_recap()
    {
        var demo = new MatchRecap("demo-1", "Demo", "The key moments.", ViewingMode.Casual, "en", CommentarySource.Template);
        _squad.Setup(s => s.RecapAsync("demo-1", It.IsAny<Viewer>(), It.IsAny<CancellationToken>())).ReturnsAsync(demo);

        var recap = await Reader().GetAsync("demo-1", Viewer(ViewingMode.Casual, "en"), CancellationToken.None);

        recap.Should().BeSameAs(demo);
        _store.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Without_a_recap_store_every_match_goes_to_the_squad()
    {
        var demo = new MatchRecap(_matchId, "Squad", "Text.", ViewingMode.Casual, "en", CommentarySource.Model);
        _squad.Setup(s => s.RecapAsync(_matchId, It.IsAny<Viewer>(), It.IsAny<CancellationToken>())).ReturnsAsync(demo);

        var reader = new RecapReader(_squad.Object, _facts.Object, NullLogger<RecapReader>.Instance);

        (await reader.GetAsync(_matchId, Viewer(ViewingMode.Casual, "en"), CancellationToken.None)).Should().BeSameAs(demo);
    }

    private RecapReader Reader() => new(_squad.Object, _facts.Object, NullLogger<RecapReader>.Instance, _store.Object);

    private static Viewer Viewer(ViewingMode mode, string language) =>
        Touchline.Domain.Viewing.Viewer.Create(mode, language, null, null, null, _ => true, _ => true).Viewer!;
}
