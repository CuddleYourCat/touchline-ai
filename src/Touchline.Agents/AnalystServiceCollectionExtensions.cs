using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Domain.Squad;

namespace Touchline.Agents;

/// <summary>
/// Registers the Analyst for fan Q&amp;A (ADR-030) in a host that already has an <see cref="ISquad"/>.
/// </summary>
public static class AnalystServiceCollectionExtensions
{
    /// <summary>
    /// When <c>Squad:FoundryEndpoint</c> is set, adds the squad's models (<see cref="SquadModelServiceCollectionExtensions.AddSquadModels"/>)
    /// and replaces the registered <see cref="ISquad"/> with <see cref="AnalystSquad"/>, which
    /// answers questions itself and hands recaps to the squad it replaced (a decorator). When the
    /// endpoint isn't set, nothing changes, so the host's stand-in squad stays in use. A second call
    /// does nothing, so the Analyst is decorated once.
    /// </summary>
    /// <remarks>
    /// Call it after the host registers its <see cref="ISquad"/> and an <see cref="IMatchFacts"/>.
    /// <see cref="IPossessionSearch"/> is optional: without it the Analyst has no search tool.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the <c>Squad</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">More than one <see cref="ISquad"/> is registered.</exception>
    public static IServiceCollection AddAnalystQa(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration[$"{SquadOptions.SectionName}:{nameof(SquadOptions.FoundryEndpoint)}"]))
        {
            return services;
        }

        if (services.Any(d => d.ServiceType == typeof(AnalystSquadMarker)))
        {
            // Already decorated: a second call must not wrap the Analyst in another Analyst.
            return services;
        }

        List<ServiceDescriptor> plain = [.. services.Where(d => d.ServiceType == typeof(ISquad) && !d.IsKeyedService)];
        if (plain.Count > 1)
        {
            throw new InvalidOperationException(
                $"AddAnalystQa found {plain.Count} ISquad registrations and can decorate only one. Register a single ISquad before calling it.");
        }

        services.AddSquadModels(configuration);
        services.AddSingleton<AnalystSquadMarker>();

        var replaced = plain.SingleOrDefault();
        if (replaced is not null)
        {
            services.Remove(replaced);
        }

        services.AddSingleton<ISquad>(provider => new AnalystSquad(
            provider.GetRequiredKeyedService<IChatClient>(SquadModels.Large),
            provider.GetRequiredService<IMatchFacts>(),
            provider.GetRequiredService<IOptions<SquadOptions>>(),
            provider.GetRequiredService<SquadMetrics>(),
            provider.GetRequiredService<ILogger<AnalystSquad>>(),
            provider.GetService<IPossessionSearch>(),
            replaced is null ? null : Resolve(provider, replaced),
            provider.GetService<ISquadFeatureGate>()));
        return services;
    }

    private static ISquad Resolve(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor switch
        {
            { ImplementationInstance: ISquad instance } => instance,
            { ImplementationFactory: { } factory } => (ISquad)factory(provider),
            { ImplementationType: { } type } => (ISquad)ActivatorUtilities.CreateInstance(provider, type),
            _ => throw new InvalidOperationException("The replaced ISquad registration can't be resolved.")
        };

    /// <summary>Marks a service collection whose <see cref="ISquad"/> is already the Analyst, so it's decorated once.</summary>
    private sealed class AnalystSquadMarker;
}
