using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class MatchCentreSpaFallbackTests
{
    private const string _indexHtml = "<!doctype html><title>Match Centre UI</title>";

    private string _webRoot = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartMatchCentreWithABuiltUi()
    {
        _webRoot = Directory.CreateTempSubdirectory("touchline-wwwroot-").FullName;
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), _indexHtml);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Production").UseWebRoot(_webRoot));
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopMatchCentre()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    [TestCase("/")]
    [TestCase("/matches/demo-1")]
    [TestCase("/teams/fan-42/edit")]
    [TestCase("/kickoff")]
    public async Task Ui_routes_get_index_html(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(_indexHtml);
    }

    [TestCase("/api/no-such-endpoint")]
    [TestCase("/api/matches/demo-1/no-such-thing")]
    [TestCase("/API/No-Such-Endpoint")]
    public async Task Unknown_api_paths_get_a_404_problem_not_the_ui(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [TestCase("/")]
    [TestCase("/matches/x")]
    public async Task Index_html_is_sent_with_no_cache_so_a_deploy_is_picked_up(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoCache.Should().BeTrue();
    }

    [TestCase("/API/nope")]
    [TestCase("/OpenApi/nope")]
    public async Task Excluded_prefixes_ignore_case(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
        (await response.Content.ReadAsStringAsync()).Should().NotBe(_indexHtml);
    }

    [TestCase("/openapi/v2.json")]
    [TestCase("/openapi/no-such-thing")]
    public async Task Unknown_openapi_paths_get_a_404_not_the_ui(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotBe(_indexHtml);
    }

    [Test]
    public async Task Known_api_endpoints_still_answer()
    {
        using var response = await _client.GetAsync(new Uri("/api/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task With_no_ui_build_a_ui_route_answers_404_rather_than_failing()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Production"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/matches/demo-1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
