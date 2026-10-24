using Azure.Core;
using Azure.Search.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Touchline.Search;

/// <summary>Registers the write side of possession search (ADR-039, ADR-056, ADR-057).</summary>
public static class PossessionIndexingServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="PossessionSearchOptions"/>, <see cref="SearchMetrics"/>, the
    /// <see cref="KeyMomentSummaryWriter"/> and an <see cref="IPossessionIndexer"/>.
    /// </summary>
    /// <remarks>
    /// With <c>Search:Endpoint</c> unset, or <c>Squad:FoundryEndpoint</c> unset (there's nothing to
    /// embed with, and documents are never indexed without vectors), only
    /// <see cref="NullPossessionIndexer"/> is registered and a warning is logged once at start-up.
    /// Otherwise it adds a keyless <see cref="SearchClient"/>, <see cref="AzurePossessionIndexer"/>
    /// and an embedding generator for
    /// <see cref="PossessionSearchOptions.EmbeddingDeployment"/> on the Squad's Foundry endpoint.
    /// The host must register a <see cref="TokenCredential"/> and the keyed small chat client first
    /// (<c>AddLiveCommentary</c> does both).
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Search</c> and <c>Squad</c> sections.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddPossessionIndexing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PossessionSearchOptions>().Bind(configuration.GetSection(PossessionSearchOptions.SectionName));
        services.AddMetrics();
        services.TryAddSingleton<SearchMetrics>();
        services.TryAddSingleton<KeyMomentSummaryWriter>();

        services.TryAddSingleton<ITeamProfiles, EmbeddedTeamProfiles>();

        if (!configuration.GetSection(PossessionSearchOptions.SectionName).Get<PossessionSearchOptions>()?.IsConfigured ?? true)
        {
            return Off(services, "Search:Endpoint is not set");
        }

        // The one place that decides whether there is anything to embed with.
        if (!services.AddPossessionEmbeddings(configuration))
        {
            return Off(services, "Squad:FoundryEndpoint is not set, so there is nothing to embed with");
        }

        services.TryAddSingleton(provider => new SearchClient(
            provider.GetRequiredService<IOptions<PossessionSearchOptions>>().Value.Endpoint!,
            provider.GetRequiredService<IOptions<PossessionSearchOptions>>().Value.IndexName,
            provider.GetRequiredService<TokenCredential>()));
        services.TryAddSingleton<IPossessionIndexer>(provider => new AzurePossessionIndexer(provider.GetRequiredService<SearchClient>()));
        return services;
    }

    private static IServiceCollection Off(IServiceCollection services, string reason)
    {
        services.TryAddSingleton<IPossessionIndexer, NullPossessionIndexer>();
        services.AddHostedService(provider => new IndexingOffNotice(provider.GetRequiredService<ILogger<IndexingOffNotice>>(), reason));
        return services;
    }

}

/// <summary>Logs, once at start-up, that possession indexing is off (ADR-057).</summary>
/// <param name="logger">The logger.</param>
/// <param name="reason">Why it's off.</param>
internal sealed partial class IndexingOffNotice(ILogger<IndexingOffNotice> logger, string reason) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogOff(logger, reason);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Possession indexing is off: {Reason}")]
    private static partial void LogOff(ILogger logger, string reason);
}
