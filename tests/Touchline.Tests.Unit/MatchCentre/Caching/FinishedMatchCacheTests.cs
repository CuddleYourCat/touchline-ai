using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Touchline.Domain.Matches;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Caching;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// The output cache for finished matches (ADR-061): finished reads are cached and shared, live
/// ones never are, and the viewer's settings vary the entry.
/// </summary>
[TestFixture]
public class FinishedMatchCacheTests
{
    private const string _id = "fix-2026-r1-01";
    private static readonly ClockReading _clock = new(MatchPeriod.SecondHalf, 90, 0, "90:00");

    private Mock<IMatchReadModel> _matches = null!;
    private Mock<ISquad> _squad = null!;
    private bool _fullTime;
    private CommentarySource _recapSource;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _fullTime = true;
        _recapSource = CommentarySource.Model;
        _matches = new Mock<IMatchReadModel>();
        _matches.Setup(m => m.GetMatchAsync(_id, It.IsAny<CancellationToken>())).ReturnsAsync(() => Detail(_id, _fullTime));
        _matches.Setup(m => m.GetMomentsAsync(_id, It.IsAny<Viewer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Viewer viewer, CancellationToken _) => [Moment(MomentTier.Key, Line(viewer.Language, viewer.Mode))]);
        _squad = new Mock<ISquad>();
        _squad.Setup(s => s.RecapAsync(_id, It.IsAny<Viewer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Viewer viewer, CancellationToken _) => new MatchRecap(_id, "Full time", "Text", viewer.Mode, viewer.Language, _recapSource));

        _factory = MatchCentreFactory.Finished().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(_matches.Object);
            services.AddSingleton(_squad.Object);
        }));
        _client = _factory.CreateClient();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Test]
    public async Task A_finished_match_is_read_once_and_then_served_from_the_cache()
    {
        var first = await GetAsync($"/api/matches/{_id}");
        var second = await GetAsync($"/api/matches/{_id}");

        second.Should().Be(first);
        Reads(nameof(IMatchReadModel.GetMatchAsync)).Should().Be(1);
    }

    [Test]
    public async Task A_live_match_is_read_fresh_every_time()
    {
        _fullTime = false;

        await GetAsync($"/api/matches/{_id}");
        await GetAsync($"/api/matches/{_id}");

        Reads(nameof(IMatchReadModel.GetMatchAsync)).Should().Be(2);
    }

    [Test]
    public async Task A_match_that_finishes_is_never_served_its_live_response()
    {
        _fullTime = false;
        var live = await GetAsync($"/api/matches/{_id}");

        _fullTime = true;
        var finished = await GetAsync($"/api/matches/{_id}");
        var again = await GetAsync($"/api/matches/{_id}");

        live.Should().Contain("\"fullTime\":false");
        finished.Should().Contain("\"fullTime\":true");
        again.Should().Be(finished);
        Reads(nameof(IMatchReadModel.GetMatchAsync)).Should().Be(2);
    }

    [Test]
    public async Task A_finished_matchs_moments_are_cached_per_viewer()
    {
        await GetAsync($"/api/matches/{_id}/moments?lang=en");
        await GetAsync($"/api/matches/{_id}/moments?lang=en");
        var spanish = await GetAsync($"/api/matches/{_id}/moments?lang=es");
        await GetAsync($"/api/matches/{_id}/moments?lang=es&mode=analyst");
        await GetAsync($"/api/matches/{_id}/moments?lang=es");

        spanish.Should().Contain("\"language\":\"es\"");
        Reads(nameof(IMatchReadModel.GetMomentsAsync)).Should().Be(3);
    }

    [Test]
    public async Task A_live_matchs_moments_are_never_cached()
    {
        _fullTime = false;

        await GetAsync($"/api/matches/{_id}/moments");
        await GetAsync($"/api/matches/{_id}/moments");

        Reads(nameof(IMatchReadModel.GetMomentsAsync)).Should().Be(2);
    }

    [Test]
    public async Task Moments_whose_key_moment_has_no_line_yet_are_not_cached()
    {
        _matches.Setup(m => m.GetMomentsAsync(_id, It.IsAny<Viewer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Moment(MomentTier.Key, null)]);

        await GetAsync($"/api/matches/{_id}/moments");
        await GetAsync($"/api/matches/{_id}/moments");

        Reads(nameof(IMatchReadModel.GetMomentsAsync)).Should().Be(2);
    }

    [Test]
    public async Task The_models_recap_is_cached_per_viewer()
    {
        await GetAsync($"/api/matches/{_id}/recap?mode=analyst");
        await GetAsync($"/api/matches/{_id}/recap?mode=analyst");
        await GetAsync($"/api/matches/{_id}/recap");

        _squad.Invocations.Count(i => i.Method.Name == nameof(ISquad.RecapAsync)).Should().Be(2);
    }

    [Test]
    public async Task A_template_recap_is_not_cached_because_the_models_may_still_be_coming()
    {
        _recapSource = CommentarySource.Template;

        await GetAsync($"/api/matches/{_id}/recap");
        await GetAsync($"/api/matches/{_id}/recap");

        _squad.Invocations.Count(i => i.Method.Name == nameof(ISquad.RecapAsync)).Should().Be(2);
    }

    [Test]
    public async Task A_recap_served_from_a_fallback_language_is_not_cached_under_the_viewers_language()
    {
        _squad.Setup(s => s.RecapAsync(_id, It.IsAny<Viewer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Viewer viewer, CancellationToken _) => new MatchRecap(_id, "Full time", "Text", viewer.Mode, "en", CommentarySource.Model));

        await GetAsync($"/api/matches/{_id}/recap?lang=es");
        await GetAsync($"/api/matches/{_id}/recap?lang=es");
        await GetAsync($"/api/matches/{_id}/recap?lang=en");
        await GetAsync($"/api/matches/{_id}/recap?lang=en");

        _squad.Invocations.Count(i => i.Method.Name == nameof(ISquad.RecapAsync)).Should().Be(3, "only the exact English recap is cached");
    }

    [Test]
    public async Task The_list_is_never_cached()
    {
        _matches.Setup(m => m.ListMatchesAsync(It.IsAny<MatchStatus?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchList([Summary(_id, MatchStatus.FullTime)]));

        await GetAsync("/api/matches");
        await GetAsync("/api/matches");

        Reads(nameof(IMatchReadModel.ListMatchesAsync)).Should().Be(2);
    }

    [Test]
    public void Only_the_three_finished_match_reads_carry_the_cache_policy()
    {
        var cached = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IOutputCachePolicy>() is not null)
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);

        cached.Should().BeEquivalentTo("GetMatch", "GetMoments", "GetRecap");
    }

    [TestCase("demo-1", true)]
    [TestCase(_id, false)]
    [TestCase(_id, true)]
    public void A_demo_lap_is_never_cached_because_each_replica_plays_it_on_its_own_clock(string id, bool fullTime)
    {
        var cacheable = IsFinished(FinishedMatchRead.Detail, TypedResults.Ok(Detail(id, fullTime)), finishedBefore: false);

        cacheable.Should().Be(id == _id && fullTime);
    }

    [Test]
    public void Moments_read_before_the_match_was_known_to_be_finished_are_not_cached()
    {
        IReadOnlyList<MomentView> moments = [Moment(MomentTier.Key, Line("en"))];

        IsFinished(FinishedMatchRead.Moments, TypedResults.Ok(moments), finishedBefore: false).Should().BeFalse();
        IsFinished(FinishedMatchRead.Moments, TypedResults.Ok(moments), finishedBefore: true).Should().BeTrue();
    }

    [Test]
    public void Moments_with_a_template_key_line_are_not_cached()
    {
        IReadOnlyList<MomentView> moments = [Moment(MomentTier.Key, Line("en") with { Source = CommentarySource.Template })];

        IsFinished(FinishedMatchRead.Moments, TypedResults.Ok(moments), finishedBefore: true).Should().BeFalse();
    }

    [TestCase("es", ViewingMode.Casual, "es-MX", ViewingMode.Casual)]
    [TestCase("en", ViewingMode.Casual, "es", ViewingMode.Casual)]
    [TestCase("en", ViewingMode.Casual, "en", ViewingMode.Analyst)]
    public void Moments_with_a_fallback_language_or_mode_line_are_not_cached(string lineLanguage, ViewingMode lineMode, string language, ViewingMode mode)
    {
        IReadOnlyList<MomentView> moments = [Moment(MomentTier.Key, Line(lineLanguage, lineMode))];

        FinishedMatchCache.IsFinished(FinishedMatchRead.Moments, TypedResults.Ok(moments), true, mode, language).Should().BeFalse();
    }

    [Test]
    public void Moments_whose_every_key_line_is_the_models_in_the_exact_language_and_mode_are_cached()
    {
        IReadOnlyList<MomentView> moments =
        [
            Moment(MomentTier.Key, Line("es-MX", ViewingMode.Analyst)),
            Moment(MomentTier.Notable, Line("en") with { Source = CommentarySource.Template }),
            Moment(MomentTier.Key, Line("es-mx", ViewingMode.Analyst)),
        ];

        FinishedMatchCache.IsFinished(FinishedMatchRead.Moments, TypedResults.Ok(moments), true, ViewingMode.Analyst, "es-MX").Should().BeTrue();
    }

    [Test]
    public void A_recap_is_cached_only_in_its_own_language_and_mode()
    {
        var recap = new MatchRecap(_id, "Full time", "Text", ViewingMode.Casual, "es", CommentarySource.Model);

        FinishedMatchCache.IsFinished(FinishedMatchRead.Recap, TypedResults.Ok(recap), false, ViewingMode.Casual, "es").Should().BeTrue();
        FinishedMatchCache.IsFinished(FinishedMatchRead.Recap, TypedResults.Ok(recap), false, ViewingMode.Casual, "es-MX").Should().BeFalse();
        FinishedMatchCache.IsFinished(FinishedMatchRead.Recap, TypedResults.Ok(recap), false, ViewingMode.Analyst, "es").Should().BeFalse();
    }

    [Test]
    public void A_problem_is_never_cached() =>
        IsFinished(FinishedMatchRead.Detail, TypedResults.NotFound(), finishedBefore: true).Should().BeFalse();

    private static bool IsFinished(FinishedMatchRead read, object result, bool finishedBefore) =>
        FinishedMatchCache.IsFinished(read, result, finishedBefore, ViewingMode.Casual, "en");

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync();
    }

    private int Reads(string method) => _matches.Invocations.Count(i => i.Method.Name == method);

    private static MatchSummary Summary(string id, MatchStatus status) => new(
        id, MatchKind.Fixture, status, new MatchTeam("home", "Home Town", "HOM", "/crest/home"), new MatchTeam("away", "Away Rovers", "AWY", "/crest/away"),
        new Score(1, 0), _clock, DateTimeOffset.UnixEpoch, "2026", 1, "synthetic");

    private static MatchDetail Detail(string id, bool fullTime) => new(
        Summary(id, fullTime ? MatchStatus.FullTime : MatchStatus.Live), "Elm Park", "A. Referee", [], [],
        new LiveState(id, 10, _clock, new Score(1, 0), TeamStats.Empty, TeamStats.Empty, fullTime));

    private static MomentView Moment(MomentTier tier, CommentaryLine? line) =>
        new($"{_id}:1", tier, "Goal", 1, _clock, TeamSide.Home, null, "xG 0.41 from 11 m", line);

    private static CommentaryLine Line(string language, ViewingMode mode = ViewingMode.Casual) =>
        new(_id, $"{_id}:1", 1, MomentTier.Key, CommentarySource.Model, "What a goal.", mode, language);
}
