using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// Serves matches, events, moments with their commentary, momentum and season cards from Cosmos DB, through a
    /// keyless Aspire client (the <c>cosmos</c> connection). Not registered yet: routing between
    /// the demo and the live source is a later slice (ADR-036).
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddCosmosReadModel(this IHostApplicationBuilder builder)
    {
        // No Cosmos DB health check: /health is the Container App's readiness probe, so an
        // outage would take every replica out of rotation and the demo fallback with it.
        builder.AddAzureCosmosClient("cosmos", settings => settings.DisableHealthChecks = true);
        builder.Services.AddSingleton<ICommentaryStore, CosmosCommentaryStore>();
        builder.Services.AddSingleton<IMatchReadModel, CosmosMatchReadModel>();
        return builder;
    }

    /// <summary>
    /// Serves the live stream from the Cosmos DB change feed (ADR-037): the pull-model source, the
    /// replica's hubs, the background reader that feeds them, and the <see cref="ILiveFeed"/>.
    /// Needs <see cref="AddCosmosReadModel"/>. Not registered yet: routing by match ID is slice 4.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddCosmosLiveFeed(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IChangeFeedSource, CosmosChangeFeedSource>();
        builder.Services.AddSingleton(provider => ActivatorUtilities.CreateInstance<LiveMatchHubs>(provider, LiveMatchHubs.DefaultBuffer));
        builder.Services.AddHostedService<ChangeFeedReader>();
        builder.Services.AddSingleton<ILiveFeed, CosmosLiveFeed>();
        return builder;
    }
}
