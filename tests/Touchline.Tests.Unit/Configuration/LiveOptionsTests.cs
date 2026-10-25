using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Touchline.Agents;
using Touchline.Configuration;
using Touchline.MatchCentre.Configuration;

namespace Touchline.Tests.Unit.Configuration;

/// <summary>A bad value in the store never stops the app or breaks a refresh (ADR-062).</summary>
[TestFixture]
public sealed class LiveOptionsTests
{
    private static readonly Dictionary<string, string?> _good = new()
    {
        ["Squad:FoundryEndpoint"] = "https://touchline-test.services.ai.azure.com/",
        ["Squad:Deployments:Small"] = "small",
        ["Squad:Deployments:Large"] = "large",
        ["Squad:MaxOutputTokens:Analyst"] = "600",
        ["Squad:MaxInputTokens"] = "4000"
    };

    private ReloadableSource _store = null!;

    [SetUp]
    public void SetUp() => _store = new ReloadableSource();

    private ServiceProvider Start(IDictionary<string, string?> appsettings, IDictionary<string, string?>? store = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(appsettings);
        foreach (var (key, value) in store ?? new Dictionary<string, string?>())
        {
            _store.Provider.Data[key] = value;
        }

        ((IConfigurationBuilder)configuration).Add(_store);
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddSquadModels(configuration);
        services.AddTouchlineFeatureFlags();
        return services.BuildServiceProvider();
    }

    [TestCase("Squad:MaxInputTokens", "abc", TestName = "A_binder_failure_in_the_store_at_start_up_falls_back_to_the_configuration_without_it")]
    [TestCase("Squad:MaxOutputTokens:Analyst", "0", TestName = "A_validation_failure_in_the_store_at_start_up_falls_back_to_the_configuration_without_it")]
    public void A_bad_stored_value_at_start_up_falls_back_to_the_configuration_without_the_store(string key, string bad)
    {
        using var provider = Start(_good, new Dictionary<string, string?> { [key] = bad });

        var options = provider.GetRequiredService<IOptions<SquadOptions>>().Value;

        options.MaxInputTokens.Should().Be(4000);
        options.MaxOutputTokens.Analyst.Should().Be(600);
    }

    [Test]
    public void A_bad_stored_value_does_not_stop_ValidateOnStart()
    {
        using var provider = Start(_good, new Dictionary<string, string?> { ["Squad:MaxOutputTokens:Analyst"] = "0" });

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
    }

    [Test]
    public void A_bad_value_that_is_not_from_the_store_still_stops_the_host()
    {
        var bad = new Dictionary<string, string?>(_good) { ["Squad:MaxOutputTokens:Analyst"] = "0" };
        using var provider = Start(bad);

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>();
    }

    [Test]
    public void When_the_fallback_fails_too_the_start_up_error_reaches_the_host()
    {
        var bad = new Dictionary<string, string?>(_good) { ["Squad:MaxInputTokens"] = "abc" };
        using var provider = Start(bad, new Dictionary<string, string?> { ["Squad:MaxInputTokens"] = "def" });

        var resolve = () => provider.GetRequiredService<IOptions<SquadOptions>>();

        resolve.Should().Throw<Exception>();
    }

    [TestCase("Squad:MaxInputTokens", "abc", TestName = "A_binder_failure_at_refresh_keeps_the_last_good_options_and_does_not_throw_into_the_provider")]
    [TestCase("Squad:MaxOutputTokens:Analyst", "0", TestName = "A_validation_failure_at_refresh_keeps_the_last_good_options_and_does_not_throw_into_the_provider")]
    public void A_bad_stored_value_at_refresh_keeps_the_last_good_options(string key, string bad)
    {
        using var provider = Start(_good, new Dictionary<string, string?> { ["Squad:MaxOutputTokens:Analyst"] = "700" });
        var options = provider.GetRequiredService<IOptions<SquadOptions>>();
        var monitor = provider.GetRequiredService<IOptionsMonitor<SquadOptions>>();
        options.Value.MaxOutputTokens.Analyst.Should().Be(700);

        var change = () => _store.Provider.Change(key, bad);

        change.Should().NotThrow();
        options.Value.MaxOutputTokens.Analyst.Should().Be(700);
        monitor.CurrentValue.Should().BeSameAs(options.Value);

        _store.Provider.Data["Squad:MaxInputTokens"] = "5000";
        _store.Provider.Change("Squad:MaxOutputTokens:Analyst", "800");
        options.Value.MaxOutputTokens.Analyst.Should().Be(800, "a later good value is picked up again");
    }

    [TestCase("/api/matches", true)]
    [TestCase("/API/status", true)]
    [TestCase("/api/matches/demo-1/questions", true)]
    [TestCase("/api/matches/demo-1/stream", false)]
    [TestCase("/alive", false)]
    [TestCase("/health", false)]
    [TestCase("/index.html", false)]
    [TestCase("/", false)]
    public void Only_api_calls_other_than_the_stream_trigger_a_refresh(string path, bool expected)
    {
        MatchCentreAppConfiguration.TriggersRefresh(new Microsoft.AspNetCore.Http.PathString(path)).Should().Be(expected);
    }
}
