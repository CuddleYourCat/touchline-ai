using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.ContentSafety;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>
/// The name guard at the endpoints (ADR-049): the chaos case where Content Safety is down, and
/// the 403 problem types (ADR-031).
/// </summary>
[TestFixture]
public class TeamNameGuardApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartMatchCentre()
    {
        _factory = MatchCentreFactory.Finished();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopMatchCentre()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task When_content_safety_is_down_creating_a_team_is_a_503_and_nothing_is_stored()
    {
        using var down = ContentSafetyDown();
        using var client = down.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/teams", TeamDrafts.Valid(), _json);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fans = await client.GetFromJsonAsync<List<TeamSummary>>("/api/teams?origin=fan", _json);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        problem.RootElement.GetProperty("type").GetString().Should().Be(ProblemTypes.NameCheckUnavailable);
        fans.Should().BeEmpty();
    }

    [Test]
    public async Task When_content_safety_is_down_a_name_check_is_a_503()
    {
        using var down = ContentSafetyDown();
        using var client = down.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/team-builder/names/check", new NameCheckRequest([new(NameKind.Team, "Riverside Rovers")]), _json);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        problem.RootElement.GetProperty("type").GetString().Should().Be(ProblemTypes.NameCheckUnavailable);
    }

    [Test]
    public async Task When_content_safety_is_down_a_name_with_a_format_problem_is_still_answered_without_it()
    {
        using var down = ContentSafetyDown();
        using var client = down.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/team-builder/names/check", new NameCheckRequest([new(NameKind.Player, "Player 9")]), _json);
        var result = await response.Content.ReadFromJsonAsync<NameCheckResult>(_json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Names.Single().Allowed.Should().BeFalse();
    }

    [Test]
    public async Task When_content_safety_is_down_an_update_is_a_503_and_the_stored_team_is_unchanged()
    {
        using var up = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(AllowingGuard())));
        using var upClient = up.CreateClient();
        using var created = await upClient.PostAsJsonAsync("/api/teams", TeamDrafts.Valid("Larkmoor Athletic"), _json);
        var team = await created.Content.ReadFromJsonAsync<TeamCreated>(_json);
        using var down = ContentSafetyDown(up.Services.GetRequiredService<ITeamStore>());
        using var client = down.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/teams/{team!.Team.Team.Id}") { Content = JsonContent.Create(TeamDrafts.Valid("Larkmoor Town"), options: _json) };
        request.Headers.Add(EditKeys.Header, team.EditKey);

        using var response = await client.SendAsync(request);
        var after = await client.GetFromJsonAsync<TeamDetail>($"/api/teams/{team.Team.Team.Id}", _json);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        after!.Team.Name.Should().Be("Larkmoor Athletic");
        after.Version.Should().Be(1);
    }

    [Test]
    public async Task A_built_in_team_403_carries_the_read_only_problem_type()
    {
        using var response = await Put("north-bank-fc", "anything");

        await AssertForbidden(response, ProblemTypes.ReadOnlyTeam);
    }

    [Test]
    public async Task A_wrong_key_403_carries_the_wrong_edit_key_problem_type()
    {
        using var up = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(AllowingGuard())));
        using var client = up.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/teams", TeamDrafts.Valid("Saltmere Harriers"), _json);
        var team = await created.Content.ReadFromJsonAsync<TeamCreated>(_json);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/teams/{team!.Team.Team.Id}");
        request.Headers.Add(EditKeys.Header, "not-the-key");

        using var response = await client.SendAsync(request);

        await AssertForbidden(response, ProblemTypes.WrongEditKey);
    }

    [Test]
    public async Task Deleting_a_built_in_team_is_the_read_only_problem_type_too()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/teams/north-bank-fc");
        request.Headers.Add(EditKeys.Header, "anything");

        using var response = await _client.SendAsync(request);

        await AssertForbidden(response, ProblemTypes.ReadOnlyTeam);
    }

    [Test]
    public async Task A_colliding_team_id_is_retried_with_a_fresh_one()
    {
        var ids = new List<string>();
        var store = new Mock<ITeamStore>();
        store.Setup(s => s.AddAsync(It.IsAny<StoredTeam>(), It.IsAny<CancellationToken>()))
            .Returns((StoredTeam team, CancellationToken _) =>
            {
                ids.Add(team.Detail.Team.Id);
                return ids.Count == 1 ? Task.FromException(new TeamAlreadyExistsException(team.Detail.Team.Id)) : Task.CompletedTask;
            });
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(store.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/teams", TeamDrafts.Valid(), _json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        ids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Test]
    public async Task A_team_id_that_keeps_colliding_is_a_409_problem_not_a_500()
    {
        var store = new Mock<ITeamStore>();
        store.Setup(s => s.AddAsync(It.IsAny<StoredTeam>(), It.IsAny<CancellationToken>()))
            .Returns((StoredTeam team, CancellationToken _) => Task.FromException(new TeamAlreadyExistsException(team.Detail.Team.Id)));
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(store.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/teams", TeamDrafts.Valid(), _json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        store.Verify(s => s.AddAsync(It.IsAny<StoredTeam>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public async Task A_teams_outage_says_the_teams_are_unavailable_not_the_match()
    {
        var store = new Mock<ITeamStore>();
        store.Setup(s => s.ListAsync(It.IsAny<TeamOrigin?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Touchline.MatchCentre.Storage.MatchSourceUnavailableException("down", new HttpRequestException(), "The teams aren't available right now. Try again shortly."));
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(store.Object)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/teams");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("The teams aren't available").And.NotContain("match");
    }

    private static async Task AssertForbidden(HttpResponseMessage response, string type)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        problem.RootElement.GetProperty("type").GetString().Should().Be(type);
    }

    private async Task<HttpResponseMessage> Put(string teamId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/teams/{teamId}") { Content = JsonContent.Create(TeamDrafts.Valid(), options: _json) };
        request.Headers.Add(EditKeys.Header, key);
        return await _client.SendAsync(request);
    }

    private static INameGuard AllowingGuard()
    {
        var guard = new Mock<INameGuard>();
        guard.Setup(g => g.CheckAsync(It.IsAny<IReadOnlyList<NameToCheck>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NameToCheck> names, CancellationToken _) => [.. names.Select(n => new NameVerdict(n.Kind, n.Name, true, null))]);
        return guard.Object;
    }

    // The real guard over a Content Safety client whose every call fails as a throttled service would.
    private WebApplicationFactory<Program> ContentSafetyDown(ITeamStore? store = null)
    {
        var client = new Mock<ContentSafetyClient>();
        client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Content Safety is down"));
        var guard = new ContentSafetyNameGuard(client.Object, new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>(), Options.Create(new ContentSafetyOptions()), NullLogger<ContentSafetyNameGuard>.Instance);
        return _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s =>
        {
            s.AddSingleton<INameGuard>(guard);
            if (store is not null)
            {
                s.AddSingleton(store);
            }
        }));
    }
}
