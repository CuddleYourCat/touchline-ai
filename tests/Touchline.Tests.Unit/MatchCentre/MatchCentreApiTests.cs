using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Demo;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class MatchCentreApiTests
{
    private const string _match = "demo-1";

    private WebApplicationFactory<Program> _finished = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartAFinishedMatch()
    {
        _finished = MatchCentreFactory.Finished();
        _client = _finished.CreateClient();

        // The lap starts when the source is first used, and plays in a few milliseconds.
        await _client.GetAsync(Url($"/api/matches/{_match}"));
        await Task.Delay(TimeSpan.FromMilliseconds(200));
    }

    [OneTimeTearDown]
    public void StopMatchCentre()
    {
        _client.Dispose();
        _finished.Dispose();
    }

    [Test]
    public async Task The_teams_are_the_fictional_clubs_with_their_squads()
    {
        var teams = await _client.GetFromJsonAsync<JsonElement>(Url("/api/teams"));
        var team = await _client.GetFromJsonAsync<JsonElement>(Url("/api/teams/north-bank-fc"));

        teams.EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should().BeEquivalentTo("north-bank-fc", "thames-valley-united");
        team.GetProperty("squad").GetArrayLength().Should().BeGreaterThan(11);
        team.GetProperty("squad")[0].GetProperty("position").ValueKind.Should().Be(JsonValueKind.String, "enums travel as names");
    }

    [Test]
    public async Task An_unknown_club_is_a_problem_details_404()
    {
        using var response = await _client.GetAsync(Url("/api/teams/a-real-club"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task The_finished_match_is_listed_and_read_in_full()
    {
        var finished = await _client.GetFromJsonAsync<JsonElement>(Url("/api/matches?status=fulltime"));
        var match = await _client.GetFromJsonAsync<JsonElement>(Url($"/api/matches/{_match}"));

        finished.EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().Contain(_match);
        match.GetProperty("summary").GetProperty("score").GetProperty("home").GetInt32().Should().Be(3);
        match.GetProperty("summary").GetProperty("dataSource").GetString().Should().Be("synthetic");
        match.GetProperty("homeLineup").GetArrayLength().Should().Be(11);
        match.GetProperty("state").GetProperty("fullTime").GetBoolean().Should().BeTrue();
    }

    [TestCase("/api/matches?status=postponed")]
    [TestCase("/api/matches/demo-1/events?limit=0")]
    [TestCase("/api/matches/demo-1/events?limit=1001")]
    [TestCase("/api/matches/demo-1/events?after=-1")]
    [TestCase("/api/matches/demo-1/moments?mode=pundit")]
    [TestCase("/api/matches/demo-1/moments?team=a-real-club")]
    [TestCase("/api/matches/demo-1/moments?lang=english")]
    [TestCase("/api/matches/demo-1/recap?player=a-real-player")]
    public async Task Bad_query_values_are_validation_problems(string url)
    {
        using var response = await _client.GetAsync(Url(url));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [TestCase("/api/matches/demo-99")]
    [TestCase("/api/matches/demo-99/events")]
    [TestCase("/api/matches/demo-99/momentum")]
    [TestCase("/api/matches/demo-99/moments")]
    [TestCase("/api/matches/demo-99/recap")]
    [TestCase("/api/matches/demo-99/stream")]
    [TestCase("/api/seasons/2026-27/players/nbfc-07")]
    public async Task Unknown_matches_and_season_cards_are_404s(string url)
    {
        using var response = await _client.GetAsync(Url(url));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Moments_carry_commentary_written_for_the_viewers_mode()
    {
        var moments = await _client.GetFromJsonAsync<JsonElement>(Url($"/api/matches/{_match}/moments?mode=Analyst"));

        moments.GetArrayLength().Should().Be(9);
        moments[0].GetProperty("commentary").GetProperty("mode").GetString().Should().Be("Analyst");
        moments[0].GetProperty("tier").GetString().Should().Be("Key");
    }

    [Test]
    public async Task Momentum_covers_the_whole_match_in_five_minute_windows()
    {
        var readings = await _client.GetFromJsonAsync<JsonElement>(Url($"/api/matches/{_match}/momentum"));

        readings.GetArrayLength().Should().BeGreaterThanOrEqualTo(18);
        readings.EnumerateArray().Should().OnlyContain(r => r.GetProperty("value").GetDouble() >= -1 && r.GetProperty("value").GetDouble() <= 1);
    }

    [Test]
    public async Task The_stand_in_analyst_says_it_is_a_stand_in()
    {
        using var response = await _client.PostAsJsonAsync(Url($"/api/matches/{_match}/questions"), new QuestionRequest("Who scored the winner?"));
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        answer.GetProperty("text").GetString().Should().StartWith(DemoSquad.StandInNotice);
        answer.GetProperty("citations").GetArrayLength().Should().Be(1);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task An_empty_question_is_refused(string question)
    {
        using var response = await _client.PostAsJsonAsync(Url($"/api/matches/{_match}/questions"), new QuestionRequest(question));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task A_question_too_long_to_be_worth_its_tokens_is_refused()
    {
        var question = new string('x', QuestionRequest.MaxLength + 1);

        using var response = await _client.PostAsJsonAsync(Url($"/api/matches/{_match}/questions"), new QuestionRequest(question));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task The_recap_is_ready_at_full_time()
    {
        var recap = await _client.GetFromJsonAsync<JsonElement>(Url($"/api/matches/{_match}/recap"));

        recap.GetProperty("headline").GetString().Should().Contain("3–3");
    }

    [Test]
    public async Task When_the_squad_is_down_questions_and_recaps_degrade_to_a_503_and_the_match_carries_on()
    {
        var squad = new Mock<ISquad>();
        squad.Setup(s => s.AnswerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Viewer>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Foundry is unreachable"));
        squad.Setup(s => s.RecapAsync(It.IsAny<string>(), It.IsAny<Viewer>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());
        using var factory = MatchCentreFactory.Finished()
            .WithWebHostBuilder(host => host.ConfigureTestServices(services => services.AddSingleton(squad.Object)));
        using var client = factory.CreateClient();
        await client.GetAsync(Url($"/api/matches/{_match}"));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using var question = await client.PostAsJsonAsync(Url($"/api/matches/{_match}/questions"), new QuestionRequest("Why?"));
        using var recap = await client.GetAsync(Url($"/api/matches/{_match}/recap"));
        using var match = await client.GetAsync(Url($"/api/matches/{_match}"));

        question.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        recap.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await question.Content.ReadAsStringAsync()).Should().NotContain("Foundry", "internal errors stay in the logs");
        match.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task The_stream_replays_from_after_to_full_time_and_then_ends()
    {
        var messages = await ReadStream(_client, $"/api/matches/{_match}/stream?after=0");

        messages.Count(m => m.EventType == StreamEvents.Event).Should().Be(DemoRecording.Events.Count);
        messages.Count(m => m.EventType == StreamEvents.Moment).Should().Be(9);
        messages[^1].EventType.Should().Be(StreamEvents.FullTime);
        messages[^1].EventId.Should().Be(DemoRecording.Events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Last_event_id_resumes_after_the_last_sequence_seen()
    {
        var resumeFrom = DemoRecording.Events.Count - 3;

        var messages = await ReadStream(_client, $"/api/matches/{_match}/stream?after=0", request =>
            request.Headers.Add("Last-Event-ID", resumeFrom.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        messages.Where(m => m.EventType == StreamEvents.Event).Should().HaveCount(3);
    }

    [Test]
    public async Task A_live_stream_starts_with_a_snapshot_and_keeps_the_connection_alive_with_heartbeats()
    {
        using var factory = MatchCentreFactory.Live();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"/api/matches/{_match}/stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var received = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync(timeout.Token)).EnumerateAsync(timeout.Token))
        {
            received.Add(item);
            if (received.Count == 2)
            {
                break;
            }
        }

        received[0].EventType.Should().Be(StreamEvents.State);
        received[1].EventType.Should().Be(StreamEvents.Heartbeat);
    }

    [Test]
    public async Task The_openapi_document_is_served()
    {
        using var response = await _client.GetAsync(Url("/openapi/v1.json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<List<SseItem<string>>> ReadStream(HttpClient client, string url, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(url));
        configure?.Invoke(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync(timeout.Token)).EnumerateAsync(timeout.Token))
        {
            items.Add(item);
        }

        return items;
    }
}
