using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Touchline.Configuration;
using Touchline.MatchCentre.Api;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The feature flags that gate Match Centre endpoints (ADR-062).</summary>
[TestFixture]
public class FeatureFlagEndpointTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static WebApplicationFactory<Program> Factory(params (string Flag, bool On)[] flags) =>
        MatchCentreFactory.Live().WithWebHostBuilder(host =>
        {
            foreach (var (flag, on) in flags)
            {
                host.UseSetting($"FeatureManagement:{flag}", on ? "true" : "false");
            }
        });

    [Test]
    public async Task Every_flag_defaults_to_on_when_no_store_is_configured()
    {
        await using var factory = MatchCentreFactory.Live();
        var features = factory.Services.GetRequiredService<Microsoft.FeatureManagement.IFeatureManager>();

        foreach (var name in FeatureNames.All)
        {
            (await features.IsEnabledAsync(name)).Should().BeTrue($"{name} is on in appsettings");
        }
    }

    [Test]
    public async Task With_FanQa_off_a_question_gets_a_503_problem()
    {
        await using var factory = Factory((FeatureNames.FanQa, false));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/api/matches/demo-1/questions", UriKind.Relative), new QuestionRequest("Who scored?"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("title").GetString().Should().Be("Temporarily unavailable");
        problem.RootElement.GetProperty("detail").GetString().Should().Contain("switched off");
    }

    [Test]
    public async Task With_FanQa_on_a_question_is_answered()
    {
        await using var factory = Factory((FeatureNames.FanQa, true));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/api/matches/demo-1/questions", UriKind.Relative), new QuestionRequest("Who scored?"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("FanQa", "POST", "/api/matches/demo-1/questions")]
    [TestCase("FanMatches", "POST", "/api/matches")]
    public async Task A_Touchline_Kill_setting_switches_the_endpoint_off_in_the_real_host(string flag, string method, string path)
    {
        await using var factory = MatchCentreFactory.Live().WithWebHostBuilder(host => host.UseSetting($"Touchline:Kill:{flag}", "true"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(new { question = "Who scored?", homeTeamId = "north-bank-fc", awayTeamId = "thames-valley-united" })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("switched off");
    }

    [Test]
    public async Task Turning_FanQa_off_leaves_the_other_endpoints_alone()
    {
        await using var factory = Factory((FeatureNames.FanQa, false));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/matches", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task With_FanMatches_off_a_match_is_not_started_and_the_launcher_is_never_called()
    {
        var launcher = new Mock<IMatchLauncher>();
        await using var factory = Factory((FeatureNames.FanMatches, false))
            .WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(launcher.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/matches", UriKind.Relative), new MatchRequest("north-bank-fc", "thames-valley-united"), _json);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("switched off");
        launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task With_FanMatches_on_the_launcher_is_asked()
    {
        var accepted = new MatchAccepted("fan-match-1", MatchStatus.Scheduled, "/api/matches/fan-match-1/stream");
        var launcher = new Mock<IMatchLauncher>();
        launcher.Setup(l => l.LaunchAsync(It.IsAny<MatchRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchLaunch(LaunchOutcome.Accepted, accepted, null, null));
        await using var factory = Factory((FeatureNames.FanMatches, true))
            .WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(launcher.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/matches", UriKind.Relative), new MatchRequest("north-bank-fc", "thames-valley-united"), _json);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, false)]
    public async Task Status_reports_fan_matches_only_when_the_flag_is_on_and_the_launcher_can_start_them(bool flagOn, bool canLaunch, bool expected)
    {
        var launcher = new Mock<IMatchLauncher>();
        launcher.SetupGet(l => l.CanLaunch).Returns(canLaunch);
        await using var factory = Factory((FeatureNames.FanMatches, flagOn))
            .WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(launcher.Object)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("fanMatches").GetBoolean().Should().Be(expected);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Status_reports_fan_questions_as_their_flag_says(bool flagOn)
    {
        await using var factory = Factory((FeatureNames.FanQa, flagOn));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("fanQa").GetBoolean().Should().Be(flagOn);
    }

    [Test]
    public async Task Status_reports_no_fan_matches_in_the_demo_build_whose_launcher_is_unconnected()
    {
        await using var factory = Factory((FeatureNames.FanMatches, true));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/status", UriKind.Relative));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("fanMatches").GetBoolean().Should().BeFalse();
    }
}
