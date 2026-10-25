using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Registers the real team store and name guard when their Azure services are configured
/// (ADR-048, ADR-049). Without them the demo's in-memory store and format-only guard stay.
/// </summary>
internal static class TeamBuilderServices
{
    /// <summary>
    /// Stores fan teams in Cosmos DB when a <c>cosmos</c> connection is configured, and checks
    /// names with Content Safety when <c>MatchCentre:ContentSafety:Endpoint</c> is set. Also maps
    /// an unavailable guard to a <c>503</c> problem. Call before
    /// <see cref="Demo.DemoServices.AddDemoTeamBuilder"/>, which only fills what's still missing.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddTeamBuilderBackends(this IHostApplicationBuilder builder)
    {
        builder.Services.AddExceptionHandler<NameGuardUnavailableHandler>();

        if (MatchSources.IsCosmosConfigured(builder.Configuration))
        {
            builder.Services.AddOptions<TeamStoreOptions>()
                .BindConfiguration(TeamStoreOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();
            builder.Services.AddSingleton<ITeamStore, CosmosTeamStore>();
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{ContentSafetyOptions.SectionName}:Endpoint"]))
        {
            builder.Services.AddOptions<ContentSafetyOptions>()
                .BindConfiguration(ContentSafetyOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();
            builder.Services.AddMemoryCache();

            // Keyless: the registered credential, or DefaultAzureCredential, which signs in as the
            // user-assigned identity named by AZURE_CLIENT_ID in Azure and as the developer locally.
            builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<ContentSafetyOptions>>().Value;
                var clientOptions = new ContentSafetyClientOptions();
                clientOptions.Retry.MaxRetries = 1;
                return new ContentSafetyClient(new Uri(options.Endpoint!), provider.GetRequiredService<TokenCredential>(), clientOptions);
            });
            builder.Services.AddSingleton<INameGuard, ContentSafetyNameGuard>();
        }

        return builder;
    }

    /// <summary>Answers an unavailable name guard with a <c>503</c> problem, so nothing is stored (ADR-049).</summary>
    private sealed class NameGuardUnavailableHandler : IExceptionHandler
    {
        /// <inheritdoc />
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not NameGuardUnavailableException)
            {
                return false;
            }

            await TypedResults.Problem(
                title: "Names can't be checked right now",
                detail: "We couldn't check that name just now, so nothing was saved. Please try again in a moment.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: ProblemTypes.NameCheckUnavailable).ExecuteAsync(httpContext);
            return true;
        }
    }
}
