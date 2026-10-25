using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Touchline.Storage;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// Registers the Match Centre's caches (ADR-061): the output cache for finished-match reads,
/// HybridCache for the name guard's verdicts, and, when Redis is configured, Redis behind all
/// three uses, the fan-match limits included.
/// </summary>
internal static class CachingServices
{
    /// <summary>Whether Redis is configured: the local Aspire container, or an Azure Managed Redis endpoint.</summary>
    /// <param name="configuration">The app's configuration.</param>
    /// <returns>True when Redis should be used.</returns>
    public static bool IsRedisConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString(RedisOptions.ConnectionName))
        || !string.IsNullOrWhiteSpace(configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.Endpoint)}"]);

    /// <summary>
    /// Adds the caches. Without Redis: the output cache and HybridCache stay in memory, and the
    /// limits stay in Cosmos DB. With Redis: memory first and Redis second for both caches, and the
    /// limits in Redis with Cosmos DB as the fallback. Call after
    /// <see cref="TeamBuilding.TeamBuilderServices.AddTeamBuilderBackends"/>, whose Cosmos DB limit
    /// store this wraps.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddTouchlineCaching(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<FinishedMatchCacheOptions>()
            .BindConfiguration(FinishedMatchCacheOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOutputCache();
        services.AddHybridCache();

        if (!IsRedisConfigured(builder.Configuration))
        {
            return builder;
        }

        services.AddOptions<RedisOptions>()
            .BindConfiguration(RedisOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<IRedisConnection, RedisConnection>();
        services.AddHostedService<RedisConnectionStarter>();

        // HybridCache finds its second level as the registered IDistributedCache.
        services.Replace(ServiceDescriptor.Singleton<IDistributedCache, RedisDistributedCache>());
        services.Replace(ServiceDescriptor.Singleton<IOutputCacheStore, TieredOutputCacheStore>());

        // The limits only exist with Cosmos DB (TeamBuilderServices registers its store then).
        if (services.Any(service => service.ServiceType == typeof(IFanMatchLimitStore)))
        {
            services.AddSingleton<IFanMatchLimitScripts, LuaFanMatchLimitScripts>();
            services.Replace(ServiceDescriptor.Singleton<IFanMatchLimitStore>(provider => new FallbackFanMatchLimitStore(
                new RedisFanMatchLimitStore(provider.GetRequiredService<IFanMatchLimitScripts>()),
                new CosmosFanMatchLimitStore(provider.GetRequiredService<CosmosClient>()),
                provider.GetRequiredService<ILogger<FallbackFanMatchLimitStore>>())));
        }

        return builder;
    }
}
