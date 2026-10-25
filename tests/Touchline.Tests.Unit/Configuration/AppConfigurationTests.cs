extern alias processor;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using processor::Touchline.EventProcessor.Configuration;
using Touchline.Agents;
using Touchline.Configuration;

namespace Touchline.Tests.Unit.Configuration;

/// <summary>Provider order, defaults, live changes and starting with no store (ADR-062).</summary>
[TestFixture]
public sealed class AppConfigurationTests
{
    private const string _variable = "Touchline__OrderTest";
    private const string _key = "Touchline:OrderTest";

    private static ConfigurationManager Layered(string appsettings, string? store, string? environment)
    {
        // The sources of a default host, lowest first, with the store inserted as the real code does.
        Environment.SetEnvironmentVariable(_variable, environment);
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [_key] = appsettings });
        configuration.AddEnvironmentVariables();
        AppConfigurationExtensions.InsertBeforeEnvironmentVariables(
            configuration.Sources,
            new MemoryConfigurationSource { InitialData = store is null ? [] : new Dictionary<string, string?> { [_key] = store } });
        return configuration;
    }

    [TearDown]
    public void ClearEnvironment() => Environment.SetEnvironmentVariable(_variable, null);

    [Test]
    public void The_store_beats_appsettings()
    {
        Layered("appsettings", "store", environment: null)[_key].Should().Be("store");
    }

    [Test]
    public void An_environment_variable_beats_the_store_and_appsettings()
    {
        var configuration = Layered("appsettings", "store", environment: "environment");

        configuration[_key].Should().Be("environment");
        configuration.Sources.Select(s => s.GetType()).Should().Equal(
            typeof(MemoryConfigurationSource),
            typeof(MemoryConfigurationSource),
            typeof(MemoryConfigurationSource), // the manager's own writable layer
            typeof(EnvironmentVariablesConfigurationSource));
    }

    [Test]
    public void A_value_only_in_appsettings_survives_a_store_without_it()
    {
        Layered("appsettings", store: null, environment: null)[_key].Should().Be("appsettings");
    }

    [Test]
    public void With_no_environment_source_the_store_goes_last()
    {
        var sources = new List<IConfigurationSource> { new MemoryConfigurationSource(), new MemoryConfigurationSource() };
        var store = new MemoryConfigurationSource();

        AppConfigurationExtensions.InsertBeforeEnvironmentVariables(sources, store);

        sources[^1].Should().BeSameAs(store);
    }

    [Test]
    public void With_no_endpoint_nothing_is_added()
    {
        var configuration = new ConfigurationManager();
        var before = configuration.Sources.Count;

        configuration.AddTouchlineAppConfiguration().Should().BeFalse();

        configuration.Sources.Should().HaveCount(before);
    }

    [Test]
    public void With_an_endpoint_a_source_is_made_and_nothing_connects_until_it_is_built()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConfigurationExtensions.EndpointKey] = "https://appcs-test.azconfig.io"
        }).Build();

        AppConfigurationExtensions.CreateSource(settings).Should().NotBeNull();
    }

    [Test]
    public void The_refresh_interval_is_15_minutes_and_start_up_does_not_wait_100_seconds()
    {
        AppConfigurationExtensions.DefaultRefreshInterval.Should().Be(TimeSpan.FromMinutes(15));
        AppConfigurationExtensions.StartupTimeout.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [TestCase(null, "00:15:00")]
    [TestCase("00:30:00", "00:30:00")]
    [TestCase("00:00:05", "00:15:00")]
    [TestCase("not a time", "00:15:00")]
    public void The_refresh_interval_can_be_set_but_not_below_a_minute(string? setting, string expected)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConfigurationExtensions.RefreshIntervalKey] = setting
        }).Build();

        AppConfigurationExtensions.RefreshIntervalFrom(settings).Should().Be(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public void The_store_goes_after_every_file_and_prefixed_source_and_before_the_environment_in_a_web_host()
    {
        var builder = WebApplication.CreateBuilder();

        AssertStoreOrder(builder.Configuration);
    }

    [Test]
    public void The_store_goes_after_every_file_and_prefixed_source_and_before_the_environment_in_a_functions_host()
    {
        var builder = Microsoft.Azure.Functions.Worker.Builder.FunctionsApplication.CreateBuilder([]);

        AssertStoreOrder(builder.Configuration);
    }

    private static void AssertStoreOrder(ConfigurationManager configuration)
    {
        var store = new MemoryConfigurationSource();

        AppConfigurationExtensions.InsertBeforeEnvironmentVariables(configuration.Sources, store);

        var sources = configuration.Sources.ToList();
        var at = sources.IndexOf(store);
        var unprefixed = sources.FindLastIndex(s => s is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        unprefixed.Should().BeGreaterThan(at, "the app's own environment variables beat the store");
        sources.Where(s => s is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource || s is EnvironmentVariablesConfigurationSource { Prefix.Length: > 0 })
            .Should().OnlyContain(s => sources.IndexOf(s) < at, "appsettings and the DOTNET_/ASPNETCORE_ sources are lower than the store");
        sources.Should().Contain(s => s is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource, "a real host has appsettings sources");
    }

    [Test]
    public async Task A_flag_in_the_store_beats_the_default_in_appsettings_and_the_default_applies_without_one()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeatureManagement:FanQa"] = "true",
            ["FeatureManagement:FanMatches"] = "true"
        });
        // The shape the App Configuration provider writes for a key-value feature flag.
        AppConfigurationExtensions.InsertBeforeEnvironmentVariables(
            configuration.Sources,
            new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["feature_management:feature_flags:0:id"] = "FanQa",
                    ["feature_management:feature_flags:0:enabled"] = "false"
                }
            });
        using var provider = Services(configuration);
        var features = provider.GetRequiredService<IFeatureManager>();

        (await features.IsEnabledAsync(FeatureNames.FanQa)).Should().BeFalse("the store switched it off");
        (await features.IsEnabledAsync(FeatureNames.FanMatches)).Should().BeTrue("the store says nothing, so the appsettings default stands");
    }

    [Test]
    public async Task An_environment_style_FeatureManagement_value_does_not_beat_a_flag_from_the_store()
    {
        // What the library does with both schemas: the Microsoft schema (what the store writes) wins over the
        // older FeatureManagement section, wherever the sources sit. So this is NOT a way to hold a flag off.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["feature_management:feature_flags:0:id"] = "FanQa",
            ["feature_management:feature_flags:0:enabled"] = "true"
        });
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureManagement:FanQa"] = "false" });
        using var provider = Services(configuration);

        var enabled = await provider.GetRequiredService<Microsoft.FeatureManagement.IFeatureManager>().IsEnabledAsync(FeatureNames.FanQa);

        enabled.Should().BeTrue();
    }

    [TestCase(FeatureNames.FanMatches)]
    [TestCase(FeatureNames.FanQa)]
    [TestCase(FeatureNames.PossessionSearch)]
    [TestCase(FeatureNames.SquadModels)]
    public async Task A_Touchline_Kill_setting_holds_a_flag_off_whatever_the_store_says(string flag)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["feature_management:feature_flags:0:id"] = flag,
            ["feature_management:feature_flags:0:enabled"] = "true",
            ["feature_management:feature_flags:1:id"] = "Other",
            ["feature_management:feature_flags:1:enabled"] = "true"
        });
        // What Touchline__Kill__<Flag>=true becomes as an environment variable.
        Environment.SetEnvironmentVariable($"Touchline__Kill__{flag}", "true");
        try
        {
            configuration.AddEnvironmentVariables();
            using var provider = Services(configuration);
            var features = provider.GetRequiredService<IFeatureManager>();

            (await features.IsEnabledAsync(flag)).Should().BeFalse();
            (await features.IsEnabledAsync("Other")).Should().BeTrue("only the named flag is held");
            (await provider.GetRequiredService<IVariantFeatureManager>().IsEnabledAsync(flag, CancellationToken.None)).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable($"Touchline__Kill__{flag}", null);
        }
    }

    [Test]
    public async Task A_Touchline_Kill_setting_of_false_leaves_the_flag_to_the_store()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["feature_management:feature_flags:0:id"] = "FanQa",
            ["feature_management:feature_flags:0:enabled"] = "true",
            ["Touchline:Kill:FanQa"] = "false"
        });
        using var provider = Services(configuration);

        (await provider.GetRequiredService<IFeatureManager>().IsEnabledAsync(FeatureNames.FanQa)).Should().BeTrue();
    }

    [Test]
    public async Task A_flag_change_applies_on_the_next_check_without_a_restart()
    {
        var store = new ReloadableSource();
        store.Provider.Data["feature_management:feature_flags:0:id"] = "FanQa";
        store.Provider.Data["feature_management:feature_flags:0:enabled"] = "true";
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(store);
        using var provider = Services(configuration);
        var features = provider.GetRequiredService<IFeatureManager>();

        (await features.IsEnabledAsync(FeatureNames.FanQa)).Should().BeTrue();
        store.Provider.Change("feature_management:feature_flags:0:enabled", "false");
        (await features.IsEnabledAsync(FeatureNames.FanQa)).Should().BeFalse();
        store.Provider.Change("feature_management:feature_flags:0:enabled", "true");
        (await features.IsEnabledAsync(FeatureNames.FanQa)).Should().BeTrue();
    }

    [Test]
    public async Task The_squad_gate_follows_the_SquadModels_and_PossessionSearch_flags()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeatureManagement:SquadModels"] = "false",
            ["FeatureManagement:PossessionSearch"] = "true"
        });
        using var provider = Services(configuration);
        var gate = provider.GetRequiredService<ISquadFeatureGate>();

        (await gate.ModelsEnabledAsync(CancellationToken.None)).Should().BeFalse();
        (await gate.PossessionSearchEnabledAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Test]
    public void Squad_options_follow_a_change_and_a_bad_change_keeps_the_last_good_value()
    {
        var store = new ReloadableSource();
        store.Provider.Data["Squad:FoundryEndpoint"] = "https://touchline-test.services.ai.azure.com/";
        store.Provider.Data["Squad:Deployments:Small"] = "small";
        store.Provider.Data["Squad:Deployments:Large"] = "large";
        store.Provider.Data["Squad:MaxOutputTokens:Analyst"] = "600";
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(store);
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddSquadModels(configuration);
        services.AddTouchlineFeatureFlags();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SquadOptions>>();

        options.Value.MaxOutputTokens.Analyst.Should().Be(600);
        store.Provider.Change("Squad:MaxOutputTokens:Analyst", "900");
        options.Value.MaxOutputTokens.Analyst.Should().Be(900, "token caps apply live");

        store.Provider.Change("Squad:MaxOutputTokens:Analyst", "0"); // fails validation
        options.Value.MaxOutputTokens.Analyst.Should().Be(900, "a bad edit leaves the last good settings in place");
    }

    [Test]
    public async Task The_processor_starts_with_no_store_and_has_its_gate_and_flags()
    {
        var builder = Microsoft.Azure.Functions.Worker.Builder.FunctionsApplication.CreateBuilder([]);

        builder.AddProcessorAppConfiguration();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<ISquadFeatureGate>().Should().NotBeNull();
        (await provider.GetRequiredService<IFeatureManager>().IsEnabledAsync(FeatureNames.SquadModels))
            .Should().BeTrue("the processor's appsettings default is on");
    }

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(configuration);
        services.AddTouchlineFeatureFlags();
        return services.BuildServiceProvider();
    }

}
