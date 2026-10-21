extern alias processor;
extern alias simulator;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using processor::Touchline.EventProcessor.Processing;
using simulator::Touchline.Simulator.Engine;
using simulator::Touchline.Simulator.Publishing;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Season;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>ADR-025's Cosmos DB adapters, mocked at the SDK boundary like the match store's.</summary>
[TestFixture]
public class SeasonAdapterTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    [Test]
    public async Task A_match_s_events_are_read_from_its_partition_across_pages_in_sequence_order()
    {
        var events = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).Play().Take(6).ToList();
        QueryRequestOptions? asked = null;
        var container = new Mock<Container>();
        container.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback((QueryDefinition _, string _, QueryRequestOptions options) => asked = options)
            // Two pages, out of order, each document carrying an id and Cosmos DB's system properties.
            .Returns(Pages(Stored(events[3..]), Stored(events[..3])));
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosMatchStore.DatabaseName, CosmosMatchStore.EventsContainerName)).Returns(container.Object);

        var read = await new CosmosMatchStore(client.Object).GetEventsAsync(events[0].MatchId, CancellationToken.None);

        read.Should().BeEquivalentTo(events, options => options.WithStrictOrdering());
        asked!.PartitionKey.Should().Be(new PartitionKey(events[0].MatchId), "one match is one partition, so the query never fans out");
    }

    [Test]
    public async Task Player_documents_go_to_the_player_s_partition_and_seasons_to_the_season_s()
    {
        var written = new List<(string Container, JsonObject Document, PartitionKey Partition)>();
        var client = new Mock<CosmosClient>();
        foreach (var name in new[] { CosmosSeasonStore.PlayerMatchesContainerName, CosmosSeasonStore.PlayerSeasonsContainerName })
        {
            var container = new Mock<Container>();
            container.Setup(c => c.UpsertItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Stream content, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
                {
                    lock (written)
                    {
                        written.Add((name, JsonNode.Parse(content)!.AsObject(), partition));
                    }

                    return new ResponseMessage(HttpStatusCode.OK);
                });
            client.Setup(c => c.GetContainer(CosmosMatchStore.DatabaseName, name)).Returns(container.Object);
        }

        var store = new CosmosSeasonStore(client.Object);
        var played = new PlayerMatch(PlayerMatch.IdFor("m1", "nbfc-04"), "nbfc-04", "m1", "north-bank-fc", "2026-27", 1, 1, 0, 2, 1, 0.4, 30, 26, 1, 1, 0, [], []);
        await store.UpsertPlayerMatchesAsync([played], CancellationToken.None);
        await store.UpsertPlayerSeasonAsync(PlayerSeason.Rebuild([played]), CancellationToken.None);

        written.Should().HaveCount(2);
        written[0].Container.Should().Be("player-matches");
        written[0].Partition.Should().Be(new PartitionKey("nbfc-04"));
        written[0].Document["playerId"]!.GetValue<string>().Should().Be("nbfc-04", "the partition key path is /playerId");
        written[1].Container.Should().Be("player-seasons");
        written[1].Partition.Should().Be(new PartitionKey("2026-27"));
        written[1].Document["season"]!.GetValue<string>().Should().Be("2026-27", "the partition key path is /season");
        written[1].Document["bannedRounds"].Should().NotBeNull("the simulator's ban query reads this array");
    }

    [Test]
    public async Task The_simulator_reads_one_season_partition_into_rounds_and_bans()
    {
        QueryRequestOptions? options = null;
        // The projected documents as Cosmos DB returns the query's three fields: camelCase JSON.
        JsonArray standings =
        [
            new JsonObject { ["playerId"] = "nbfc-04", ["rounds"] = new JsonArray(1, 2, 3), ["bannedRounds"] = new JsonArray(4) },
            new JsonObject { ["playerId"] = "tvu-07", ["rounds"] = new JsonArray(1, 2), ["bannedRounds"] = new JsonArray() }
        ];
        var container = new Mock<Container>();
        container.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback((QueryDefinition _, string _, QueryRequestOptions o) => options = o)
            .Returns(Pages(standings));
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosSeasonReader.DatabaseName, CosmosSeasonReader.PlayerSeasonsContainerName)).Returns(container.Object);

        var status = await new CosmosSeasonReader(client.Object).ReadAsync("2026-27", CancellationToken.None);

        options!.PartitionKey.Should().Be(new PartitionKey("2026-27"), "one season is one partition");
        status.LatestRound.Should().Be(3);
        status.HasPlayed(3).Should().BeTrue();
        status.HasPlayed(4).Should().BeFalse();
        status.BannedFor(4).Should().BeEquivalentTo(["nbfc-04"]);
    }

    [Test]
    public void The_names_both_services_use_are_the_shared_ones()
    {
        CosmosSeasonReader.DatabaseName.Should().Be(CosmosMatchStore.DatabaseName);
        CosmosSeasonReader.PlayerSeasonsContainerName.Should().Be(CosmosSeasonStore.PlayerSeasonsContainerName,
            "the simulator reads exactly the container the projection writes");
    }

    // The documents as Cosmos DB returns them: the stored JSON, its id, and system properties.
    private static JsonArray Stored(IEnumerable<MatchEvent> events) =>
        [.. events.Select(e =>
        {
            var document = JsonSerializer.SerializeToNode(e, DomainJson.Options)!.AsObject();
            document["id"] = e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            document["_rid"] = "abc==";
            document["_ts"] = 1792580000;
            return (JsonNode)document;
        })];

    private static FeedIterator Pages(params JsonArray[] pages)
    {
        var iterator = new Mock<FeedIterator>();
        var next = 0;
        iterator.Setup(i => i.HasMoreResults).Returns(() => next < pages.Length);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var body = new JsonObject { ["Documents"] = pages[next++].DeepClone(), ["_count"] = 3 };
            return new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body)) };
        });
        return iterator.Object;
    }
}
