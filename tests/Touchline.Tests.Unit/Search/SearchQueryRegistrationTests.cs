using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Touchline.Domain.Squad;
using Touchline.Search;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class SearchQueryRegistrationTests
{
    [Test]
    public void EndpointUnset_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddPossessionSearch(Config());

        services.Should().BeEmpty();
    }

    [Test]
    public void EndpointSet_RegistersSearch_WithoutAnyNetworkCall()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Mock.Of<TokenCredential>());

        services.AddPossessionSearch(Config(("Search:Endpoint", "https://example.search.windows.net"), ("Squad:FoundryEndpoint", "https://example.openai.azure.com")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<IPossessionSearch>().Should().BeOfType<AzurePossessionSearch>();
    }

    [Test]
    public void EndpointSetWithoutFoundry_StillRegistersSearch()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Mock.Of<TokenCredential>());

        services.AddPossessionSearch(Config(("Search:Endpoint", "https://example.search.windows.net")));

        using var provider = services.BuildServiceProvider();
        provider.GetService<IPossessionSearch>().Should().NotBeNull();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();
}
