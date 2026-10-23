using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Agents;
using Touchline.Domain.Squad;
using Touchline.MatchCentre.Demo;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class AnalystWiringTests
{
    [Test]
    public async Task Without_Foundry_the_demo_squad_answers_and_the_match_facts_are_still_registered()
    {
        await using var factory = MatchCentreFactory.Live();

        factory.Services.GetRequiredService<ISquad>().Should().BeOfType<DemoSquad>();
        factory.Services.GetRequiredService<IMatchFacts>().Should().NotBeNull();
    }

    [Test]
    public async Task With_Foundry_configured_the_Analyst_answers_and_recaps_stay_with_the_demo_squad()
    {
        await using var factory = MatchCentreFactory.Live().WithWebHostBuilder(host => host
            .UseSetting("Squad:FoundryEndpoint", "https://example-foundry.services.ai.azure.com/")
            .UseSetting("Squad:Deployments:Small", "small-model")
            .UseSetting("Squad:Deployments:Large", "large-model"));

        factory.Services.GetRequiredService<ISquad>().Should().BeOfType<AnalystSquad>();
        factory.Services.GetServices<ISquad>().Should().ContainSingle("the Analyst replaces the demo squad, it doesn't sit beside it");
    }
}
