using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class TeamBuilderApiTests
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
    public async Task The_builder_options_list_every_archetype_and_the_squad_rules()
    {
        var options = await _client.GetFromJsonAsync<TeamBuilderOptions>(Url("/api/team-builder/options"), _json);

        options!.Archetypes.Select(a => a.Id).Should().BeEquivalentTo(Enum.GetValues<PlayerArchetype>());
        options.Rules.MinPlayers.Should().Be(SquadRules.MinPlayers);
        options.CrestEmblems.Should().Contain(CrestEmblem.Oak);
    }

    [Test]
    public async Task Name_suggestions_come_in_the_number_asked_for()
    {
        var suggestions = await _client.GetFromJsonAsync<NameSuggestions>(Url("/api/team-builder/names?kind=player&count=3"), _json);

        suggestions!.Names.Should().HaveCount(3);
    }

    [TestCase("/api/team-builder/names?kind=pundit")]
    [TestCase("/api/team-builder/names?kind=team&count=11")]
    [TestCase("/api/teams?origin=imported")]
    public async Task Bad_builder_queries_are_validation_problems(string url)
    {
        using var response = await _client.GetAsync(Url(url));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task A_name_check_answers_each_name_in_order()
    {
        var request = new NameCheckRequest([new(NameKind.Team, "Riverside Rovers"), new(NameKind.Player, "Player 9")]);

        using var response = await _client.PostAsJsonAsync(Url("/api/team-builder/names/check"), request, _json);
        var result = await response.Content.ReadFromJsonAsync<NameCheckResult>(_json);

        result!.Names.Select(v => v.Allowed).Should().Equal(true, false);
        result.Names[1].Reason.Should().Contain("letters");
    }

    [Test]
    public async Task Creating_a_team_returns_it_once_with_its_edit_key_location_and_etag()
    {
        using var response = await _client.PostAsJsonAsync(Url("/api/teams"), TeamDrafts.Valid(), _json);
        var created = await response.Content.ReadFromJsonAsync<TeamCreated>(_json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"/api/teams/{created!.Team.Team.Id}");
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        created.EditKey.Should().HaveLength(43);

        var fetched = await _client.GetFromJsonAsync<TeamDetail>(Url($"/api/teams/{created.Team.Team.Id}"), _json);
        fetched!.Squad.Should().HaveCount(16);
        (await _client.GetStringAsync(Url($"/api/teams/{created.Team.Team.Id}"))).Should().NotContain(created.EditKey);
    }

    [Test]
    public async Task Fan_teams_are_listed_after_the_built_in_ones_and_can_be_filtered()
    {
        var created = await Create("Larkmoor Athletic");

        var all = await _client.GetFromJsonAsync<List<TeamSummary>>(Url("/api/teams"), _json);
        var fans = await _client.GetFromJsonAsync<List<TeamSummary>>(Url("/api/teams?origin=fan"), _json);

        all![0].Origin.Should().Be(TeamOrigin.BuiltIn);
        fans!.Should().OnlyContain(t => t.Origin == TeamOrigin.Fan).And.Contain(t => t.Id == created.Team.Team.Id);
    }

    [Test]
    public async Task An_invalid_draft_is_a_validation_problem_keyed_by_field()
    {
        using var response = await _client.PostAsJsonAsync(Url("/api/teams"), TeamDrafts.Valid() with { ShortName = "toolong" }, _json);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("errors").TryGetProperty("shortName", out _).Should().BeTrue();
    }

    [Test]
    public async Task A_name_the_guard_refuses_is_reported_against_its_field()
    {
        var guard = new Mock<INameGuard>();
        guard.Setup(g => g.CheckAsync(It.IsAny<IReadOnlyList<NameToCheck>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NameToCheck> names, CancellationToken _) =>
                [.. names.Select(n => new NameVerdict(n.Kind, n.Name, n.Name != "Penmere Town", n.Name == "Penmere Town" ? "That's a real club's name." : null))]);
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(guard.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Url("/api/teams"), TeamDrafts.Valid("Penmere Town"), _json);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("errors").GetProperty("name")[0].GetString().Should().Be("That's a real club's name.");
    }

    [Test]
    public async Task An_update_needs_the_edit_key()
    {
        var created = await Create("Saltmere Harriers");

        using var noKey = await Put(created.Team.Team.Id, TeamDrafts.Valid("Saltmere Harriers"), key: null);
        using var wrongKey = await Put(created.Team.Team.Id, TeamDrafts.Valid("Saltmere Harriers"), key: "not-the-key");

        noKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        wrongKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Built_in_teams_are_read_only()
    {
        using var response = await Put("north-bank-fc", TeamDrafts.Valid(), key: "anything");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task An_update_with_the_key_bumps_the_version_and_keeps_player_ids()
    {
        var created = await Create("Thornwold Vale");
        var draft = TeamDrafts.Valid("Thornwold Wanderers") with
        {
            Squad = [.. created.Team.Squad.Select(p => new PlayerDraft(p.Name, p.ShirtNumber, p.Archetype!.Value, p.PreferredFoot, p.Id))]
        };

        using var response = await Put(created.Team.Team.Id, draft, created.EditKey, ifMatch: "\"1\"");
        var updated = await response.Content.ReadFromJsonAsync<TeamDetail>(_json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        updated!.Version.Should().Be(2);
        updated.Team.Name.Should().Be("Thornwold Wanderers");
        updated.Squad.Select(p => p.Id).Should().BeEquivalentTo(created.Team.Squad.Select(p => p.Id));
    }

    [Test]
    public async Task An_update_from_a_stale_copy_is_refused()
    {
        var created = await Create("Fenwold Borough");
        using var first = await Put(created.Team.Team.Id, TeamDrafts.Valid("Fenwold Borough"), created.EditKey, ifMatch: "\"1\"");

        using var second = await Put(created.Team.Team.Id, TeamDrafts.Valid("Fenwold Town"), created.EditKey, ifMatch: "\"1\"");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Test]
    public async Task Deleting_with_the_key_removes_the_team()
    {
        var created = await Create("Gallowmere Rangers");
        using var request = new HttpRequestMessage(HttpMethod.Delete, Url($"/api/teams/{created.Team.Team.Id}"));
        request.Headers.Add(EditKeys.Header, created.EditKey);

        using var deleted = await _client.SendAsync(request);
        using var after = await _client.GetAsync(Url($"/api/teams/{created.Team.Team.Id}"));

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        after.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_crest_is_served_as_svg_that_cannot_run_script()
    {
        using var response = await _client.GetAsync(Url("/api/teams/north-bank-fc/crest.svg"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("default-src 'none'");
        (await response.Content.ReadAsStringAsync()).Should().StartWith("<svg");
    }

    [Test]
    public async Task A_fan_team_can_be_a_viewers_favourite()
    {
        var created = await Create("Oakhaven Albion");

        using var response = await _client.GetAsync(Url($"/api/matches/demo-1/moments?team={created.Team.Team.Id}&player={created.Team.Squad[0].Id}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Starting_a_match_checks_the_teams_and_sheets_before_the_launcher()
    {
        var created = await Create("Marrowby Town");
        var squad = created.Team.Squad;
        var noKeeper = new MatchdaySheet([.. squad.Where(p => p.Position != Touchline.Domain.Teams.PlayerPosition.Goalkeeper).Take(11).Select(p => p.Id)], []);

        using var unknown = await StartMatch(new MatchRequest("fan-nobody", "north-bank-fc"));
        using var itself = await StartMatch(new MatchRequest("north-bank-fc", "north-bank-fc"));
        using var badSheet = await StartMatch(new MatchRequest(created.Team.Team.Id, "north-bank-fc", Home: noKeeper));

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        itself.StatusCode.Should().Be(HttpStatusCode.Conflict);
        badSheet.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badSheet.Content.ReadAsStringAsync()).Should().Contain("exactly one goalkeeper");
    }

    [Test]
    public async Task A_valid_match_request_is_refused_plainly_in_the_demo_build()
    {
        using var response = await StartMatch(new MatchRequest("north-bank-fc", "thames-valley-united"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("isn't connected in this demo build");
    }

    [Test]
    public async Task When_the_launcher_is_busy_the_fan_is_told_when_to_try_again()
    {
        var launcher = new Mock<IMatchLauncher>();
        launcher.Setup(l => l.LaunchAsync(It.IsAny<MatchRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchLaunch(LaunchOutcome.Busy, null, TimeSpan.FromSeconds(90), "A fan match is already live."));
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(launcher.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Url("/api/matches"), new MatchRequest("north-bank-fc", "thames-valley-united"), _json);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(90));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().Should().Be(ProblemTypes.FanMatchLimit);
        problem.RootElement.GetProperty("detail").GetString().Should().Be("A fan match is already live.");
    }

    [TestCase("has space")]
    [TestCase("tab\tkey")]
    [TestCase("x")]
    public async Task An_idempotency_key_must_be_1_to_100_visible_ASCII_characters(string key)
    {
        var tooLong = key == "x" ? new string('k', 101) : key;
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/matches"))
        {
            Content = JsonContent.Create(new MatchRequest("north-bank-fc", "thames-valley-united"), options: _json)
        };
        request.Headers.TryAddWithoutValidation(TeamBuilderEndpoints.IdempotencyHeader, tooLong).Should().BeTrue();

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(TeamBuilderEndpoints.IdempotencyHeader);
    }

    [Test]
    public async Task An_accepted_match_points_at_itself()
    {
        var accepted = new MatchAccepted("fan-match-1", MatchStatus.Scheduled, "/api/matches/fan-match-1/stream");
        var launcher = new Mock<IMatchLauncher>();
        launcher.Setup(l => l.LaunchAsync(It.IsAny<MatchRequest>(), "click-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchLaunch(LaunchOutcome.Accepted, accepted, null, null));
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(launcher.Object)));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/matches"))
        {
            Content = JsonContent.Create(new MatchRequest("north-bank-fc", "thames-valley-united"), options: _json)
        };
        request.Headers.Add(TeamBuilderEndpoints.IdempotencyHeader, "click-1");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().Be("/api/matches/fan-match-1");
    }

    [Test]
    public async Task Creating_too_many_teams_in_an_hour_is_refused_with_a_problem()
    {
        using var factory = MatchCentreFactory.Create(speed: "100000", lapGap: "01:00:00", teamsPerHour: "2");
        using var client = factory.CreateClient();

        HttpStatusCode last = default;
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.PostAsJsonAsync(Url("/api/teams"), TeamDrafts.Valid(), _json);
            last = response.StatusCode;
            if (i == 2)
            {
                response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            }
        }

        last.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [TestCase("""{"names":[null]}""", "names[0]")]
    [TestCase("""{"names":[{"kind":99,"name":"Riverside"}]}""", "names[0].kind")]
    public async Task A_malformed_name_check_is_a_validation_problem_not_a_crash(string body, string field)
    {
        using var response = await _client.PostAsync(Url("/api/team-builder/names/check"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Test]
    public async Task Abbreviations_are_checked_by_their_own_format()
    {
        var request = new NameCheckRequest([new(NameKind.Abbreviation, "RR"), new(NameKind.Abbreviation, "rr")]);

        using var response = await _client.PostAsJsonAsync(Url("/api/team-builder/names/check"), request, _json);
        var result = await response.Content.ReadFromJsonAsync<NameCheckResult>(_json);

        result!.Names.Select(v => v.Allowed).Should().Equal(true, false);
    }

    [Test]
    public async Task Names_are_not_suggested_for_abbreviations()
    {
        using var response = await _client.GetAsync(Url("/api/team-builder/names?kind=abbreviation"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Crest_initials_the_guard_refuses_are_reported_against_the_crest()
    {
        var guard = new Mock<INameGuard>();
        guard.Setup(g => g.CheckAsync(It.IsAny<IReadOnlyList<NameToCheck>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NameToCheck> names, CancellationToken _) =>
                [.. names.Select(n => n.Kind == NameKind.Abbreviation && n.Name == "RR"
                    ? new NameVerdict(n.Kind, n.Name, false, "Those are a real club's initials.")
                    : new NameVerdict(n.Kind, n.Name, true, null))]);
        using var factory = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(s => s.AddSingleton(guard.Object)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Url("/api/teams"), TeamDrafts.Valid(), _json);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("errors").GetProperty("crest.initials")[0].GetString().Should().Be("Those are a real club's initials.");
    }

    [TestCase("*")]
    [TestCase("\"7\", \"1\"")]
    public async Task If_match_accepts_a_wildcard_or_a_list_containing_the_current_tag(string ifMatch)
    {
        var created = await Create("Wexmouth Rangers");

        using var response = await Put(created.Team.Team.Id, TeamDrafts.Valid("Wexmouth Rangers"), created.EditKey, ifMatch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task If_match_never_accepts_a_weak_tag()
    {
        var created = await Create("Ivyholme Town");

        using var response = await Put(created.Team.Team.Id, TeamDrafts.Valid("Ivyholme Town"), created.EditKey, "W/\"1\"");

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [TestCase("""{}""", "homeTeamId")]
    [TestCase("""{"homeTeamId":"north-bank-fc"}""", "awayTeamId")]
    public async Task A_match_request_missing_a_team_is_a_validation_problem(string body, string field)
    {
        using var response = await _client.PostAsync(Url("/api/matches"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Test]
    public async Task Paid_endpoints_are_rate_limited_per_client()
    {
        using var factory = MatchCentreFactory.Create(speed: "100000", lapGap: "01:00:00", paidCallsPerMinute: "2");
        using var client = factory.CreateClient();
        var request = new NameCheckRequest([new(NameKind.Team, "Riverside Rovers")]);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.PostAsJsonAsync(Url("/api/team-builder/names/check"), request, _json);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<TeamCreated> Create(string name)
    {
        using var response = await _client.PostAsJsonAsync(Url("/api/teams"), TeamDrafts.Valid(name), _json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamCreated>(_json))!;
    }

    private async Task<HttpResponseMessage> Put(string teamId, TeamDraft draft, string? key, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url($"/api/teams/{teamId}")) { Content = JsonContent.Create(draft, options: _json) };
        if (key is not null)
        {
            request.Headers.Add(EditKeys.Header, key);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> StartMatch(MatchRequest request) =>
        _client.PostAsJsonAsync(Url("/api/matches"), request, _json);
}
