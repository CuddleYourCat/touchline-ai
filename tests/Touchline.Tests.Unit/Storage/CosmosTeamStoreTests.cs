using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Tests.Unit.MatchCentre;

namespace Touchline.Tests.Unit.Storage;

/// <summary>The Cosmos DB team store (ADR-048), mocked at the SDK boundary with an in-memory container that honours ETags.</summary>
[TestFixture]
public class CosmosTeamStoreTests
{
    private readonly Dictionary<string, JsonObject> _documents = [];
    private int _etags;
    private Mock<Container> _container = null!;
    private CosmosTeamStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _documents.Clear();
        _container = new Mock<Container>();
        _container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
                _documents.TryGetValue(id, out var document)
                    ? new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document)) }
                    : new ResponseMessage(HttpStatusCode.NotFound));
        _container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
            {
                var document = JsonNode.Parse(content)!.AsObject();
                var id = document["id"]!.GetValue<string>();
                if (_documents.ContainsKey(id))
                {
                    return new ResponseMessage(HttpStatusCode.Conflict);
                }

                Store(id, document);
                return new ResponseMessage(HttpStatusCode.Created);
            });
        _container.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream content, string id, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                if (!_documents.TryGetValue(id, out var current))
                {
                    return new ResponseMessage(HttpStatusCode.NotFound);
                }

                if (options?.IfMatchEtag is { } expected && expected != current["_etag"]!.GetValue<string>())
                {
                    return new ResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                Store(id, JsonNode.Parse(content)!.AsObject());
                return new ResponseMessage(HttpStatusCode.OK);
            });
        _container.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions _) => Page(Run(query)));

        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.Teams)).Returns(_container.Object);
        _store = new CosmosTeamStore(
            client.Object,
            TeamProfileCatalog.LoadEmbedded(),
            Options.Create(new TeamStoreOptions { DeletedRetention = TimeSpan.FromDays(7) }),
            NullLogger<CosmosTeamStore>.Instance);
    }

    [Test]
    public async Task A_new_team_is_one_document_in_its_own_partition_and_reads_back()
    {
        var team = FanTeam("fan-larkmoor-k3x9qa", "Larkmoor Athletic");

        await _store.AddAsync(team, CancellationToken.None);
        var found = await _store.GetAsync("fan-larkmoor-k3x9qa", CancellationToken.None);

        _documents.Keys.Should().Equal("fan-larkmoor-k3x9qa");
        found.Should().BeEquivalentTo(team);
        _container.Verify(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("fan-larkmoor-k3x9qa"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Adding_the_same_team_twice_is_refused()
    {
        var team = FanTeam("fan-a-aaaaaa", "Aaa");
        await _store.AddAsync(team, CancellationToken.None);

        var again = () => _store.AddAsync(team, CancellationToken.None);

        await again.Should().ThrowAsync<TeamAlreadyExistsException>();
    }

    [Test]
    public async Task A_team_that_is_not_a_fan_id_is_never_written()
    {
        var add = () => _store.AddAsync(FanTeam("north-bank-fc", "Imposter"), CancellationToken.None);

        await add.Should().ThrowAsync<ArgumentException>();
        _documents.Should().BeEmpty();
    }

    [Test]
    public async Task Built_in_teams_come_from_the_catalogue_without_touching_cosmos_db()
    {
        var team = await _store.GetAsync("north-bank-fc", CancellationToken.None);

        team!.Detail.Team.Origin.Should().Be(TeamOrigin.BuiltIn);
        team.EditKeyHash.Should().BeNull();
        _container.VerifyNoOtherCalls();
    }

    [Test]
    public async Task An_unknown_or_malformed_id_is_no_such_team_without_a_round_trip()
    {
        (await _store.GetAsync("nobody", CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync("fan-a/b", CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync("fan-missing-aaaaaa", CancellationToken.None)).Should().BeNull();

        _container.Verify(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Listing_puts_built_in_teams_first_then_fan_teams_by_name()
    {
        await _store.AddAsync(FanTeam("fan-b-bbbbbb", "Zed Town"), CancellationToken.None);
        await _store.AddAsync(FanTeam("fan-a-aaaaaa", "Abel Rovers"), CancellationToken.None);

        var all = await _store.ListAsync(null, CancellationToken.None);
        var fans = await _store.ListAsync(TeamOrigin.Fan, CancellationToken.None);
        var builtIn = await _store.ListAsync(TeamOrigin.BuiltIn, CancellationToken.None);

        all.Select(t => t.Detail.Team.Origin).Should().BeInAscendingOrder();
        all.Skip(builtIn.Count).Select(t => t.Detail.Team.Name).Should().Equal("Abel Rovers", "Zed Town");
        fans.Select(t => t.Detail.Team.Id).Should().Equal("fan-a-aaaaaa", "fan-b-bbbbbb");
        builtIn.Should().OnlyContain(t => t.Detail.Team.Origin == TeamOrigin.BuiltIn);
    }

    [Test]
    public async Task Listing_only_built_in_teams_makes_no_query()
    {
        await _store.ListAsync(TeamOrigin.BuiltIn, CancellationToken.None);

        _container.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Never);
    }

    [Test]
    public async Task A_replace_at_the_expected_version_stores_the_new_team_and_a_stale_one_is_refused()
    {
        var team = FanTeam("fan-a-aaaaaa", "Abel Rovers");
        await _store.AddAsync(team, CancellationToken.None);
        var next = team with { Detail = team.Detail with { Version = 2, Team = team.Detail.Team with { Name = "Abel Town" } } };

        var first = await _store.ReplaceAsync(next, expectedVersion: 1, CancellationToken.None);
        var stale = await _store.ReplaceAsync(next, expectedVersion: 1, CancellationToken.None);

        first.Should().BeTrue();
        stale.Should().BeFalse();
        (await _store.GetAsync("fan-a-aaaaaa", CancellationToken.None))!.Detail.Team.Name.Should().Be("Abel Town");
    }

    [Test]
    public async Task A_replace_that_loses_a_race_on_the_etag_returns_false()
    {
        var team = FanTeam("fan-a-aaaaaa", "Abel Rovers");
        await _store.AddAsync(team, CancellationToken.None);
        var next = team with { Detail = team.Detail with { Version = 2 } };
        // Someone else saves between this caller's read and its write: the stored ETag moves on.
        _container.Setup(c => c.ReadItemStreamAsync("fan-a-aaaaaa", It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var read = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(_documents["fan-a-aaaaaa"]));
                Store("fan-a-aaaaaa", _documents["fan-a-aaaaaa"]);
                return new ResponseMessage(HttpStatusCode.OK) { Content = read };
            });

        var replaced = await _store.ReplaceAsync(next, expectedVersion: 1, CancellationToken.None);

        replaced.Should().BeFalse();
        _documents["fan-a-aaaaaa"]["detail"]!["version"]!.GetValue<int>().Should().Be(1);
    }

    [Test]
    public async Task Replacing_a_built_in_or_missing_team_returns_false()
    {
        var builtIn = await _store.GetAsync("north-bank-fc", CancellationToken.None);

        (await _store.ReplaceAsync(builtIn!, 1, CancellationToken.None)).Should().BeFalse();
        (await _store.ReplaceAsync(FanTeam("fan-gone-aaaaaa", "Gone"), 1, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task A_delete_flags_the_document_with_a_ttl_and_hides_the_team_everywhere()
    {
        await _store.AddAsync(FanTeam("fan-a-aaaaaa", "Abel Rovers"), CancellationToken.None);

        await _store.DeleteAsync("fan-a-aaaaaa", CancellationToken.None);

        _documents["fan-a-aaaaaa"]["deleted"]!.GetValue<bool>().Should().BeTrue();
        _documents["fan-a-aaaaaa"]["ttl"]!.GetValue<int>().Should().Be((int)TimeSpan.FromDays(7).TotalSeconds);
        (await _store.GetAsync("fan-a-aaaaaa", CancellationToken.None)).Should().BeNull();
        (await _store.ListAsync(TeamOrigin.Fan, CancellationToken.None)).Should().BeEmpty();
        (await _store.PlayerExistsAsync("fan-a-aaaaaa-p1", CancellationToken.None)).Should().BeFalse();
        _container.Verify(c => c.DeleteItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Deleting_a_missing_team_does_nothing()
    {
        var delete = () => _store.DeleteAsync("fan-gone-aaaaaa", CancellationToken.None);

        await delete.Should().NotThrowAsync();
    }

    [Test]
    public async Task A_player_is_found_in_a_built_in_squad_or_a_live_fan_squad()
    {
        var team = FanTeam("fan-a-aaaaaa", "Abel Rovers");
        await _store.AddAsync(team, CancellationToken.None);
        var builtInPlayer = (await _store.GetAsync("north-bank-fc", CancellationToken.None))!.Detail.Squad[0].Id;

        (await _store.PlayerExistsAsync(builtInPlayer, CancellationToken.None)).Should().BeTrue();
        (await _store.PlayerExistsAsync(team.Detail.Squad[0].Id, CancellationToken.None)).Should().BeTrue();
        (await _store.PlayerExistsAsync("fan-a-aaaaaa-p99", CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task A_cosmos_db_outage_becomes_a_source_unavailable_exception()
    {
        _container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResponseMessage(HttpStatusCode.ServiceUnavailable));

        var read = () => _store.GetAsync("fan-a-aaaaaa", CancellationToken.None);

        await read.Should().ThrowAsync<MatchSourceUnavailableException>();
    }

    private static StoredTeam FanTeam(string id, string name)
    {
        var detail = TeamViews.FromDraft(TeamDrafts.Valid(name), id, previous: null);
        return new StoredTeam(detail, EditKeys.Hash("a-key"));
    }

    private void Store(string id, JsonObject document)
    {
        document["_etag"] = $"\"etag-{++_etags}\"";
        _documents[id] = document;
    }

    // Just enough of Cosmos DB's SQL for the three queries the store runs.
    private List<JsonObject> Run(QueryDefinition query)
    {
        var live = _documents.Values.Where(d => d["deleted"]?.GetValue<bool>() != true);
        if (query.QueryText.Contains("TOP 1", StringComparison.Ordinal))
        {
            var playerId = query.GetQueryParameters().Single().Item2.ToString();
            return [.. live
                .Where(d => d["detail"]!["squad"]!.AsArray().Any(p => p!["id"]!.GetValue<string>() == playerId))
                .Select(d => new JsonObject { ["id"] = d["id"]!.GetValue<string>() })];
        }

        return [.. live];
    }

    private static FeedIterator Page(List<JsonObject> documents)
    {
        var iterator = new Mock<FeedIterator>();
        var more = true;
        iterator.SetupGet(i => i.HasMoreResults).Returns(() => more);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            more = false;
            var body = new JsonObject { ["Documents"] = new JsonArray([.. documents.Select(d => (JsonNode?)d.DeepClone())]) };
            return new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body)) };
        });
        return iterator.Object;
    }
}
