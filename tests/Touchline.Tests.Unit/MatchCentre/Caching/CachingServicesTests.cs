using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Caching;
using Touchline.MatchCentre.Live;
using Touchline.MatchCentre.Storage;
using Touchline.Storage;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// How the caches are composed (ADR-061): in memory without Redis, Redis behind memory with it,
/// and the limits in Redis only when Cosmos DB is there to fall back to.
/// </summary>
[TestFixture]
public class CachingServicesTests
{
    // Nothing listens here, so the connection never comes up: Redis configured but down.
    private const string _unreachableRedis = "localhost:1,connectTimeout=200";

    [Test]
    public async Task Without_redis_the_caches_stay_in_memory_and_the_limits_in_cosmos_db()
    {
        await using var factory = WithCosmos(MatchCentreFactory.Finished());

        factory.Services.GetRequiredService<IOutputCacheStore>().Should().NotBeOfType<TieredOutputCacheStore>();
        (factory.Services.GetService<IDistributedCache>() is RedisDistributedCache).Should().BeFalse();
        factory.Services.GetService<IRedisConnection>().Should().BeNull();
        factory.Services.GetRequiredService<IFanMatchLimitStore>().Should().BeOfType<CosmosFanMatchLimitStore>();
    }

    [Test]
    public async Task With_redis_both_caches_and_the_limits_go_through_it()
    {
        await using var factory = WithCosmos(MatchCentreFactory.Finished().WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:redis", _unreachableRedis)));

        factory.Services.GetRequiredService<IOutputCacheStore>().Should().BeOfType<TieredOutputCacheStore>();
        factory.Services.GetRequiredService<IDistributedCache>().Should().BeOfType<RedisDistributedCache>();
        factory.Services.GetRequiredService<IFanMatchLimitStore>().Should().BeOfType<FallbackFanMatchLimitStore>();
    }

    [Test]
    public async Task An_azure_endpoint_alone_also_turns_redis_on()
    {
        await using var factory = MatchCentreFactory.Finished().WithWebHostBuilder(host => host
            .UseSetting("MatchCentre:Redis:Endpoint", "redis-touchline-test.swedencentral.redis.azure.net:10000")
            .ConfigureTestServices(services => services.AddSingleton<Azure.Core.TokenCredential>(new FailingCredential())));

        factory.Services.GetRequiredService<IOutputCacheStore>().Should().BeOfType<TieredOutputCacheStore>();
        factory.Services.GetService<IFanMatchLimitStore>().Should().BeNull("there's no Cosmos DB, so no fan matches either");
    }

    [Test]
    public async Task With_redis_down_the_match_centre_still_serves_and_caches_from_memory()
    {
        await using var factory = MatchCentreFactory.Finished().WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:redis", _unreachableRedis));
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(new Uri("/api/matches", UriKind.Relative));
        var demoId = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement[0].GetProperty("id").GetString();
        using var match = await client.GetAsync(new Uri($"/api/matches/{demoId}", UriKind.Relative));
        using var moments = await client.GetAsync(new Uri($"/api/matches/{demoId}/moments?lang=es", UriKind.Relative));

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        match.StatusCode.Should().Be(HttpStatusCode.OK);
        moments.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Services.GetRequiredService<IRedisConnection>().Database.Should().BeNull();
    }

    private static WebApplicationFactory<Program> WithCosmos(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:cosmos", "AccountEndpoint=https://localhost:8081/;")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton(new Mock<CosmosClient>().Object);
                services.AddSingleton<IChangeFeedSource, NoChanges>();
            }));

    private sealed class NoChanges : IChangeFeedSource
    {
        public Task<IReadOnlyList<JsonElement>> ReadChangesAsync(string container, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonElement>>([]);
    }

    private sealed class FailingCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new Azure.Identity.CredentialUnavailableException("no identity in tests");

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new Azure.Identity.CredentialUnavailableException("no identity in tests");
    }
}
