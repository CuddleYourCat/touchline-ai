using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Touchline.Configuration;

/// <summary>
/// Adds Azure App Configuration to a host's configuration (ADR-062): keyless with the host's
/// managed identity, optional, refreshed every 15 minutes, and placed between
/// <c>appsettings</c> and the environment variables.
/// </summary>
public static class AppConfigurationExtensions
{
    /// <summary>The setting that holds the store's endpoint, for example <c>https://appcs-touchline-abc.azconfig.io</c>. Bicep sets it as an app setting only when the store is deployed.</summary>
    public const string EndpointKey = "AppConfiguration:Endpoint";

    /// <summary>
    /// The connection string Aspire injects for the local App Configuration emulator (<c>ConnectionStrings__appconfig</c>).
    /// It's used only when there is no endpoint, so Azure always takes the keyless path.
    /// </summary>
    public const string EmulatorConnectionStringKey = "ConnectionStrings:appconfig";

    /// <summary>
    /// The key whose change makes the provider reload every selected key-value. Changing it, after
    /// editing the others, is how a batch of settings is applied together.
    /// </summary>
    public const string SentinelKey = "Touchline:Sentinel";

    /// <summary>The setting that overrides <see cref="DefaultRefreshInterval"/>, as a time span such as <c>00:15:00</c>.</summary>
    public const string RefreshIntervalKey = "AppConfiguration:RefreshInterval";

    /// <summary>
    /// How often the sentinel and the feature flags are checked, at the earliest, unless
    /// <see cref="RefreshIntervalKey"/> says otherwise. Each check is two requests (the sentinel and the flags) and
    /// the Free tier counts both, so 15 minutes is 192 requests a day for each running instance (ADR-062).
    /// </summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(15);

    /// <summary>The shortest interval accepted, so a typo can't spend the day's requests in minutes.</summary>
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long start-up waits for the store. The default is 100 seconds, which would hold up a cold
    /// start when the store can't be reached; the app carries on with its defaults instead.
    /// </summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] _selectedKeys = ["Squad:*", "Touchline:*"];

    /// <summary>
    /// Adds the App Configuration provider when <see cref="EndpointKey"/> (or the emulator's connection string)
    /// is set, and does nothing otherwise, so an app with no store starts as it always did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order:</b> the provider is inserted just before the environment variables, so the order, lowest first, is
    /// <c>appsettings</c>, user secrets, App Configuration, environment variables, command line. An environment
    /// variable always wins, which is what Bicep and an emergency <c>az containerapp update</c> rely on.
    /// </para>
    /// <para>
    /// <b>Optional:</b> a store that is down, throttled (HTTP 429) or missing a role leaves the app on its
    /// <c>appsettings</c> values, and the provider keeps its last good values once it has loaded.
    /// </para>
    /// </remarks>
    /// <param name="configuration">The host's configuration.</param>
    /// <returns>True if the provider was added, so the host should add its refresh middleware.</returns>
    public static bool AddTouchlineAppConfiguration(this IConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var source = CreateSource(configuration);
        if (source is null)
        {
            return false;
        }

        InsertBeforeEnvironmentVariables(configuration.Sources, source);
        return true;
    }

    /// <summary>Builds the provider's configuration source, or null when no store is configured.</summary>
    /// <param name="settings">The configuration to read the endpoint from.</param>
    /// <returns>The source, not yet added to any builder.</returns>
    internal static IConfigurationSource? CreateSource(IConfiguration settings)
    {
        var endpoint = settings[EndpointKey];
        var emulator = settings[EmulatorConnectionStringKey];
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(emulator))
        {
            return null;
        }

        // A throwaway builder: the source is built once, in the host's builder, where it's inserted.
        var scratch = new ConfigurationBuilder();
        scratch.AddAzureAppConfiguration(
            options =>
            {
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    options.Connect(new Uri(endpoint), Credential(settings));
                }
                else
                {
                    options.Connect(emulator);
                }

                Configure(options, RefreshIntervalFrom(settings));
            },
            optional: true);
        return scratch.Sources.Single();
    }

    /// <summary>The key filters, refresh and feature flag settings, shared with the tests.</summary>
    /// <param name="options">The provider's options.</param>
    /// <param name="refreshInterval">How often to check the sentinel and the flags.</param>
    internal static void Configure(AzureAppConfigurationOptions options, TimeSpan refreshInterval)
    {
        foreach (var key in _selectedKeys)
        {
            options.Select(key);
        }

        options
            .ConfigureStartupOptions(startup => startup.Timeout = StartupTimeout)
            .ConfigureRefresh(refresh => refresh
                .Register(SentinelKey, refreshAll: true)
                .SetRefreshInterval(refreshInterval))
            .UseFeatureFlags(flags => flags.SetRefreshInterval(refreshInterval));
    }

    /// <summary>The refresh interval from <see cref="RefreshIntervalKey"/>, or the default when it's missing, invalid or too short.</summary>
    /// <param name="settings">The configuration.</param>
    /// <returns>The interval to use.</returns>
    internal static TimeSpan RefreshIntervalFrom(IConfiguration settings) =>
        TimeSpan.TryParse(settings[RefreshIntervalKey], System.Globalization.CultureInfo.InvariantCulture, out var interval) && interval >= MinimumRefreshInterval
            ? interval
            : DefaultRefreshInterval;

    /// <summary>
    /// Puts <paramref name="source"/> just before the app's own environment variables, or last if there are none.
    /// </summary>
    /// <remarks>
    /// A real host has several environment variable sources: the prefixed ones (<c>DOTNET_</c>, <c>ASPNETCORE_</c>) come
    /// first, before the JSON files. Only the one with no prefix holds the app's settings, so it is the last one with an empty
    /// prefix that the store goes before.
    /// </remarks>
    /// <param name="sources">The builder's sources, lowest priority first.</param>
    /// <param name="source">The source to place.</param>
    internal static void InsertBeforeEnvironmentVariables(IList<IConfigurationSource> sources, IConfigurationSource source)
    {
        var index = sources.ToList().FindLastIndex(s => s is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        sources.Insert(index < 0 ? sources.Count : index, source);
    }

    private static TokenCredential Credential(IConfiguration settings)
    {
        // The same pattern as the squad's clients: the configured user-assigned identity, or whatever
        // AZURE_CLIENT_ID names, or the developer's sign-in locally.
        var options = new DefaultAzureCredentialOptions();
        if (settings["Squad:ManagedIdentityClientId"] is { Length: > 0 } clientId)
        {
            options.ManagedIdentityClientId = clientId;
        }

        return new DefaultAzureCredential(options);
    }
}
