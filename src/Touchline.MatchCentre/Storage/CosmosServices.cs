using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Registers the Cosmos DB read side behind the Match Centre's ports.
/// </summary>
internal static class CosmosServices
{
    /// <summary>
    /// Serves matches, events, moments, momentum and season cards from Cosmos DB, through a
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
        builder.Services.AddSingleton<IMatchReadModel, CosmosMatchReadModel>();
        return builder;
    }
}
