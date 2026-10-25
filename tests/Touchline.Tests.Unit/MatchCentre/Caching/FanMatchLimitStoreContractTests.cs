using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using Touchline.Domain.Storage;
using Touchline.MatchCentre.Caching;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// The rules every fan-match limit store keeps (ADR-050, ADR-061), run against both the Cosmos DB
/// store (over an in-memory container that honours ETags) and the Redis store (over the scripts'
/// rules in C#), so the two can't drift apart.
/// </summary>
public abstract class FanMatchLimitStoreContractTests
{
    /// <summary>A Saturday morning, UTC.</summary>
    protected static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The day <see cref="Now"/> falls on.</summary>
    protected static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private IFanMatchLimitStore _store = null!;

    [SetUp]
    public void CreateStore() => _store = Create();

    [Test]
    public async Task A_day_gives_out_exactly_its_limit()
    {
        var taken = new List<bool>();
        for (var i = 0; i < 7; i++)
        {
            taken.Add(await _store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None));
        }

        taken.Should().Equal(true, true, true, true, true, false, false);
    }

    [Test]
    public async Task A_limit_of_zero_gives_nothing()
    {
        (await _store.TryTakeDaySlotAsync(Today, 0, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task A_returned_slot_can_be_taken_again()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None);
        }

        await _store.ReturnDaySlotAsync(Today, CancellationToken.None);

        (await _store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None)).Should().BeTrue();
        (await _store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task A_refund_never_goes_below_zero()
    {
        await _store.ReturnDaySlotAsync(Today, CancellationToken.None);
        await _store.TryTakeDaySlotAsync(Today, 1, CancellationToken.None);
        await _store.ReturnDaySlotAsync(Today, CancellationToken.None);
        await _store.ReturnDaySlotAsync(Today, CancellationToken.None);

        (await _store.TryTakeDaySlotAsync(Today, 1, CancellationToken.None)).Should().BeTrue();
        (await _store.TryTakeDaySlotAsync(Today, 1, CancellationToken.None)).Should().BeFalse("two refunds of one slot still leave one slot");
    }

    [Test]
    public async Task Each_day_counts_on_its_own()
    {
        await _store.TryTakeDaySlotAsync(Today, 1, CancellationToken.None);

        (await _store.TryTakeDaySlotAsync(Today.AddDays(1), 1, CancellationToken.None)).Should().BeTrue();
    }

    [Test]
    public async Task The_first_claim_of_a_key_wins_and_a_repeat_sees_it_unchanged()
    {
        (await _store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None)).Should().BeNull();

        var repeat = await _store.TryClaimKeyAsync("click-1", "exh-b", Now.AddSeconds(5), CancellationToken.None);

        repeat.Should().Be(new IdempotencyClaim("exh-a", Now));
    }

    [Test]
    public async Task Different_keys_are_claimed_separately()
    {
        await _store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);

        (await _store.TryClaimKeyAsync("click-2", "exh-b", Now, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task A_key_moves_to_a_new_request_when_its_claim_is_still_the_one_read()
    {
        await _store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);

        var moved = await _store.TryReclaimKeyAsync("click-1", new IdempotencyClaim("exh-a", Now), "exh-b", Now.AddMinutes(2), CancellationToken.None);

        moved.Should().BeTrue();
        (await _store.TryClaimKeyAsync("click-1", "exh-c", Now, CancellationToken.None)).Should().Be(new IdempotencyClaim("exh-b", Now.AddMinutes(2)));
    }

    [Test]
    public async Task A_key_that_another_request_moved_first_stays_theirs()
    {
        await _store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);
        await _store.TryReclaimKeyAsync("click-1", new IdempotencyClaim("exh-a", Now), "exh-b", Now, CancellationToken.None);

        var late = await _store.TryReclaimKeyAsync("click-1", new IdempotencyClaim("exh-a", Now), "exh-c", Now, CancellationToken.None);

        late.Should().BeFalse();
        (await _store.TryClaimKeyAsync("click-1", "exh-d", Now, CancellationToken.None))!.RequestId.Should().Be("exh-b");
    }

    [Test]
    public async Task A_key_that_went_meanwhile_is_claimed_afresh_by_a_reclaim()
    {
        var moved = await _store.TryReclaimKeyAsync("click-1", new IdempotencyClaim("exh-a", Now), "exh-b", Now, CancellationToken.None);

        moved.Should().BeTrue();
        (await _store.TryClaimKeyAsync("click-1", "exh-c", Now, CancellationToken.None))!.RequestId.Should().Be("exh-b");
    }

    [Test]
    public async Task A_release_lets_the_key_be_claimed_again()
    {
        await _store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);

        await _store.ReleaseKeyAsync("click-1", "exh-a", CancellationToken.None);

        (await _store.TryClaimKeyAsync("click-1", "exh-b", Now, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task One_request_cannot_release_another_requests_claim()
    {
        await _store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);

        await _store.ReleaseKeyAsync("click-1", "exh-other", CancellationToken.None);

        (await _store.TryClaimKeyAsync("click-1", "exh-b", Now, CancellationToken.None))!.RequestId.Should().Be("exh-a");
    }

    [Test]
    public async Task Releasing_a_key_that_was_never_claimed_does_nothing()
    {
        await _store.ReleaseKeyAsync("click-never", "exh-a", CancellationToken.None);

        (await _store.TryClaimKeyAsync("click-never", "exh-a", Now, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task The_key_itself_is_never_stored()
    {
        await _store.TryClaimKeyAsync("my-secret-click-key", "exh-a", Now, CancellationToken.None);

        StoredText().Should().NotContain("my-secret-click-key");
    }

    /// <summary>A fresh, empty store.</summary>
    /// <returns>The store under test.</returns>
    protected abstract IFanMatchLimitStore Create();

    /// <summary>Everything the store holds, as text.</summary>
    /// <returns>Keys and values together.</returns>
    protected abstract string StoredText();
}

/// <summary>The contract against <see cref="RedisFanMatchLimitStore"/>, plus what's Redis's own: key names and lifetimes.</summary>
[TestFixture]
public class RedisFanMatchLimitStoreTests : FanMatchLimitStoreContractTests
{
    private InMemoryLimitScripts _scripts = null!;

    [Test]
    public async Task A_day_counter_is_keyed_by_the_day_and_expires_at_the_next_utc_midnight()
    {
        await Create().TryTakeDaySlotAsync(Today, 5, CancellationToken.None);

        _scripts.Keys.Should().ContainKey("touchline:fan-matches:day-20261025")
            .WhoseValue.ExpiresAt.Should().Be(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task A_claim_is_keyed_by_the_keys_hash_and_lasts_a_day_as_in_cosmos_db()
    {
        await Create().TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);

        var key = "touchline:fan-matches:" + CosmosFanMatchLimitStore.KeyId("click-1");
        _scripts.Keys.Should().ContainKey(key).WhoseValue.Should().Be((ClaimValue.Format("exh-a", Now), Now + TimeSpan.FromDays(1)));
    }

    [Test]
    public async Task A_refund_keeps_the_counters_expiry()
    {
        var store = Create();
        await store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None);
        await store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None);

        await store.ReturnDaySlotAsync(Today, CancellationToken.None);

        _scripts.Keys["touchline:fan-matches:day-20261025"].Should().Be(("1", new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public async Task Parallel_launches_share_out_exactly_the_days_slots()
    {
        var store = Create();

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => store.TryTakeDaySlotAsync(Today, 5, CancellationToken.None))));

        results.Count(taken => taken).Should().Be(5);
    }

    [TestCase("exh-202610250900-1a2b3c4d")]
    [TestCase("has:colons:inside")]
    public void A_claim_reads_back_as_written(string requestId)
    {
        var value = ClaimValue.Format(requestId, Now);

        ClaimValue.Parse(value).Should().Be(new IdempotencyClaim(requestId, Now));
        ClaimValue.RequestIdOf(value).Should().Be(requestId);
    }

    [TestCase("")]
    [TestCase("no-colon")]
    [TestCase("abc:exh-a")]
    public void A_value_that_is_not_a_claim_reads_as_none(string value) => ClaimValue.Parse(value).Should().BeNull();

    [Test]
    public async Task A_claim_that_cannot_be_read_is_an_error_not_a_repeat()
    {
        var store = Create();
        _scripts.Keys["touchline:fan-matches:" + CosmosFanMatchLimitStore.KeyId("click-1")] = ("garbage", null);

        var claim = () => store.TryClaimKeyAsync("click-1", "exh-a", Now, CancellationToken.None);

        await claim.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <inheritdoc />
    protected override IFanMatchLimitStore Create()
    {
        _scripts ??= new InMemoryLimitScripts { Now = Now };
        return new RedisFanMatchLimitStore(_scripts);
    }

    /// <inheritdoc />
    protected override string StoredText() => string.Join("\n", _scripts.Keys.Select(k => $"{k.Key}={k.Value.Value}"));

    [SetUp]
    public void ClearScripts() => _scripts.Keys.Clear();
}

/// <summary>The contract against <see cref="CosmosFanMatchLimitStore"/>, over an in-memory container.</summary>
[TestFixture]
public class CosmosFanMatchLimitStoreContractTests : FanMatchLimitStoreContractTests
{
    private readonly Dictionary<string, JsonObject> _documents = [];
    private int _etags;

    /// <inheritdoc />
    protected override IFanMatchLimitStore Create()
    {
        _documents.Clear();
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetContainer(CosmosNames.Database, CosmosNames.MatchRequests)).Returns(Container().Object);
        return new CosmosFanMatchLimitStore(client.Object);
    }

    /// <inheritdoc />
    protected override string StoredText() => string.Join("\n", _documents.Select(d => $"{d.Key}={d.Value.ToJsonString()}"));

    private Mock<Container> Container()
    {
        var container = new Mock<Container>();
        container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
                _documents.TryGetValue(id, out var document)
                    ? new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document)) }
                    : new ResponseMessage(HttpStatusCode.NotFound));
        container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
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
        container.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
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
        container.Setup(c => c.DeleteItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
                _documents.Remove(id) ? new ResponseMessage(HttpStatusCode.NoContent) : new ResponseMessage(HttpStatusCode.NotFound));
        return container;
    }

    private void Store(string id, JsonObject document)
    {
        document["_etag"] = $"\"e{++_etags}\"";
        _documents[id] = document;
    }
}
