using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;
using Touchline.Storage;

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

            // Fan match requests (ADR-050): the rosters of record for fan matches, read by every
            // name lookup, and written by the launcher.
            builder.Services.AddSingleton<IFanMatchRequestStore, CosmosFanMatchRequestStore>();
            builder.Services.AddSingleton<IFanMatchLimitStore, CosmosFanMatchLimitStore>();
            builder.Services.AddSingleton<IMatchTeamProfiles, FanMatchTeamProfiles>();

            // The deadlines are read by the read model (a scheduled match) as well as the launcher.
            builder.Services.AddOptions<FanMatchOptions>()
                .BindConfiguration(FanMatchOptions.SectionName)
                .ValidateDataAnnotations()
                .Validate(o => string.IsNullOrWhiteSpace(o.JobResourceId)
                        || (ResourceIdentifier.TryParse(o.JobResourceId, out var id) && id?.ResourceType == "Microsoft.App/jobs"),
                    "MatchCentre:FanMatches:JobResourceId must be a Container Apps job's resource ID.")
                .ValidateOnStart();
            builder.Services.AddExceptionHandler<FanMatchNotStartedHandler>();

            // The launcher only where there's a job to start: locally under Aspire there's none, and
            // the unconnected launcher answers 503 (ADR-050). In Azure the setting is there only while
            // deployFanMatches is on, which makes it the kill switch.
            if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{FanMatchOptions.SectionName}:{nameof(FanMatchOptions.JobResourceId)}"]))
            {
                builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
                builder.Services.AddSingleton(provider => new ArmClient(provider.GetRequiredService<TokenCredential>()));
                builder.Services.AddSingleton<ISimulatorJob, ArmSimulatorJob>();
                builder.Services.AddSingleton<IMatchLauncher, FanMatchLauncher>();
            }
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

    /// <summary>Answers a fan match that didn't start with a calm <c>410</c> problem (ADR-050).</summary>
    private sealed class FanMatchNotStartedHandler : IExceptionHandler
    {
        /// <inheritdoc />
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not FanMatchNotStartedException)
            {
                return false;
            }

            await TypedResults.Problem(
                title: "Match didn't start",
                detail: "This match couldn't be started. Pick the teams again to kick off a new one.",
                statusCode: StatusCodes.Status410Gone,
                type: ProblemTypes.MatchNotStarted).ExecuteAsync(httpContext);
            return true;
        }
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
