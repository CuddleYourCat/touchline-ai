using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Live;
using Touchline.Storage;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Registers the Cosmos DB read side behind the Match Centre's ports.
/// </summary>
internal static class CosmosServices
{
    /// <summary>
    /// Serves matches, events, moments with their commentary, recaps, momentum and season cards from Cosmos DB, through a
    /// keyless Aspire client (the <c>cosmos</c> connection). The read model is keyed
    /// <see cref="MatchSources.Cosmos"/>, for the router to serve every non-demo ID (ADR-036).
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddCosmosReadModel(this IHostApplicationBuilder builder)
    {
        // No Cosmos DB health check: /health is the Container App's readiness probe, so an
        // outage would take every replica out of rotation and the demo fallback with it.
        builder.AddAzureCosmosClient("cosmos", settings => settings.DisableHealthChecks = true);
        builder.Services.AddSingleton<ICommentaryStore, CosmosCommentaryStore>();
        builder.Services.AddSingleton<IRecapStore, CosmosRecapStore>();
        builder.Services.AddKeyedSingleton<IMatchReadModel, CosmosMatchReadModel>(MatchSources.Cosmos);
        return builder;
    }

    /// <summary>
    /// Serves the live stream from the Cosmos DB change feed (ADR-037): the pull-model source, the
    /// replica's hubs, the background reader that feeds them, and the <see cref="ILiveFeed"/>
    /// keyed <see cref="MatchSources.Cosmos"/>. Needs <see cref="AddCosmosReadModel"/>.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddCosmosLiveFeed(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IChangeFeedSource, CosmosChangeFeedSource>();
        builder.Services.AddOptions<LiveFeedOptions>()
            .BindConfiguration(LiveFeedOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // Built by the container, so every parameter (the options included, and any added later)
        // resolves from DI; the buffer keeps its default.
        builder.Services.AddSingleton<LiveMatchHubs>();
        builder.Services.AddHostedService<ChangeFeedReader>();
        builder.Services.AddKeyedSingleton<ILiveFeed, CosmosLiveFeed>(MatchSources.Cosmos);
        return builder;
    }
}
