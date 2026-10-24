using Touchline.Agents;
using Touchline.Domain.Squad;
using Touchline.Search;

namespace Touchline.MatchCentre.Squad;

/// <summary>
/// Registers the Analyst behind the Q&amp;A endpoint (ADR-030).
/// </summary>
public static class AnalystServices
{
    /// <summary>
    /// Adds <see cref="IMatchFacts"/> over the registered read model, the <see cref="RecapReader"/>
    /// behind the recap endpoint, and, when Foundry is
    /// configured, the Analyst in place of the stand-in squad (<see cref="AnalystServiceCollectionExtensions.AddAnalystQa"/>).
    /// Call it after the read model and the stand-in squad are registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Squad</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddMatchCentreAnalyst(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IMatchFacts, MatchFactsAdapter>();
        services.AddSingleton<RecapReader>();
        services.AddPossessionSearch(configuration);
        return services.AddAnalystQa(configuration);
    }
}
