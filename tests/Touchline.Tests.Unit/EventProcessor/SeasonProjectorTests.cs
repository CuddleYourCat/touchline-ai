extern alias processor;
extern alias simulator;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using processor::Touchline.EventProcessor;
using processor::Touchline.EventProcessor.Processing;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>The season projection (ADR-025): finished fixtures into player and season documents.</summary>
[TestFixture]
public class SeasonProjectorTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private ServiceProvider _services = null!;
    private InMemoryMatchStore _matches = null!;
    private InMemorySeasonStore _season = null!;
    private SeasonProjector _projector = null!;

    [SetUp]
    public void SetUp()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _matches = new InMemoryMatchStore();
        _season = new InMemorySeasonStore();
        _projector = new SeasonProjector(_matches, _season, new PipelineTelemetry(_services.GetRequiredService<IMeterFactory>()), NullLogger<SeasonProjector>.Instance);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task A_finished_fixture_becomes_one_document_per_starter_and_their_seasons()
    {
        var record = Stored(Match(MatchKind.Fixture, seed: 42, round: 1));

        await _projector.ProjectAsync([record], CancellationToken.None);

        _season.PlayerMatches.Should().HaveCount(22);
        _season.PlayerSeasons.Should().HaveCount(22).And.OnlyContain(s => s.Value.Appearances == 1 && s.Value.Season == "2026-27");
    }

    [Test]
    public async Task Exhibitions_and_unfinished_fixtures_are_ignored()
    {
        var exhibition = Stored(Match(MatchKind.Exhibition, seed: 42));
        var fixture = Stored(Match(MatchKind.Fixture, seed: 43, round: 1));
        var inPlay = fixture with { State = fixture.State with { FullTime = false } };

        await _projector.ProjectAsync([exhibition, inPlay], CancellationToken.None);

        _season.PlayerMatches.Should().BeEmpty("the change feed delivers every batch's update, and only full time counts");
    }

    [Test]
    public async Task Projecting_the_same_fixture_again_changes_nothing()
    {
        var record = Stored(Match(MatchKind.Fixture, seed: 42, round: 1));
        await _projector.ProjectAsync([record], CancellationToken.None);
        var first = _season.PlayerSeasons.Values.ToList();

        await _projector.ProjectAsync([record], CancellationToken.None);

        _season.PlayerMatches.Should().HaveCount(22);
        _season.PlayerSeasons.Values.Should().BeEquivalentTo(first, "a season is rebuilt, never incremented, so redelivery can't double-count");
    }

    [Test]
    public async Task Each_round_rebuilds_the_season_from_every_fixture_so_far()
    {
        await _projector.ProjectAsync([Stored(Match(MatchKind.Fixture, seed: 42, round: 1))], CancellationToken.None);
        await _projector.ProjectAsync([Stored(Match(MatchKind.Fixture, seed: 43, round: 2))], CancellationToken.None);

        var goals = _season.PlayerMatches.Values.Sum(m => m.Goals);
        _season.PlayerMatches.Should().HaveCount(44);
        _season.PlayerSeasons.Values.Sum(s => s.Goals).Should().Be(goals);
        _season.PlayerSeasons.Values.Should().Contain(s => s.Appearances == 2);
    }

    [Test]
    public async Task The_change_feed_adapter_reads_the_documents_as_stored()
    {
        var record = Stored(Match(MatchKind.Fixture, seed: 42, round: 1));
        var documents = JsonSerializer.Serialize(new[] { record }, DomainJson.Options);

        await new ProjectSeason(_projector).RunAsync(documents, CancellationToken.None);

        _season.PlayerMatches.Should().HaveCount(22, "the trigger's JSON is read with DomainJson, like every stored document");
    }

    private static List<MatchEvent> Match(MatchKind kind, int seed, int? round = null) =>
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], seed,
            new MatchOptions { Kind = kind, Season = round is null ? null : "2026-27", Round = round }).Play()];

    // Stores the events and returns the finished match document, as the processor leaves it.
    private MatchRecord Stored(List<MatchEvent> events)
    {
        var matchId = events[0].MatchId;
        _matches.Events[matchId] = new SortedDictionary<long, MatchEvent>(events.ToDictionary(e => e.Sequence));
        var state = events.Aggregate(MatchState.KickOff(matchId), (s, e) => s.Apply(e));
        return new MatchRecord(matchId, matchId, events[0].Metadata, state);
    }
}
