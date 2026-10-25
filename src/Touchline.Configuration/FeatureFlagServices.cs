using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.FeatureManagement;
using Touchline.Agents;

namespace Touchline.Configuration;

/// <summary>Registers feature management and the live squad options (ADR-062).</summary>
public static class FeatureFlagServices
{
    /// <summary>
    /// Adds <c>Microsoft.FeatureManagement</c>, the <see cref="ISquadFeatureGate"/> behind the
    /// <see cref="FeatureNames.SquadModels"/> and <see cref="FeatureNames.PossessionSearch"/> flags, and makes
    /// <see cref="IOptions{TOptions}"/> and <see cref="IOptionsMonitor{TOptions}"/> of <see cref="SquadOptions"/>
    /// follow configuration changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flags are evaluated on each call, never cached at start-up, so a change applies once the provider has
    /// refreshed. Deployment names and the Foundry endpoint are read when the model clients are created, so a
    /// change to them needs a restart. Output caps, the input cap and the languages apply live.
    /// </para>
    /// <para>
    /// A bad value in the store never takes the app down. At start-up, if the squad's options fail to bind or validate with
    /// the store's values, the app logs an error and uses the configuration without the store. After start-up, a bad
    /// change is logged and the last good options stay.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddTouchlineFeatureFlags(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddFeatureManagement();
        DecorateWithKillSwitch<IFeatureManager>(services);
        DecorateWithKillSwitch<IVariantFeatureManager>(services);
        services.TryAddSingleton<ISquadFeatureGate, FeatureFlagSquadGate>();

        // Closed registrations beat the open IOptions<> and IOptionsMonitor<> ones whatever the order, so every
        // consumer reads the current value on each call. The monitor matters because ValidateOnStart reads it: with
        // the stock monitor, a bad stored value would still stop the host before the fallback could apply.
        services.AddSingleton(provider => new LiveOptions<SquadOptions>(
            provider.GetRequiredService<IOptionsFactory<SquadOptions>>(),
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Touchline.Configuration.SquadOptions"),
            storeless => new OptionsFactory<SquadOptions>(
                [new ConfigureFromConfigurationOptions<SquadOptions>(storeless.GetSection(SquadOptions.SectionName))],
                provider.GetServices<IPostConfigureOptions<SquadOptions>>(),
                provider.GetServices<IValidateOptions<SquadOptions>>())));
        services.AddSingleton<IOptions<SquadOptions>>(provider => provider.GetRequiredService<LiveOptions<SquadOptions>>());
        services.AddSingleton<IOptionsMonitor<SquadOptions>>(provider => provider.GetRequiredService<LiveOptions<SquadOptions>>());
        return services;
    }

    /// <summary>The setting prefix of a hold-fast override: <c>Touchline:Kill:FanQa=true</c> (environment variable <c>Touchline__Kill__FanQa</c>) switches that flag off.</summary>
    public const string KillSettingPrefix = "Touchline:Kill:";

    private static void DecorateWithKillSwitch<TService>(IServiceCollection services)
        where TService : class
    {
        var original = services.Last(d => d.ServiceType == typeof(TService) && !d.IsKeyedService);
        services.Remove(original);
        services.AddSingleton(provider => (TService)(object)KillSwitchFeatureManager.Wrap(
            Resolve(provider, original), provider.GetRequiredService<IConfiguration>()));
    }

    private static object Resolve(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor switch
        {
            { ImplementationInstance: { } instance } => instance,
            { ImplementationFactory: { } factory } => factory(provider),
            { ImplementationType: { } type } => ActivatorUtilities.CreateInstance(provider, type),
            _ => throw new InvalidOperationException("The feature manager registration can't be resolved.")
        };
}

/// <summary>
/// Puts a hold-fast override in front of the feature manager. A setting <c>Touchline:Kill:&lt;Flag&gt;</c> set to
/// <c>true</c> turns that flag off whatever the store says.
/// </summary>
/// <remarks>
/// An environment variable <c>FeatureManagement__FanQa=false</c> does not do this job: the store writes flags in the
/// Microsoft schema (<c>feature_management:feature_flags</c>), and the library prefers that over the older
/// <c>FeatureManagement</c> section, so the store's flag wins. This override is read before the feature manager.
/// </remarks>
internal sealed class KillSwitchFeatureManager(IFeatureManager inner, IConfiguration configuration) : IFeatureManager, IVariantFeatureManager
{
    public static object Wrap(object inner, IConfiguration configuration) =>
        new KillSwitchFeatureManager((IFeatureManager)inner, configuration);

    public bool IsKilled(string feature) =>
        bool.TryParse(configuration[FeatureFlagServices.KillSettingPrefix + feature], out var killed) && killed;

    public IAsyncEnumerable<string> GetFeatureNamesAsync() => inner.GetFeatureNamesAsync();

    public IAsyncEnumerable<string> GetFeatureNamesAsync(CancellationToken cancellationToken) =>
        ((IVariantFeatureManager)inner).GetFeatureNamesAsync(cancellationToken);

    public Task<bool> IsEnabledAsync(string feature) =>
        IsKilled(feature) ? Task.FromResult(false) : inner.IsEnabledAsync(feature);

    public Task<bool> IsEnabledAsync<TContext>(string feature, TContext appContext) =>
        IsKilled(feature) ? Task.FromResult(false) : inner.IsEnabledAsync(feature, appContext);

    public ValueTask<bool> IsEnabledAsync(string feature, CancellationToken cancellationToken) =>
        IsKilled(feature) ? ValueTask.FromResult(false) : ((IVariantFeatureManager)inner).IsEnabledAsync(feature, cancellationToken);

    public ValueTask<bool> IsEnabledAsync<TContext>(string feature, TContext appContext, CancellationToken cancellationToken) =>
        IsKilled(feature) ? ValueTask.FromResult(false) : ((IVariantFeatureManager)inner).IsEnabledAsync(feature, appContext, cancellationToken);

    public ValueTask<Variant> GetVariantAsync(string feature, CancellationToken cancellationToken) =>
        ((IVariantFeatureManager)inner).GetVariantAsync(feature, cancellationToken);

    public ValueTask<Variant> GetVariantAsync(string feature, Microsoft.FeatureManagement.FeatureFilters.ITargetingContext context, CancellationToken cancellationToken) =>
        ((IVariantFeatureManager)inner).GetVariantAsync(feature, context, cancellationToken);
}

/// <summary>The squad's gate, backed by feature flags evaluated on every call.</summary>
internal sealed class FeatureFlagSquadGate(IFeatureManager features) : ISquadFeatureGate
{
    public ValueTask<bool> ModelsEnabledAsync(CancellationToken cancellationToken) =>
        new(features.IsEnabledAsync(FeatureNames.SquadModels, cancellationToken));

    public ValueTask<bool> PossessionSearchEnabledAsync(CancellationToken cancellationToken) =>
        new(features.IsEnabledAsync(FeatureNames.PossessionSearch, cancellationToken));
}

/// <summary>Marks a configuration provider as the App Configuration store, for the start-up fallback and for tests.</summary>
internal interface IStoreConfigurationProvider;

/// <summary>
/// Options that are rebuilt, with their validators, whenever configuration reloads, and that survive a bad value in the store.
/// It stands in for <see cref="IOptions{TOptions}"/> and <see cref="IOptionsMonitor{TOptions}"/>.
/// </summary>
/// <remarks>
/// The stock monitor validates inside the provider's reload callback, so a bad value would throw back into the provider's
/// refresh. Here every failure (a binder error, a validation failure) is caught and the last good value stays. At start-up,
/// if the first build fails, the options are built again from the configuration without the store's provider; only if that
/// fails too does the exception reach the host.
/// </remarks>
internal sealed class LiveOptions<TOptions> : IOptions<TOptions>, IOptionsMonitor<TOptions>, IDisposable
    where TOptions : class
{
    private readonly IOptionsFactory<TOptions> _factory;
    private readonly ILogger _logger;
    private readonly IDisposable _subscription;
    private readonly List<Action<TOptions, string?>> _listeners = [];
    private volatile TOptions _current;

    public LiveOptions(
        IOptionsFactory<TOptions> factory,
        IConfiguration configuration,
        ILogger logger,
        Func<IConfiguration, IOptionsFactory<TOptions>> storelessFactory)
    {
        _factory = factory;
        _logger = logger;
        _current = Initial(factory, configuration, logger, storelessFactory);
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, Rebuild);
    }

    public TOptions Value => _current;

    public TOptions CurrentValue => _current;

    public TOptions Get(string? name) => _current;

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        lock (_listeners)
        {
            _listeners.Add(listener);
        }

        return new Subscription(this, listener);
    }

    public void Dispose() => _subscription.Dispose();

    private static TOptions Initial(
        IOptionsFactory<TOptions> factory,
        IConfiguration configuration,
        ILogger logger,
        Func<IConfiguration, IOptionsFactory<TOptions>> storelessFactory)
    {
        try
        {
            return factory.Create(Options.DefaultName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && configuration is IConfigurationRoot root)
        {
            var withoutStore = root.Providers.Where(p => !IsStore(p)).ToList();
            if (withoutStore.Count == root.Providers.Count())
            {
                throw; // there is no store in play, so there is nothing to fall back from
            }

            logger.LogError(ex, "The {Options} settings failed to bind or validate with the App Configuration store's values, so the store is ignored until it changes.", typeof(TOptions).Name);
            return storelessFactory(new ConfigurationRoot(withoutStore)).Create(Options.DefaultName);
        }
    }

    private static bool IsStore(IConfigurationProvider provider) =>
        provider is IStoreConfigurationProvider
        || provider.GetType().FullName?.StartsWith("Microsoft.Extensions.Configuration.AzureAppConfiguration.", StringComparison.Ordinal) == true;

    private void Rebuild()
    {
        TOptions rebuilt;
        try
        {
            rebuilt = _factory.Create(Options.DefaultName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A changed {Options} value failed to bind or validate, so the last good value stays in use.", typeof(TOptions).Name);
            return;
        }

        _current = rebuilt;
        Action<TOptions, string?>[] listeners;
        lock (_listeners)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(rebuilt, Options.DefaultName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "A listener for changed {Options} threw.", typeof(TOptions).Name);
            }
        }
    }

    private sealed class Subscription(LiveOptions<TOptions> owner, Action<TOptions, string?> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._listeners)
            {
                owner._listeners.Remove(listener);
            }
        }
    }
}
