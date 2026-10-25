using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Touchline.Agents;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Search;
using Touchline.Tests.Unit.Agents;
using static Touchline.Tests.Unit.Search.SearchTestSupport;

namespace Touchline.Tests.Unit.Search;

[TestFixture]
public sealed class KeyMomentSummaryWriterTests
{
    private const string _goodSummary = "Theo Lindqvist and Elias Brook moved the ball for North Bank FC, and the move ended in a goal.";

    private ServiceProvider _services = null!;
    private FakeChatClient _chat = null!;
    private FallbackRecorder _fallbacks = null!;
    private SquadOptions _squad = null!;
    private PossessionSearchOptions _search = null!;
    private IReadOnlyList<SummarizedPossession> _possessions = null!;
    private PossessionRoster _roster = null!;

    [SetUp]
    public void SetUp()
    {
        _services = Metrics();
        _chat = new FakeChatClient();
        _fallbacks = new FallbackRecorder(_services.GetRequiredService<IMeterFactory>());
        _squad = new SquadOptions();
        _search = new PossessionSearchOptions();
        var events = SeededMatch();
        _roster = SeededRoster(events);
        _possessions = PossessionSummarizer.Summarize(MatchId, events, new KeyMomentDetector(), _roster);
    }

    [TearDown]
    public void TearDown()
    {
        _fallbacks.Dispose();
        _chat.Dispose();
        _services.Dispose();
    }

    private int KeyMomentCount => _possessions.Count(p => p.KeyMoment is not null);

    private KeyMomentSummaryWriter Writer(IChatClient? client = null) => new(
        client ?? _chat,
        Options.Create(_squad),
        Options.Create(_search),
        new SearchMetrics(_services.GetRequiredService<IMeterFactory>()),
        NullLogger<KeyMomentSummaryWriter>.Instance);

    [Test]
    public async Task A_key_moment_possession_gets_the_model_summary_and_the_rest_keep_the_template()
    {
        _chat.DefaultResponse = FakeChatClient.Reply(_goodSummary, 100, 20);

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        documents.Should().HaveCount(_possessions.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            documents[i].Summary.Should().Be(_possessions[i].KeyMoment is null ? _possessions[i].Document.Summary : _goodSummary);
            documents[i].Id.Should().Be(_possessions[i].Document.Id);
        }

        _chat.Requests.Should().HaveCount(KeyMomentCount);
        _fallbacks.Reasons.Should().BeEmpty();
    }

    [Test]
    public async Task The_request_has_a_fixed_system_prompt_a_delimited_data_block_and_names_its_agent()
    {
        _chat.DefaultResponse = FakeChatClient.Reply(_goodSummary, 100, 20);

        await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        // The writer sends its requests concurrently, so their order varies: every one is checked.
        var requests = _chat.Requests;
        requests.Should().NotBeEmpty();
        foreach (var request in requests)
        {
            request.Messages[0].Role.Should().Be(ChatRole.System);
            request.Messages[0].Text.Should().Be(KeyMomentSummaryWriter.SystemPrompt);
            request.Messages[1].Role.Should().Be(ChatRole.User);
            request.Messages[1].Text.Should().StartWith("<possession-data>").And.EndWith("</possession-data>");
            request.Messages[1].Text.Should().NotContain("nbk-", "player IDs never reach the model");
            request.Options!.AdditionalProperties![SquadAgentChatOptions.AgentPropertyKey].Should().Be(nameof(SquadAgent.Indexer));
            request.Options.MaxOutputTokens.Should().Be(_squad.MaxOutputTokens.Indexer);
        }

        requests.Should().Contain(r => r.Messages[1].Text.Contains("Lindqvist"), "names come from the profiles");
    }

    [Test]
    public async Task Goals_use_the_cap_before_big_chances()
    {
        _search.MaxModelSummariesPerMatch = 3;
        _chat.DefaultResponse = FakeChatClient.Reply(_goodSummary, 100, 20);

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        var modelWritten = documents.Where((d, i) => d.Summary == _goodSummary).Select(d => d.EndedIn);
        modelWritten.Should().Equal("goal", "goal", "goal");
        _fallbacks.Reasons.Should().Equal(SearchMetrics.SummaryCap);
    }

    [Test]
    public async Task Chaos_a_model_that_is_down_leaves_every_template_and_never_throws()
    {
        _chat.Handler = (_, _) => throw new InvalidOperationException("503 from the model");

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        documents.Select(d => d.Summary).Should().Equal(_possessions.Select(p => p.Document.Summary));
        _fallbacks.Reasons.Should().HaveCount(KeyMomentCount).And.OnlyContain(r => r == SearchMetrics.SummaryModelFailed);
    }

    [Test]
    public async Task A_summary_naming_someone_off_the_roster_is_dropped_for_the_template()
    {
        _chat.DefaultResponse = FakeChatClient.Reply("Zlatan Ibrahimovic ran through for North Bank FC.", 100, 20);

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        documents.Select(d => d.Summary).Should().Equal(_possessions.Select(p => p.Document.Summary));
        _fallbacks.Reasons.Should().HaveCount(KeyMomentCount).And.OnlyContain(r => r == SearchMetrics.SummaryGuardrail);
    }

    [Test]
    public async Task A_blown_token_budget_leaves_the_template()
    {
        _squad.MaxInputTokens = 8;
        using var budgeted = new TokenBudgetChatClient(_chat, SquadModels.Small, "gpt-test", _squad, new SquadMetrics(_services.GetRequiredService<IMeterFactory>()));

        var documents = await Writer(budgeted).WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        documents.Select(d => d.Summary).Should().Equal(_possessions.Select(p => p.Document.Summary));
        _fallbacks.Reasons.Should().HaveCount(KeyMomentCount).And.OnlyContain(r => r == SearchMetrics.SummaryBudget);
        _chat.Requests.Should().BeEmpty("a refused call never reaches the model");
    }

    [Test]
    public async Task After_the_cap_the_rest_use_the_template_and_are_counted()
    {
        _search.MaxModelSummariesPerMatch = 1;
        _chat.DefaultResponse = FakeChatClient.Reply(_goodSummary, 100, 20);

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        _chat.Requests.Should().ContainSingle();
        documents.Count(d => d.Summary == _goodSummary).Should().Be(1);
        _fallbacks.Reasons.Should().HaveCount(KeyMomentCount - 1).And.OnlyContain(r => r == SearchMetrics.SummaryCap);
    }

    [Test]
    public async Task The_cap_is_counted_once_for_the_match_not_once_per_skipped_possession()
    {
        _search.MaxModelSummariesPerMatch = 1;
        _chat.DefaultResponse = FakeChatClient.Reply(_goodSummary, 100, 20);

        await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        _fallbacks.Measurements.Should().ContainSingle().Which.Should().Be((SearchMetrics.SummaryCap, (long)(KeyMomentCount - 1)));
    }

    [Test]
    public async Task A_redelivery_makes_no_model_call_and_is_counted_once()
    {
        _chat.DefaultResponse = FakeChatClient.Reply(_goodSummary, 100, 20);

        var documents = await Writer().WriteAsync(_possessions, _roster, 2, CancellationToken.None);

        documents.Select(d => d.Summary).Should().Equal(_possessions.Select(p => p.Document.Summary));
        _chat.Requests.Should().BeEmpty();
        _fallbacks.Measurements.Should().ContainSingle().Which.Should().Be((SearchMetrics.SummaryRedelivery, (long)KeyMomentCount));
    }

    [Test]
    public async Task Model_calls_run_concurrently_up_to_the_limit_and_the_documents_stay_in_possession_order()
    {
        _search.MaxSummaryConcurrency = 2;
        var running = 0;
        var peak = 0;
        _chat.Handler = async (_, token) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.Delay(40, token);
            Interlocked.Decrement(ref running);
            return FakeChatClient.Reply(_goodSummary, 100, 20);
        };

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        peak.Should().Be(2);
        documents.Select(d => d.Id).Should().Equal(_possessions.Select(p => p.Document.Id));
        _chat.Requests.Should().HaveCount(KeyMomentCount);
    }

    [Test]
    public async Task With_no_models_every_summary_is_the_template_and_nothing_is_counted()
    {
        _squad.ModelsAvailable = false;

        var documents = await Writer().WriteAsync(_possessions, _roster, 1, CancellationToken.None);

        documents.Select(d => d.Summary).Should().Equal(_possessions.Select(p => p.Document.Summary));
        _chat.Requests.Should().BeEmpty();
        _fallbacks.Reasons.Should().BeEmpty();
    }

    [Test]
    public async Task A_cancelled_run_stops_instead_of_falling_back()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _chat.Handler = (_, token) => Task.FromCanceled<ChatResponse>(token);

        var act = () => Writer().WriteAsync(_possessions, _roster, 1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
