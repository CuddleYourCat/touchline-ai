using Touchline.Domain.Squad;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// Registers the demo match behind the Match Centre's ports.
/// </summary>
internal static class DemoServices
{
    /// <summary>
    /// Serves the recorded match as the read model, the live feed and the squad.
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
        services.AddSingleton<IMatchReadModel>(provider => provider.GetRequiredService<DemoMatchSource>());
        services.AddSingleton<ILiveFeed>(provider => provider.GetRequiredService<DemoMatchSource>());
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
        services.AddSingleton<ITeamStore, InMemoryTeamStore>();
        services.AddSingleton<INameGuard, FormatOnlyNameGuard>();
        services.AddSingleton<IMatchLauncher, UnconnectedMatchLauncher>();
        return services;
    }
}
