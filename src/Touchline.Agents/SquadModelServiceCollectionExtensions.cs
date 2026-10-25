using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Touchline.Agents;

/// <summary>
/// Registers the squad's model clients (ADR-007, ADR-043).
/// </summary>
public static class SquadModelServiceCollectionExtensions
{
    /// <summary>The Entra ID scope for Azure OpenAI and Foundry models.</summary>
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Adds <see cref="SquadOptions"/> (validated on start), <see cref="SquadMetrics"/>, and two
    /// keyed <see cref="IChatClient"/>s, <see cref="SquadModels.Small"/> and
    /// <see cref="SquadModels.Large"/>. Each is the GA OpenAI SDK's chat client against the
    /// Foundry account's v1 endpoint, signed in with an Entra ID token (no keys), wrapped in
    /// <see cref="TokenBudgetChatClient"/>.
    /// </summary>
    /// <remarks>
    /// The token comes from the registered <see cref="TokenCredential"/>, or
    /// <see cref="DefaultAzureCredential"/> if the host registered none: the user-assigned managed
    /// identity named by <see cref="SquadOptions.ManagedIdentityClientId"/> or <c>AZURE_CLIENT_ID</c> in
    /// Azure, the developer's sign-in locally.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Squad</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddSquadModels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SquadOptions>()
            .Bind(configuration.GetSection(SquadOptions.SectionName))
            .PostConfigure(options =>
            {
                // Defaults are applied here, not in the initializer: the binder appends to a
                // collection that already has items, so "es" in config would become en, es, fr, es.
                if (options.Languages.Count == 0)
                {
                    options.Languages = [.. SquadOptions.DefaultLanguages];
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SquadOptions>, SquadOptionsValidator>());

        services.AddMetrics();
        services.TryAddSingleton<SquadMetrics>();
        services.TryAddSingleton<TokenCredential>(provider =>
            new DefaultAzureCredential(CredentialOptions(provider.GetRequiredService<IOptions<SquadOptions>>().Value)));

        services.AddKeyedSingleton<IChatClient>(SquadModels.Small, (provider, _) => CreateClient(provider, SquadModels.Small));
        services.AddKeyedSingleton<IChatClient>(SquadModels.Large, (provider, _) => CreateClient(provider, SquadModels.Large));
        return services;
    }

    /// <summary>
    /// The fallback credential's options: the configured user-assigned identity's client ID, or
    /// whatever <see cref="DefaultAzureCredentialOptions"/> reads from <c>AZURE_CLIENT_ID</c>.
    /// </summary>
    internal static DefaultAzureCredentialOptions CredentialOptions(SquadOptions options)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;
        }

        return credentialOptions;
    }

    /// <summary>The Azure OpenAI v1 endpoint under a Foundry account's endpoint.</summary>
    internal static Uri V1Endpoint(Uri foundryEndpoint) =>
        new(foundryEndpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/");

    private static IChatClient CreateClient(IServiceProvider provider, string size)
    {
        var options = provider.GetRequiredService<IOptions<SquadOptions>>().Value;
        var deployment = options.Deployments.For(size);
        var credential = provider.GetRequiredService<TokenCredential>();

        // OPENAI001: the AuthenticationPolicy constructor is marked for evaluation in OpenAI 2.14.0,
        // but it's the SDK's only key-free way to send an Entra ID bearer token, and Microsoft's
        // documented route to the Azure OpenAI v1 endpoint. The package version is pinned centrally.
#pragma warning disable OPENAI001
        var chatClient = new ChatClient(
            model: deployment,
            authenticationPolicy: new BearerTokenPolicy(credential, TokenScope),
            options: new OpenAIClientOptions { Endpoint = V1Endpoint(options.FoundryEndpoint!) });
#pragma warning restore OPENAI001

        return new TokenBudgetChatClient(
            chatClient.AsIChatClient(),
            size,
            deployment,
            provider.GetRequiredService<IOptions<SquadOptions>>(), // read per call: caps follow a live change (ADR-062)
            provider.GetRequiredService<SquadMetrics>());
    }
}
