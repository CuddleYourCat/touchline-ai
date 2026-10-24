using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using Touchline.Agents;

namespace Touchline.Search;

/// <summary>Registers the embedding generator the indexer (write) and the query side (read) share (ADR-040, ADR-056).</summary>
internal static class PossessionEmbeddingServiceCollectionExtensions
{
    /// <summary>
    /// Adds an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for
    /// <see cref="PossessionSearchOptions.EmbeddingDeployment"/> on the Squad's Foundry endpoint
    /// (<c>{Squad:FoundryEndpoint}/openai/v1/</c>), signed in with an Entra ID token, and a
    /// <see cref="TokenCredential"/> if the host has none. Everything is added with TryAdd, and
    /// nothing at all when <c>Squad:FoundryEndpoint</c> is missing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Squad</c> and <c>Search</c> sections.</param>
    /// <returns>True if the generator is registered; false when <c>Squad:FoundryEndpoint</c> is missing and nothing was added. The caller binds <see cref="PossessionSearchOptions"/> (for the deployment name).</returns>
    internal static bool AddPossessionEmbeddings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var foundry = configuration[$"{SquadOptions.SectionName}:{nameof(SquadOptions.FoundryEndpoint)}"];
        if (string.IsNullOrWhiteSpace(foundry))
        {
            return false;
        }

        var endpoint = new Uri(foundry);
        var clientId = configuration[$"{SquadOptions.SectionName}:{nameof(SquadOptions.ManagedIdentityClientId)}"];
        services.TryAddSingleton<TokenCredential>(_ =>
        {
            var options = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                options.ManagedIdentityClientId = clientId;
            }

            return new DefaultAzureCredential(options);
        });
        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(provider =>
        {
            var deployment = provider.GetRequiredService<IOptions<PossessionSearchOptions>>().Value.EmbeddingDeployment;

            // OPENAI001: the same key-free Entra ID bearer policy the chat clients use
            // (SquadModelServiceCollectionExtensions), and the SDK's only way to send it.
#pragma warning disable OPENAI001
            var client = new EmbeddingClient(
                model: deployment,
                authenticationPolicy: new BearerTokenPolicy(provider.GetRequiredService<TokenCredential>(), SquadModelServiceCollectionExtensions.TokenScope),
                options: new OpenAIClientOptions { Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/") });
#pragma warning restore OPENAI001

            return client.AsIEmbeddingGenerator();
        });
        return true;
    }
}
