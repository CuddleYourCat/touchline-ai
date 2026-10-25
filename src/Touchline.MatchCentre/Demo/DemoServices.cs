using Microsoft.Extensions.DependencyInjection.Extensions;
using Touchline.Domain.Squad;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// Registers the demo match behind the Match Centre's ports.
/// </summary>
internal static class DemoServices
{
    /// <summary>
    /// Serves the recorded match as the squad, and as the read model and the live feed keyed
    /// <see cref="MatchSources.Demo"/>, for the routers to serve its <c>demo-*</c> IDs (ADR-036).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddDemoMatch(this IServiceCollection services)
    {
        services.AddOptions<DemoOptions>()
            .BindConfiguration(DemoOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<DemoMatchSource>(provider, DemoRecording.Events));
        services.AddKeyedSingleton<IMatchReadModel>(MatchSources.Demo, (provider, _) => provider.GetRequiredService<DemoMatchSource>());
        services.AddKeyedSingleton<ILiveFeed>(MatchSources.Demo, (provider, _) => provider.GetRequiredService<DemoMatchSource>());
        services.AddSingleton<ISquad, DemoSquad>();
        return services;
    }

    /// <summary>
    /// Keeps teams in memory, checks names by format only, and refuses to start fan matches,
    /// until the team-builder lane's Cosmos DB store, Content Safety guard and job launcher
    /// arrive (ADR-031, ADR-032).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddDemoTeamBuilder(this IServiceCollection services)
    {
        services.TryAddSingleton<ITeamStore, InMemoryTeamStore>();
        services.TryAddSingleton<INameGuard, FormatOnlyNameGuard>();
        services.AddSingleton<IMatchLauncher, UnconnectedMatchLauncher>();
        return services;
    }
}
