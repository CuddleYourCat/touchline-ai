using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Viewing;
using Touchline.Storage;

namespace Touchline.Tests.Unit.Storage;

/// <summary>The Cosmos DB recap store (ADR-030, amended 23 Oct), mocked at the SDK boundary.</summary>
[TestFixture]
public class CosmosRecapStoreTests
{
    private const string _matchId = "m-7";

    private readonly Dictionary<(string Id, PartitionKey Partition), JsonObject> _documents = [];
    private CosmosRecapStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _documents.Clear();
        var container = new Mock<Container>();
        container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
            {
                var document = JsonNode.Parse(content)!.AsObject();
                return _documents.TryAdd((document["id"]!.GetValue<string>(), partition), document)
                    ? new ResponseMessage(HttpStatusCode.Created)
                    : new ResponseMessage(HttpStatusCode.Conflict);
            });
        container.Setup(c => c.UpsertItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
            {
                var document = JsonNode.Parse(content)!.AsObject();
                _documents[(document["id"]!.GetValue<string>(), partition)] = document;
                return new ResponseMessage(HttpStatusCode.OK);
            });
        container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
                _documents.TryGetValue((id, partition), out var document)
                    ? new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document)) }
                    : new ResponseMessage(HttpStatusCode.NotFound));
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Recaps)).Returns(container.Object);
        _store = new CosmosRecapStore(client.Object);
    }

    [Test]
    public async Task A_recap_is_stored_by_match_mode_and_language_and_read_back()
    {
        var recap = new MatchRecap(_matchId, "Headline", "Text.", ViewingMode.Analyst, "es", CommentarySource.Model);

        var saved = await _store.SaveAsync(recap, CancellationToken.None);
        var read = await _store.GetAsync(_matchId, ViewingMode.Analyst, "ES", CancellationToken.None);

        saved.Should().BeTrue();
        _documents.Keys.Should().Equal(("m-7:Analyst:es", new PartitionKey(_matchId)));
        read.Should().Be(recap);
    }

    [Test]
    public async Task A_second_save_keeps_the_recap_already_stored()
    {
        await _store.SaveAsync(new MatchRecap(_matchId, "First", "Text.", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);

        var saved = await _store.SaveAsync(new MatchRecap(_matchId, "Second", "Text.", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);

        saved.Should().BeFalse();
        (await _store.GetAsync(_matchId, ViewingMode.Casual, "en", CancellationToken.None))!.Headline.Should().Be("First");
    }

    [Test]
    public async Task An_attempt_marker_is_created_replaced_and_read_back_beside_the_recap()
    {
        var before = await _store.GetAttemptsAsync(_matchId, ViewingMode.Casual, "ES", CancellationToken.None);
        await _store.SaveAttemptsAsync(_matchId, ViewingMode.Casual, "es", 1, CancellationToken.None);
        await _store.SaveAttemptsAsync(_matchId, ViewingMode.Casual, "es", 2, CancellationToken.None);

        before.Should().Be(0);
        (await _store.GetAttemptsAsync(_matchId, ViewingMode.Casual, "ES", CancellationToken.None)).Should().Be(2);
        _documents.Keys.Should().Equal(("m-7:Casual:es:attempts", new PartitionKey(_matchId)));
    }

    [Test]
    public async Task An_attempt_marker_is_never_read_as_a_recap()
    {
        await _store.SaveAttemptsAsync(_matchId, ViewingMode.Casual, "es", 1, CancellationToken.None);

        (await _store.GetAsync(_matchId, ViewingMode.Casual, "es", CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync(_matchId, ViewingMode.Casual, "es:attempts", CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task An_empty_slot_reads_as_null()
    {
        (await _store.GetAsync(_matchId, ViewingMode.Casual, "fr", CancellationToken.None)).Should().BeNull();
    }
}
