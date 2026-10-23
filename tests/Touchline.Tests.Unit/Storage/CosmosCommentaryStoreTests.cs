using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using Touchline.Domain;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Viewing;
using Touchline.Storage;

namespace Touchline.Tests.Unit.Storage;

/// <summary>The Cosmos DB commentary store (ADR-038), mocked at the SDK boundary.</summary>
[TestFixture]
public class CosmosCommentaryStoreTests
{
    private const string _matchId = "demo-3";

    private readonly Dictionary<(string Id, PartitionKey Partition), JsonObject> _documents = [];
    private Mock<Container> _container = null!;
    private CosmosCommentaryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _documents.Clear();
        _container = new Mock<Container>();
        _container.Setup(c => c.UpsertItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
            {
                var document = JsonNode.Parse(content)!.AsObject();
                _documents[(document["id"]!.GetValue<string>(), partition)] = document;
                return new ResponseMessage(HttpStatusCode.OK);
            });
        _container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
            {
                var document = JsonNode.Parse(content)!.AsObject();
                var key = (document["id"]!.GetValue<string>(), partition);
                if (_documents.ContainsKey(key))
                {
                    return new ResponseMessage(HttpStatusCode.Conflict);
                }

                _documents[key] = document;
                return new ResponseMessage(HttpStatusCode.Created);
            });
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Commentary)).Returns(_container.Object);
        _store = new CosmosCommentaryStore(client.Object);
    }

    [Test]
    public async Task A_line_is_stored_by_moment_mode_and_language_in_its_match_partition()
    {
        await _store.SaveAsync(Line("Gol!", ViewingMode.Analyst, "es-MX"), CancellationToken.None);

        _documents.Keys.Should().Equal(("demo-3:412:Analyst:es-MX", new PartitionKey(_matchId)));
        var stored = _documents.Values.Single().Deserialize<CommentaryLine>(DomainJson.Options);
        stored.Should().Be(Line("Gol!", ViewingMode.Analyst, "es-MX"));
    }

    [Test]
    public async Task A_model_line_after_a_template_line_replaces_it()
    {
        await _store.SaveAsync(Line("Goal.", ViewingMode.Casual, "en"), CancellationToken.None);
        await _store.SaveAsync(Line("A thunderbolt!", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);

        _documents.Should().ContainSingle();
        _documents.Values.Single()["text"]!.GetValue<string>().Should().Be("A thunderbolt!");
    }

    [Test]
    public async Task A_template_line_after_a_model_line_keeps_the_model_line()
    {
        await _store.SaveAsync(Line("A thunderbolt!", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);
        await _store.SaveAsync(Line("Goal.", ViewingMode.Casual, "en"), CancellationToken.None);

        _documents.Should().ContainSingle();
        _documents.Values.Single()["text"]!.GetValue<string>().Should().Be("A thunderbolt!");
        _documents.Values.Single().Deserialize<CommentaryLine>(DomainJson.Options)!.Source.Should().Be(CommentarySource.Model);
    }

    [Test]
    public async Task A_model_line_after_a_model_line_replaces_it()
    {
        await _store.SaveAsync(Line("Old.", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);
        await _store.SaveAsync(Line("New.", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);

        _documents.Values.Single()["text"]!.GetValue<string>().Should().Be("New.");
    }

    [TestCase("ES", "es")]
    [TestCase("es", "es")]
    [TestCase("es-mx", "es-MX")]
    [TestCase("ES-mx", "es-MX")]
    public async Task The_language_is_normalised_in_the_ID_and_the_stored_line(string given, string normalised)
    {
        await _store.SaveAsync(Line("Gol!", ViewingMode.Casual, given), CancellationToken.None);

        _documents.Keys.Single().Id.Should().Be($"demo-3:412:Casual:{normalised}");
        _documents.Values.Single().Deserialize<CommentaryLine>(DomainJson.Options)!.Language.Should().Be(normalised);
    }

    [Test]
    public async Task Differently_cased_languages_share_one_slot()
    {
        await _store.SaveAsync(Line("Gol.", ViewingMode.Casual, "ES"), CancellationToken.None);
        await _store.SaveAsync(Line("Golazo!", ViewingMode.Casual, "es", CommentarySource.Model), CancellationToken.None);

        _documents.Should().ContainSingle();
    }

    [Test]
    public async Task A_line_whose_moment_ID_is_not_its_match_and_sequence_is_refused()
    {
        var save = () => _store.SaveAsync(Line("Goal.", ViewingMode.Casual, "en") with { Sequence = 413 }, CancellationToken.None);

        await save.Should().ThrowAsync<ArgumentException>();
        _documents.Should().BeEmpty();
    }

    [TestCase("a/b", "en")]
    [TestCase("demo-3", "en?x")]
    public async Task A_line_with_an_ID_Cosmos_DB_forbids_is_refused(string matchId, string language)
    {
        var line = Line("Goal.", ViewingMode.Casual, language) with { MatchId = matchId, MomentId = MomentIds.For(matchId, 412) };
        var save = () => _store.SaveAsync(line, CancellationToken.None);

        await save.Should().ThrowAsync<ArgumentException>();
        _documents.Should().BeEmpty();
    }

    [Test]
    public async Task A_match_is_read_with_one_single_partition_query_in_sequence_order()
    {
        QueryDefinition? asked = null;
        QueryRequestOptions? options = null;
        var line = Line("Goal.", ViewingMode.Casual, "en");
        _container.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions requestOptions) =>
            {
                asked = query;
                options = requestOptions;
                return Pages(line);
            });

        var lines = await _store.GetAsync(_matchId, CancellationToken.None);

        lines.Should().Equal(line);
        asked!.QueryText.Should().Be("SELECT * FROM c ORDER BY c.sequence");
        options!.PartitionKey.Should().Be(new PartitionKey(_matchId));
        _container.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once());
    }

    [Test]
    public async Task A_match_ID_Cosmos_DB_forbids_has_no_commentary_without_calling_it()
    {
        (await _store.GetAsync("a#b", CancellationToken.None)).Should().BeEmpty();
        _container.VerifyNoOtherCalls();
    }

    private static CommentaryLine Line(string text, ViewingMode mode, string language, CommentarySource source = CommentarySource.Template) =>
        new(_matchId, MomentIds.For(_matchId, 412), 412, MomentTier.Key, source, text, mode, language);

    private static FeedIterator Pages(params CommentaryLine[] lines)
    {
        var page = new JsonObject { ["Documents"] = JsonSerializer.SerializeToNode(lines, DomainJson.Options) };
        var iterator = new Mock<FeedIterator>();
        iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(page, DomainJson.Options)) });
        return iterator.Object;
    }
}
