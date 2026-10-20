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
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>The Cosmos DB and Service Bus adapters: what they write, mocked at the SDK boundary.</summary>
[TestFixture]
public class AdapterTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private static List<MatchEvent> Match() =>
        [.. new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).Play()];

    [Test]
    public void A_key_moment_message_is_identified_by_its_match_and_sequence()
    {
        var goal = Match().First(e => e.IsGoal);
        var moment = new KeyMoment(KeyMomentKind.Goal, goal, "GOAL!");

        var message = ServiceBusKeyMomentPublisher.ToMessage(moment);

        message.MessageId.Should().Be($"{goal.MatchId}:{goal.Sequence}", "the same moment published twice can be recognised as one");
        message.Subject.Should().Be("Goal");
        message.ApplicationProperties["matchId"].Should().Be(goal.MatchId);
        JsonSerializer.Deserialize<KeyMoment>(message.Body.ToArray(), DomainJson.Options)!.Trigger.Should().BeEquivalentTo(goal);
    }

    [Test]
    public async Task An_event_is_stored_as_its_own_json_with_its_sequence_as_the_document_id()
    {
        var written = new List<(JsonObject Document, PartitionKey Partition)>();
        var events = new Mock<Container>();
        events.Setup(c => c.UpsertItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey partition, ItemRequestOptions _, CancellationToken _) =>
            {
                written.Add((JsonNode.Parse(content)!.AsObject(), partition));
                return new ResponseMessage(HttpStatusCode.OK);
            });
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosMatchStore.DatabaseName, CosmosMatchStore.EventsContainerName)).Returns(events.Object);
        var pass = Match().First(e => e.Type == MatchEventType.Pass);

        await new CosmosMatchStore(client.Object).UpsertEventsAsync(pass.MatchId, [pass], CancellationToken.None);

        var (document, partition) = written.Single();
        document["id"]!.GetValue<string>().Should().Be(pass.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        document["matchId"]!.GetValue<string>().Should().Be(pass.MatchId, "the partition key path is /matchId");
        document["type"]!.GetValue<string>().Should().Be("Pass", "enums are stored as names, as everywhere in the domain");
        partition.Should().Be(new PartitionKey(pass.MatchId));
        JsonSerializer.Deserialize<MatchEvent>(document.ToJsonString(), DomainJson.Options).Should().BeEquivalentTo(pass);
    }

    [Test]
    public async Task A_match_with_nothing_stored_reads_as_null_and_a_stored_one_round_trips()
    {
        var match = Match();
        var record = MatchRecord.New(match[0].MatchId) with
        {
            Metadata = match[0].Metadata,
            State = match.Take(200).Aggregate(MatchState.KickOff(match[0].MatchId), (s, e) => s.Apply(e))
        };
        var matches = new Mock<Container>();
        matches.Setup(c => c.ReadItemStreamAsync("unknown", It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResponseMessage(HttpStatusCode.NotFound));
        matches.Setup(c => c.ReadItemStreamAsync(record.MatchId, It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, DomainJson.Options)) });
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosMatchStore.DatabaseName, CosmosMatchStore.MatchesContainerName)).Returns(matches.Object);
        var store = new CosmosMatchStore(client.Object);

        (await store.GetMatchAsync("unknown", CancellationToken.None)).Should().BeNull();
        (await store.GetMatchAsync(record.MatchId, CancellationToken.None)).Should().BeEquivalentTo(record);
    }
}
