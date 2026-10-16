using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class MatchCentreEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartMatchCentreInProduction()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Production"));
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopMatchCentre()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task Status_reports_the_service_and_a_synthetic_data_source()
    {
        using var response = await _client.GetAsync(new Uri("/api/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("service").GetString().Should().Be("touchline-ai");
        body.RootElement.GetProperty("dataSource").GetString().Should().Be("synthetic");
    }

    [TestCase("/health")]
    [TestCase("/alive")]
    public async Task Health_endpoints_respond_outside_Development(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the AppHost and Container Apps probes call {0} in production", path);
    }
}
