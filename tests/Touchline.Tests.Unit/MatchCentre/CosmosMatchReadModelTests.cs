extern alias simulator;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using simulator::Touchline.Simulator.Engine;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The Cosmos DB read model: what it reads, mocked at the SDK boundary.</summary>
[TestFixture]
public class CosmosMatchReadModelTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private static readonly List<MatchEvent> _match =
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).Play()];

    private Mock<Container> _matches = null!;
    private Mock<Container> _events = null!;
    private Mock<ICommentaryStore> _commentary = null!;
    private CosmosMatchReadModel _readModel = null!;

    [SetUp]
    public void SetUp()
    {
        _matches = new Mock<Container>();
        _events = new Mock<Container>();
        _matches.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ResponseMessage(HttpStatusCode.NotFound));
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Matches)).Returns(_matches.Object);
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Events)).Returns(_events.Object);
        _commentary = new Mock<ICommentaryStore>();
        _commentary.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _readModel = new CosmosMatchReadModel(client.Object, _teams, new KeyMomentDetector(), _commentary.Object, NullLogger<CosmosMatchReadModel>.Instance);
    }

    [Test]
    public async Task Moments_carry_the_viewers_commentary_read_fresh_on_every_call()
    {
        var stored = Stored(_match.Count);
        Store(stored);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages(_match));
        var first = (await _readModel.GetMomentsAsync(stored.MatchId, Viewer.Default, CancellationToken.None))!.First(m => m.Tier == MomentTier.Key);
        first.Commentary.Should().BeNull("nothing is written yet");
        var line = new CommentaryLine(stored.MatchId, first.Id, first.Sequence, MomentTier.Key, CommentarySource.Model, "What a strike.", ViewingMode.Casual, "en");
        _commentary.Setup(s => s.GetAsync(stored.MatchId, It.IsAny<CancellationToken>())).ReturnsAsync([line]);

        var moments = await _readModel.GetMomentsAsync(stored.MatchId, Viewer.Default, CancellationToken.None);

        moments!.Single(m => m.Id == first.Id).Commentary.Should().Be(line);
        moments!.Where(m => m.Tier == MomentTier.Key && m.Id != first.Id).Should().OnlyContain(m => m.Commentary == null);
        _commentary.Verify(s => s.GetAsync(stored.MatchId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Moments_still_come_back_without_commentary_when_the_store_is_down()
    {
        var stored = Stored(_match.Count);
        Store(stored);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages(_match));
        _commentary.Setup(s => s.GetAsync(stored.MatchId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Service unavailable", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));

        var moments = await _readModel.GetMomentsAsync(stored.MatchId, Viewer.Default, CancellationToken.None);

        moments.Should().NotBeNullOrEmpty();
        moments!.Where(m => m.Tier == MomentTier.Key).Should().OnlyContain(m => m.Commentary == null);
        var notable = moments!.Where(m => m.Tier == MomentTier.Notable).ToList();
        notable.Should().HaveCount(19, "template lines are worked out from the events, so the store being down doesn't touch them");
        notable.Should().OnlyContain(m => m.Commentary!.Source == CommentarySource.Template && m.Commentary.Language == "en");
    }

    [Test]
    public async Task A_notable_moment_is_read_in_the_viewers_language_and_falls_back_to_english()
    {
        var stored = Stored(_match.Count);
        Store(stored);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages(_match));
        var (spanish, _) = Viewer.Create(ViewingMode.Casual, "es-MX", null, null, null, _ => true, _ => true);
        var (german, _) = Viewer.Create(ViewingMode.Casual, "de", null, null, null, _ => true, _ => true);

        var inSpanish = await _readModel.GetMomentsAsync(stored.MatchId, spanish!, CancellationToken.None);
        var inGerman = await _readModel.GetMomentsAsync(stored.MatchId, german!, CancellationToken.None);

        inSpanish!.Where(m => m.Tier == MomentTier.Notable).Should().OnlyContain(m => m.Commentary!.Language == "es");
        inGerman!.Where(m => m.Tier == MomentTier.Notable).Should().OnlyContain(m => m.Commentary!.Language == "en");
    }

    [Test]
    public async Task A_match_with_nothing_stored_reads_as_null_everywhere()
    {
        (await _readModel.GetMatchAsync("unknown", CancellationToken.None)).Should().BeNull();
        (await _readModel.GetEventsAsync("unknown", 0, 10, CancellationToken.None)).Should().BeNull();
        (await _readModel.GetMomentumAsync("unknown", CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task A_stored_match_reads_with_its_line_ups_named_from_the_team_profiles()
    {
        var stored = Stored(_match.Count);
        Store(stored);

        var detail = await _readModel.GetMatchAsync(stored.MatchId, CancellationToken.None);

        detail!.Summary.Status.Should().Be(MatchStatus.FullTime);
        detail.Summary.DataSource.Should().Be("synthetic");
        detail.HomeLineup.Should().HaveCount(11);
        detail.HomeLineup[0].Name.Should().Be(_teams[stored.Metadata!.Home.Id].FindPlayer(stored.Metadata.Home.StartingEleven[0])!.Name);
    }

    [Test]
    public async Task A_page_of_events_starts_after_the_given_sequence_and_stops_at_the_limit()
    {
        var stored = Stored(_match.Count);
        Store(stored);
        QueryDefinition? asked = null;
        QueryRequestOptions? options = null;
        var replies = new Queue<IEnumerable<MatchEvent>>([_match.Skip(10).Take(6), _match.Skip(37).Take(3)]);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions requestOptions) =>
            {
                asked = query;
                options = requestOptions;
                return Pages(replies.Dequeue());
            });
        var after = _match[9].Sequence;

        var page = await _readModel.GetEventsAsync(stored.MatchId, after, 5, CancellationToken.None);

        asked!.QueryText.Should().Be("SELECT TOP @take * FROM c WHERE c.sequence > @after ORDER BY c.sequence");
        asked.GetQueryParameters().Should().Equal(("@take", (object)6), ("@after", (object)after));
        options!.PartitionKey.Should().Be(new PartitionKey(stored.MatchId), "a page is a single-partition query");
        page!.Events.Select(e => e.Sequence).Should().Equal(_match.Skip(10).Take(5).Select(e => e.Sequence));
        page.NextAfter.Should().Be(_match[14].Sequence, "the extra event shows more follow the page");

        var last = await _readModel.GetEventsAsync(stored.MatchId, _match[36].Sequence, 5, CancellationToken.None);

        last!.Events.Should().HaveCount(3);
        last.NextAfter.Should().BeNull("there are no more events yet");
    }

    [Test]
    public async Task Moments_replay_a_match_whose_stored_events_have_gaps()
    {
        var stored = Stored(_match.Count);
        Store(stored);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages(_match.Where(e => e.Sequence is not (5 or 6))));

        var read = async () => await _readModel.GetMomentsAsync(stored.MatchId, Viewer.Default, CancellationToken.None);

        (await read.Should().NotThrowAsync()).Subject.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task A_finished_match_reads_its_events_from_Cosmos_DB_once()
    {
        var stored = Stored(_match.Count);
        Store(stored);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages(_match));

        var moments = await _readModel.GetMomentsAsync(stored.MatchId, Viewer.Default, CancellationToken.None);
        var momentum = await _readModel.GetMomentumAsync(stored.MatchId, CancellationToken.None);

        moments.Should().NotBeNull();
        momentum.Should().NotBeNull();
        _events.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once());
        _matches.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Test]
    public async Task A_live_match_reads_its_events_from_Cosmos_DB_every_time()
    {
        var stored = Stored(200);
        Store(stored);
        _events.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages(_match.Take(200)));

        await _readModel.GetMomentumAsync(stored.MatchId, CancellationToken.None);
        await _readModel.GetMomentumAsync(stored.MatchId, CancellationToken.None);

        _events.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Exactly(2));
    }

    [TestCase("a/b")]
    [TestCase("a\\b")]
    [TestCase("a?b")]
    [TestCase("a#b")]
    public async Task An_ID_Cosmos_DB_forbids_reads_as_no_such_match_without_calling_it(string matchId)
    {
        (await _readModel.GetMatchAsync(matchId, CancellationToken.None)).Should().BeNull();
        (await _readModel.GetEventsAsync(matchId, 0, 10, CancellationToken.None)).Should().BeNull();
        (await _readModel.GetMomentsAsync(matchId, Viewer.Default, CancellationToken.None)).Should().BeNull();
        (await _readModel.GetMomentumAsync(matchId, CancellationToken.None)).Should().BeNull();
        (await _readModel.GetPlayerSeasonAsync("2026-27", matchId, CancellationToken.None)).Should().BeNull();
        _matches.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Test]
    public async Task An_ID_longer_than_Cosmos_DB_allows_reads_as_no_such_match()
    {
        (await _readModel.GetMatchAsync(new string('m', 256), CancellationToken.None)).Should().BeNull();
        _matches.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Test]
    public async Task The_list_maps_status_from_full_time()
    {
        var finished = Stored(_match.Count);
        var live = Stored(200) with { Id = "live-match", MatchId = "live-match" };
        _matches.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(() => Pages([finished, live]));

        var list = (await _readModel.ListMatchesAsync(null, null, CancellationToken.None)).Matches;

        list.Should().HaveCount(2);
        list.Single(m => m.Id == finished.MatchId).Status.Should().Be(MatchStatus.FullTime);
        list.Single(m => m.Id == "live-match").Status.Should().Be(MatchStatus.Live);
        (await _readModel.ListMatchesAsync(MatchStatus.Scheduled, null, CancellationToken.None)).Matches.Should().BeEmpty("a stored match has kicked off");
    }

    private static CosmosMatchReadModel.StoredMatch Stored(int played) =>
        new(_match[0].MatchId, _match[0].MatchId, _match[0].Metadata,
            _match.Take(played).Aggregate(MatchState.KickOff(_match[0].MatchId), (state, e) => state.Apply(e)));

    private void Store(CosmosMatchReadModel.StoredMatch stored) =>
        _matches.Setup(c => c.ReadItemStreamAsync(stored.MatchId, new PartitionKey(stored.MatchId), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(stored, DomainJson.Options)) });

    private static FeedIterator Pages<T>(IEnumerable<T> documents)
    {
        var page = new JsonObject { ["Documents"] = JsonSerializer.SerializeToNode(documents.ToList(), DomainJson.Options) };
        var iterator = new Mock<FeedIterator>();
        iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(page, DomainJson.Options)) });
        return iterator.Object;
    }
}
