using Azure;
using Azure.AI.ContentSafety;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.Tests.Unit.MatchCentre;

/// <summary>The Content Safety name guard (ADR-049), with the Azure client mocked.</summary>
[TestFixture]
public class ContentSafetyNameGuardTests
{
    private Mock<ContentSafetyClient> _client = null!;
    private ServiceProvider _services = null!;
    private HybridCache _cache = null!;
    private AnalyzeTextOptions? _lastRequest;

    [SetUp]
    public void SetUp()
    {
        _client = new Mock<ContentSafetyClient>();
        _services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        _cache = _services.GetRequiredService<HybridCache>();
        _lastRequest = null;
        Respond(Clean());
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task A_clean_name_is_allowed_and_the_blocklist_halts_on_a_hit()
    {
        var verdicts = await Guard().CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        verdicts.Should().Equal(new NameVerdict(NameKind.Team, "Riverside Rovers", true, null));
        _lastRequest!.BlocklistNames.Should().Equal("touchline-real-names");
        _lastRequest.HaltOnBlocklistHit.Should().BeTrue();
    }

    [Test]
    public async Task A_blocklist_hit_is_refused_with_the_real_name_reason()
    {
        Respond(ContentSafetyModelFactory.AnalyzeTextResult(
            [ContentSafetyModelFactory.TextBlocklistMatch("touchline-real-names", "item-1", "blocked united")],
            []));

        var verdicts = await Guard().CheckAsync([new(NameKind.Team, "Blocked United")], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeFalse();
        verdicts.Single().Reason.Should().Be(ContentSafetyNameGuard.RealNameReason);
    }

    [Test]
    public async Task A_harm_hit_is_refused_with_a_generic_reason_that_says_nothing_more()
    {
        Respond(ContentSafetyModelFactory.AnalyzeTextResult([], [ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Hate, 4)]));

        var verdicts = await Guard().CheckAsync([new(NameKind.Player, "Some Name")], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeFalse();
        verdicts.Single().Reason.Should().Be(ContentSafetyNameGuard.NotAllowedReason);
    }

    [Test]
    public async Task A_harm_score_below_the_threshold_is_allowed()
    {
        Respond(ContentSafetyModelFactory.AnalyzeTextResult([], [ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Violence, 2)]));

        var verdicts = await Guard().CheckAsync([new(NameKind.Team, "Hammerhead Athletic")], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeTrue();
    }

    [Test]
    public async Task The_same_name_again_is_answered_from_the_cache_without_a_second_call()
    {
        var guard = Guard();

        await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);
        await guard.CheckAsync([new(NameKind.Team, "  riverside   ROVERS ")], CancellationToken.None);

        Calls().Should().Be(1);
    }

    [Test]
    public async Task The_cache_ignores_the_kind_so_one_text_is_one_call_across_kinds()
    {
        var guard = Guard();

        await guard.CheckAsync([new(NameKind.Team, "Alder"), new(NameKind.Player, "Alder")], CancellationToken.None);
        await guard.CheckAsync([new(NameKind.Venue, "alder")], CancellationToken.None);

        Calls().Should().Be(1);
    }

    [TestCase("Blocked United")]
    [TestCase("Blöcked Üníted")]
    [TestCase("Blосked Unіted")]
    [TestCase("ΒLOCKED UNITED")]
    [TestCase("Block.ed Un'ited")]
    public async Task A_disguised_spelling_of_a_blocked_term_is_refused_because_the_normalised_text_is_what_is_checked(string typed)
    {
        // The mock blocks only the exact plain term, as the real blocklist does.
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AnalyzeTextOptions request, CancellationToken _) =>
            {
                _lastRequest = request;
                return Response.FromValue(
                    request.Text == "blocked united"
                        ? ContentSafetyModelFactory.AnalyzeTextResult([ContentSafetyModelFactory.TextBlocklistMatch("touchline-real-names", "item-1", "blocked united")], [])
                        : Clean(),
                    Mock.Of<Response>());
            });

        var verdicts = await Guard().CheckAsync([new(NameKind.Team, typed)], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeFalse();
        verdicts.Single().Name.Should().Be(typed);
        _lastRequest!.Text.Should().Be("blocked united");
    }

    [Test]
    public async Task A_name_repeated_inside_one_request_costs_one_call_and_every_slot_is_answered()
    {
        var verdicts = await Guard().CheckAsync([new(NameKind.Team, "Alder"), new(NameKind.Team, "Alder"), new(NameKind.Venue, "Elm Park")], CancellationToken.None);

        verdicts.Select(v => v.Name).Should().Equal("Alder", "Alder", "Elm Park");
        Calls().Should().Be(2);
    }

    [Test]
    public async Task A_failure_is_never_cached_so_the_next_try_asks_again()
    {
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(429, "throttled"));
        var guard = Guard();

        var failed = () => guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);
        await failed.Should().ThrowAsync<NameGuardUnavailableException>();

        Respond(Clean());
        var verdicts = await guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        verdicts.Single().Allowed.Should().BeTrue();
    }

    [Test]
    public async Task A_slow_call_times_out_as_unavailable_rather_than_hanging()
    {
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (AnalyzeTextOptions _, CancellationToken token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return Response.FromValue(Clean(), Mock.Of<Response>());
            });
        var guard = Guard(new ContentSafetyOptions { Timeout = TimeSpan.FromSeconds(1) });

        var check = () => guard.CheckAsync([new(NameKind.Team, "Riverside Rovers")], CancellationToken.None);

        await check.Should().ThrowAsync<NameGuardUnavailableException>();
    }

    [Test]
    public async Task The_callers_own_cancellation_is_not_an_outage()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .Returns((AnalyzeTextOptions _, CancellationToken token) => Task.FromCanceled<Response<AnalyzeTextResult>>(token));

        var check = () => Guard().CheckAsync([new(NameKind.Team, "Riverside Rovers")], cancelled.Token);

        await check.Should().ThrowAsync<OperationCanceledException>();
    }

    private int Calls() => _client.Invocations.Count(i => i.Method.Name == nameof(ContentSafetyClient.AnalyzeTextAsync));

    private ContentSafetyNameGuard Guard(ContentSafetyOptions? options = null) =>
        new(_client.Object, _cache, Options.Create(options ?? new ContentSafetyOptions()), NullLogger<ContentSafetyNameGuard>.Instance);

    private static AnalyzeTextResult Clean() =>
        ContentSafetyModelFactory.AnalyzeTextResult([], [ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Hate, 0)]);

    private void Respond(AnalyzeTextResult result) =>
        _client.Setup(c => c.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AnalyzeTextOptions request, CancellationToken _) =>
            {
                _lastRequest = request;
                return Response.FromValue(result, Mock.Of<Response>());
            });
}
