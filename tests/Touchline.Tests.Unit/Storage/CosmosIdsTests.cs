using FluentAssertions;
using Touchline.MatchCentre.Storage;
using Touchline.Storage;

namespace Touchline.Tests.Unit.Storage;

/// <summary>The Cosmos DB ID rules, and the cache the read model keeps finished matches in.</summary>
[TestFixture]
public class CosmosIdsTests
{
    [TestCase("match-2026-27-r01-north-bank-fc-v-thames-valley-united")]
    [TestCase("2026-27:player-7")]
    public void An_ordinary_ID_is_valid(string id) => CosmosIds.IsValid(id).Should().BeTrue();

    [TestCase("")]
    [TestCase(null)]
    [TestCase("a/b")]
    [TestCase(@"a\b")]
    [TestCase("a?b")]
    [TestCase("a#b")]
    public void An_ID_Cosmos_DB_forbids_is_invalid(string? id) => CosmosIds.IsValid(id).Should().BeFalse();

    [Test]
    public void An_ID_is_valid_up_to_255_characters()
    {
        CosmosIds.IsValid(new string('m', 255)).Should().BeTrue();
        CosmosIds.IsValid(new string('m', 256)).Should().BeFalse();
    }

    [Test]
    public void The_cache_drops_the_least_recently_used_entry_when_full()
    {
        var cache = new RecentCache<int>(2);
        cache.Add("a", 1);
        cache.Add("b", 2);
        cache.TryGet("a", out _).Should().BeTrue();

        cache.Add("c", 3);

        cache.TryGet("b", out _).Should().BeFalse("it was used longest ago");
        cache.TryGet("a", out var a).Should().BeTrue();
        a.Should().Be(1);
        cache.TryGet("c", out _).Should().BeTrue();
    }
}
