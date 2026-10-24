using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Domain.Squad;

namespace Touchline.Search;

/// <summary>Registers possession search for the Analyst (ADR-040, ADR-057).</summary>
public static class SearchQueryServiceCollectionExtensions
{
    /// <summary>
    /// When <c>Search:Endpoint</c> is set, adds <see cref="IPossessionSearch"/> over Azure AI Search
    /// (keyless, with the registered <see cref="TokenCredential"/> or <see cref="DefaultAzureCredential"/>),
    /// the query embedder (shared with indexing; only when <c>Squad:FoundryEndpoint</c> is set, else the
    /// search runs keyword only) and <see cref="SearchMetrics"/>.
    /// When the endpoint isn't set, registers nothing, so the Analyst has no search tool (ADR-057).
    /// </summary>
    /// <remarks>Call it before <c>AddAnalystQa</c>, which picks up <see cref="IPossessionSearch"/> if present.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Search</c> and <c>Squad</c> sections.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddPossessionSearch(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PossessionSearchOptions.SectionName);
        var options = section.Get<PossessionSearchOptions>() ?? new PossessionSearchOptions();
        if (!options.IsConfigured)
        {
            return services;
        }

        services.AddOptions<PossessionSearchOptions>().Bind(section);
        services.AddMetrics();
        services.TryAddSingleton<SearchMetrics>();

        // False means no Foundry endpoint: search still works, keyword only (the class logs that once).
        _ = services.AddPossessionEmbeddings(configuration);
        services.TryAddSingleton<TokenCredential>(_ =>
        {
            var credentialOptions = new DefaultAzureCredentialOptions();
            if (configuration["Squad:ManagedIdentityClientId"] is { Length: > 0 } clientId)
            {
                credentialOptions.ManagedIdentityClientId = clientId;
            }

            return new DefaultAzureCredential(credentialOptions);
        });

        services.AddSingleton(provider =>
        {
            var search = provider.GetRequiredService<IOptions<PossessionSearchOptions>>().Value;
            return new SearchClient(search.Endpoint!, search.IndexName, provider.GetRequiredService<TokenCredential>());
        });
        services.AddSingleton<IPossessionSearch>(provider => new AzurePossessionSearch(
            provider.GetRequiredService<SearchClient>(),
            provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>(),
            provider.GetRequiredService<IOptions<PossessionSearchOptions>>(),
            provider.GetRequiredService<SearchMetrics>(),
            provider.GetRequiredService<ILogger<AzurePossessionSearch>>()));
        return services;
    }
}
