using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Maintenance;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class MaintenanceModeTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartMatchCentreInMaintenance()
    {
        _factory = MatchCentreFactory.Finished().WithWebHostBuilder(host => host
            .UseSetting("MatchCentre:Maintenance:Enabled", "true")
            .UseSetting("MatchCentre:Maintenance:RetryAfterSeconds", "45")
            .UseSetting("MatchCentre:Maintenance:EstimatedMinutes", "20"));
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopMatchCentre()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestCase("/")]
    [TestCase("/matches/demo-1")]
    [TestCase("/assets/index-abc123.js")]
    public async Task Page_requests_get_the_maintenance_page(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        response.Headers.GetValues(MaintenanceMode.HeaderName).Should().Equal("on");
        response.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(45));
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("PITCH MAINTENANCE IN PROGRESS")
            .And.Contain("Kick-off expected in roughly 20 mins.")
            .And.Contain("<a class=\"cta\" href=\"/\">RETURN TO TOUCHLINE</a>")
            .And.Contain("Synthetic data")
            .And.Contain("url(\"/maintenance/background.webp\")")
            .And.NotContain("<script")
            .And.Contain("<meta http-equiv=\"refresh\" content=\"45\">")
            .And.NotContain("{{");
    }

    [Test]
    public async Task The_background_image_is_served_during_maintenance()
    {
        using var response = await _client.GetAsync(new Uri(MaintenanceMode.BackgroundPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/webp");
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(1));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(1000).And.BeLessThan(250 * 1024);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WEBP");
    }

    [TestCase("/api/matches")]
    [TestCase("/api/matches/demo-1/stream")]
    [TestCase("/API/Teams")]
    [TestCase("/openapi/v1.json")]
    public async Task Api_requests_get_a_maintenance_problem(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        response.Headers.GetValues(MaintenanceMode.HeaderName).Should().Equal("on");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().Should().Be(ProblemTypes.Maintenance);
    }

    [Test]
    public async Task Writes_are_refused_too()
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(new Uri("/api/matches", UriKind.Relative), content);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [TestCase("/health")]
    [TestCase("/alive")]
    public async Task Health_probes_still_answer_so_the_replica_stays_in_rotation(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(MaintenanceMode.HeaderName).Should().BeFalse();
    }

    [Test]
    public async Task Off_by_default()
    {
        using var factory = MatchCentreFactory.Finished();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/matches", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(MaintenanceMode.HeaderName).Should().BeFalse();
    }

    [Test]
    public void A_retry_after_out_of_range_stops_the_app_starting()
    {
        using var factory = MatchCentreFactory.Finished().WithWebHostBuilder(host => host
            .UseSetting("MatchCentre:Maintenance:Enabled", "true")
            .UseSetting("MatchCentre:Maintenance:RetryAfterSeconds", "0"));

        var start = () => factory.CreateClient();

        start.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }
}
