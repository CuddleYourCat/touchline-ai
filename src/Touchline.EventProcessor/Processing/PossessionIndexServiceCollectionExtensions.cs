using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Touchline.Search;

namespace Touchline.EventProcessor.Processing;

/// <summary>Registers the possession indexer behind the search-index function (ADR-039, ADR-057).</summary>
public static class PossessionIndexServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="PossessionIndexWriter"/> and the search library's indexing
    /// (<see cref="PossessionIndexingServiceCollectionExtensions.AddPossessionIndexing"/>). With
    /// <c>Search:Endpoint</c> unset the indexer is a no-op, so the trigger still binds and completes
    /// its messages. Needs <c>AddLiveCommentary</c> first, for the models, the credential and the
    /// options, and an <see cref="IMatchStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Search</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddPossessionIndexer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddPossessionIndexing(configuration);
        services.TryAddSingleton<PossessionIndexWriter>();
        return services;
    }
}
