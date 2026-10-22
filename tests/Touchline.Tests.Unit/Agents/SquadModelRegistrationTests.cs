using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Touchline.Agents;

namespace Touchline.Tests.Unit.Agents;

[TestFixture]
public sealed class SquadModelRegistrationTests
{
    private static readonly Dictionary<string, string?> _validSettings = new()
    {
        ["Squad:FoundryEndpoint"] = "https://touchline-test.services.ai.azure.com/",
        ["Squad:Deployments:Small"] = "gpt-5.4-mini",
        ["Squad:Deployments:Large"] = "gpt-5.5",
    };

    [TestCase(SquadModels.Small)]
    [TestCase(SquadModels.Large)]
    public void Each_keyed_client_is_wrapped_in_the_budget(string size)
    {
        using var provider = Build(_validSettings);

        var client = provider.GetRequiredKeyedService<IChatClient>(size);

        client.GetService<TokenBudgetChatClient>().Should().NotBeNull();
    }

    [Test]
    public void Defaults_apply_when_only_the_required_settings_are_given()
    {
        using var provider = Build(_validSettings);

        var options = provider.GetRequiredService<IOptions<SquadOptions>>().Value;

        options.Languages.Should().Equal("en", "es", "fr");
        options.MaxInputTokens.Should().Be(4000);
        options.MaxOutputTokens.Commentator.Should().Be(150);
        options.MaxOutputTokens.Producer.Should().Be(1200);
    }

    [Test]
    public void Configured_languages_replace_the_defaults()
    {
        using var provider = Build(new Dictionary<string, string?>(_validSettings) { ["Squad:Languages:0"] = "es" });

        provider.GetRequiredService<IOptions<SquadOptions>>().Value.Languages.Should().Equal("es");
    }

    [Test]
    public void Missing_settings_fail_validation()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Squad:MaxOutputTokens:Analyst"] = "0" });

        var read = () => provider.GetRequiredService<IOptions<SquadOptions>>().Value;

        read.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("FoundryEndpoint"))
            .And.Contain(f => f.Contains("Deployments:Small"))
            .And.Contain(f => f.Contains("MaxOutputTokens:Analyst"));
    }

    [Test]
    public void The_configured_managed_identity_is_used_by_the_fallback_credential()
    {
        using var provider = Build(new Dictionary<string, string?>(_validSettings)
        {
            ["Squad:ManagedIdentityClientId"] = "00000000-0000-0000-0000-0000000000aa"
        });

        var options = provider.GetRequiredService<IOptions<SquadOptions>>().Value;

        SquadModelServiceCollectionExtensions.CredentialOptions(options).ManagedIdentityClientId
            .Should().Be("00000000-0000-0000-0000-0000000000aa");
    }

    [TestCase("https://acct.services.ai.azure.com/", "https://acct.services.ai.azure.com/openai/v1/")]
    [TestCase("https://acct.openai.azure.com", "https://acct.openai.azure.com/openai/v1/")]
    public void The_v1_endpoint_hangs_off_the_foundry_endpoint(string endpoint, string expected) =>
        SquadModelServiceCollectionExtensions.V1Endpoint(new Uri(endpoint)).Should().Be(new Uri(expected));

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddSquadModels(configuration).BuildServiceProvider();
    }
}
