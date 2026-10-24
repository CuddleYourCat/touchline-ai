using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Touchline.Agents;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Pitch;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;

namespace Touchline.Tests.Unit.Agents;

[TestFixture]
public sealed class CommentaryWorkflowTests
{
    private const string _cleanLines = """{"casual":"Tom Okafor scores for North Bank FC!","analyst":"Okafor finishes a 0.4 xG chance after a quick switch."}""";
    private const string _inventedLines = """{"casual":"John Smith scores!","analyst":"Smith finishes it."}""";
    private const string _spanishLines = """{"casual":"Okafor marca un gol.","analyst":"Okafor convierte la ocasión."}""";

    private static readonly MatchContext _context = new(
        "demo-1",
        ["Tom Okafor", "Kwame Mensah"],
        ["Luca Bianchi", "Sam Hart"],
        ["North Bank FC", "Riverside Athletic", "Harbour Lane", "Dana Whitfield"],
        HomeScore: 1,
        AwayScore: 0);

    private static readonly KeyMoment _moment = new(
        KeyMomentKind.Goal,
        new MatchEvent
        {
            Sequence = 412,
            MatchId = "demo-1",
            PossessionId = 31,
            Time = MatchTime.At(MatchPeriod.FirstHalf, 23),
            Type = MatchEventType.Pass,
            Team = TeamSide.Home,
            PlayerId = "nbfc-09",
            Start = new PitchPosition(100, 40)
        },
        "Goal: North Bank FC lead 1-0 after 23 minutes.");

    private ServiceProvider _provider = null!;
    private FakeChatClient _chat = null!;
    private ConcurrentQueue<CommentaryLine> _saved = null!;
    private Mock<ICommentaryStore> _store = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _chat = new FakeChatClient();
        _saved = new ConcurrentQueue<CommentaryLine>();
        _store = new Mock<ICommentaryStore>();
        _store.Setup(s => s.SaveAsync(It.IsAny<CommentaryLine>(), It.IsAny<CancellationToken>()))
            .Callback<CommentaryLine, CancellationToken>((line, _) => _saved.Enqueue(line))
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _chat.Dispose();
        _provider.Dispose();
    }

    [Test]
    public async Task The_happy_path_stores_both_modes_in_every_language_as_model_lines()
    {
        _chat.Returns(_cleanLines);
        _chat.DefaultResponse = FakeChatClient.Reply(_spanishLines, 10, 2);

        var outcome = await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _saved.Should().HaveCount(6).And.OnlyContain(line => line.Source == CommentarySource.Model);
        _saved.Select(line => (line.Language, line.Mode)).Should().BeEquivalentTo(
            [("en", ViewingMode.Casual), ("en", ViewingMode.Analyst), ("es", ViewingMode.Casual),
             ("es", ViewingMode.Analyst), ("fr", ViewingMode.Casual), ("fr", ViewingMode.Analyst)]);
        _saved.Should().OnlyContain(line => line.MomentId == "demo-1:412" && line.Tier == MomentTier.Key);
        _saved.Single(line => line.Language == "en" && line.Mode == ViewingMode.Casual).Text
            .Should().Be("Tom Okafor scores for North Bank FC!");
        outcome.Lines.Should().HaveCount(6);
        _chat.Requests.Select(AgentOf).Should().BeEquivalentTo(["Commentator", "Localiser", "Localiser"]);
    }

    [Test]
    public async Task A_guardrail_miss_then_a_clean_retry_stores_model_lines()
    {
        _chat.Returns(_inventedLines).Returns(_cleanLines);
        _chat.DefaultResponse = FakeChatClient.Reply(_spanishLines, 10, 2);

        await Workflow("en").RunAsync(_moment, _context, CancellationToken.None);

        _saved.Should().HaveCount(2).And.OnlyContain(line => line.Source == CommentarySource.Model);
        _chat.Requests.Should().HaveCount(2);
        _chat.Requests[1].Messages[^1].Text.Should().Contain("John Smith");
        _chat.Requests[1].Messages.Where(m => m.Role == ChatRole.System).Should().ContainSingle()
            .Which.Text.Should().NotContain("John Smith");
    }

    [Test]
    public async Task Two_guardrail_misses_store_english_template_lines_only()
    {
        _chat.Returns(_inventedLines).Returns(_inventedLines);

        var outcome = await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _saved.Should().HaveCount(2).And.OnlyContain(line =>
            line.Source == CommentarySource.Template && line.Language == "en");
        _chat.Requests.Should().HaveCount(2).And.OnlyContain(r => AgentOf(r) == "Commentator");
        outcome.Lines.Should().HaveCount(2);
    }

    [Test]
    public async Task The_template_strips_the_clock_and_the_analyst_line_adds_the_score()
    {
        _chat.Throws(new HttpRequestException("The model is unavailable."));
        var moment = _moment with { Reason = "23:00: Goal: North Bank FC lead 1-0 after 23 minutes." };
        var context = _context with { HomeScore = 2, AwayScore = 1 };

        await Workflow("en").RunAsync(moment, context, CancellationToken.None);

        _saved.Single(line => line.Mode == ViewingMode.Casual).Text
            .Should().Be("Goal: North Bank FC lead 1-0 after 23 minutes.");
        _saved.Single(line => line.Mode == ViewingMode.Analyst).Text
            .Should().Be("Goal: North Bank FC lead 1-0 after 23 minutes. Score: 2–1.");
    }

    [Test]
    public async Task A_content_filter_block_on_the_commentator_or_a_localiser_is_tagged_content_filter()
    {
        var reasons = new ConcurrentQueue<string>();
        var factory = _provider.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == SquadMetrics.FallbackCounterName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var pair in tags)
            {
                if (pair.Key == SquadMetrics.ReasonTag)
                {
                    reasons.Enqueue(pair.Value?.ToString() ?? "");
                }
            }
        });
        listener.Start();
        var filtered = FakeChatClient.Reply(_spanishLines, 10, 2);
        filtered.FinishReason = ChatFinishReason.ContentFilter;
        _chat.Throws(ModelFailuresTests.Failure(400, """{"error":{"code":"content_filter"}}"""));

        await Workflow("en").RunAsync(_moment, _context, CancellationToken.None);
        _chat.Returns(_cleanLines).Returns(filtered);
        await Workflow("es").RunAsync(_moment, _context, CancellationToken.None);

        reasons.Should().Equal(ModelFailures.ContentFilterReason, ModelFailures.ContentFilterReason);
    }

    [Test]
    public async Task Regional_english_tags_are_not_sent_to_a_localiser()
    {
        _chat.Returns(_cleanLines);

        await Workflow("en", "en-GB", "en-US", "EN").RunAsync(_moment, _context, CancellationToken.None);

        _chat.Requests.Should().ContainSingle(r => AgentOf(r) == "Commentator");
        _chat.Requests.Should().HaveCount(1);
        _saved.Should().HaveCount(2).And.OnlyContain(line => line.Language == "en");
    }

    [Test]
    public async Task An_untranslated_reply_skips_that_language_and_records_the_fallback()
    {
        var reasons = new ConcurrentQueue<string>();
        using var listener = FallbackListener(reasons);
        _chat.Handler = (request, _) => Task.FromResult(FakeChatClient.Reply(
            AgentOf(request) == "Commentator" || LanguageOf(request) == "fr"
                ? """{"casual":"tom okafor  scores for North Bank FC!","analyst":"Okafor finishes a 0.4 xG chance after a quick switch. "}"""
                : _spanishLines,
            10,
            2));

        await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _saved.Select(line => line.Language).Distinct().Should().BeEquivalentTo(["en", "es"]);
        reasons.Should().ContainSingle().Which.Should().Be("untranslated");
    }

    [Test]
    public async Task A_translation_opening_with_an_ordinary_word_is_stored()
    {
        _chat.Handler = (request, _) => Task.FromResult(FakeChatClient.Reply(
            AgentOf(request) == "Commentator" ? _cleanLines
                : LanguageOf(request) == "fr" ? """{"casual":"Quel but de Tom Okafor pour North Bank FC !","analyst":"Superbe finition d'Okafor."}"""
                : """{"casual":"¡Qué golazo de Okafor!","analyst":"Increíble. Okafor convierte una ocasión de 0,4 xG."}""",
            10,
            2));

        await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _saved.Should().HaveCount(6);
    }

    [Test]
    public async Task The_commentator_throwing_stores_template_lines_and_does_not_throw()
    {
        _chat.Throws(new HttpRequestException("The model is unavailable."));

        var run = async () => await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        await run.Should().NotThrowAsync();
        _saved.Should().HaveCount(2).And.OnlyContain(line => line.Source == CommentarySource.Template && line.Language == "en");
        _saved.Select(line => line.Mode).Should().BeEquivalentTo([ViewingMode.Casual, ViewingMode.Analyst]);
    }

    [Test]
    public async Task The_store_throwing_does_not_throw_out_of_the_run()
    {
        _chat.Returns(_cleanLines);
        _store.Setup(s => s.SaveAsync(It.IsAny<CommentaryLine>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos DB is unavailable."));

        var outcome = await Workflow("en").RunAsync(_moment, _context, CancellationToken.None);

        outcome.Lines.Should().BeEmpty();
        outcome.AllStored.Should().BeFalse();
        outcome.EnglishModelLineLost.Should().BeTrue();
        outcome.FailedSaves.Should().Contain(line => line.Source == CommentarySource.Model)
            .And.Contain(line => line.Source == CommentarySource.Template);
    }

    [Test]
    public async Task A_failed_template_save_alone_is_reported_but_loses_no_model_line()
    {
        _chat.Throws(new HttpRequestException("The model is unavailable."));
        _store.Setup(s => s.SaveAsync(It.IsAny<CommentaryLine>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos DB is unavailable."));

        var outcome = await Workflow("en").RunAsync(_moment, _context, CancellationToken.None);

        outcome.AllStored.Should().BeFalse();
        outcome.FailedSaves.Should().NotBeEmpty().And.OnlyContain(line => line.Source == CommentarySource.Template);
        outcome.EnglishModelLineLost.Should().BeFalse();
    }

    [Test]
    public async Task A_clean_run_reports_every_line_stored()
    {
        _chat.Returns(_cleanLines);

        var outcome = await Workflow("en").RunAsync(_moment, _context, CancellationToken.None);

        outcome.AllStored.Should().BeTrue();
        outcome.FailedSaves.Should().BeEmpty();
    }

    [Test]
    public async Task With_no_models_the_template_is_stored_without_any_model_call()
    {
        var reasons = new ConcurrentQueue<string>();
        using var listener = FallbackListener(reasons);
        _chat.Returns(_cleanLines);
        var workflow = new CommentaryWorkflow(
            _chat,
            _store.Object,
            Options.Create(new SquadOptions { Languages = ["en", "es"], ModelsAvailable = false }),
            new SquadMetrics(_provider.GetRequiredService<IMeterFactory>()),
            NullLogger<CommentaryWorkflow>.Instance);

        var outcome = await workflow.RunAsync(_moment, _context, CancellationToken.None);

        _chat.Requests.Should().BeEmpty();
        _saved.Should().HaveCount(2).And.OnlyContain(line => line.Source == CommentarySource.Template && line.Language == "en");
        outcome.AllStored.Should().BeTrue();
        reasons.Should().Equal("no-models");
    }

    [Test]
    public async Task An_unknown_score_is_left_out_of_the_template_and_the_prompt()
    {
        _chat.Returns(_inventedLines).Returns(_inventedLines);
        var context = _context with { HomeScore = null, AwayScore = null };

        await Workflow("en").RunAsync(_moment, context, CancellationToken.None);

        _saved.Single(line => line.Mode == ViewingMode.Analyst).Text
            .Should().Be("Goal: North Bank FC lead 1-0 after 23 minutes.");
        _chat.Requests[0].Messages.Should().NotContain(m => m.Text.Contains("scoreAfter", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_known_score_is_in_the_prompt()
    {
        _chat.Returns(_cleanLines);

        await Workflow("en").RunAsync(_moment, _context, CancellationToken.None);

        _chat.Requests[0].Messages.Should().Contain(m => m.Text.Contains("\"scoreAfter\":{\"home\":1,\"away\":0}", StringComparison.Ordinal));
    }

    [Test]
    public async Task One_localiser_throwing_still_stores_the_other_languages()
    {
        _chat.Handler = (request, _) => AgentOf(request) == "Commentator"
            ? Task.FromResult(FakeChatClient.Reply(_cleanLines, 10, 2))
            : LanguageOf(request) == "fr"
                ? Task.FromException<ChatResponse>(new HttpRequestException("Throttled."))
                : Task.FromResult(FakeChatClient.Reply(_spanishLines, 10, 2));

        await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _saved.Select(line => line.Language).Distinct().Should().BeEquivalentTo(["en", "es"]);
        _saved.Should().HaveCount(4).And.OnlyContain(line => line.Source == CommentarySource.Model);
    }

    [Test]
    public async Task A_localiser_guardrail_miss_skips_that_language()
    {
        _chat.Handler = (request, _) => Task.FromResult(FakeChatClient.Reply(
            AgentOf(request) == "Commentator" ? _cleanLines
                : LanguageOf(request) == "fr" ? """{"casual":"But de John Smith.","analyst":"Smith marque."}""" : _spanishLines,
            10,
            2));

        await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _saved.Select(line => line.Language).Distinct().Should().BeEquivalentTo(["en", "es"]);
    }

    [Test]
    public async Task The_localisers_run_concurrently()
    {
        var events = new ConcurrentQueue<string>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        _chat.Handler = async (request, cancellationToken) =>
        {
            if (AgentOf(request) == "Commentator")
            {
                return FakeChatClient.Reply(_cleanLines, 10, 2);
            }

            var language = LanguageOf(request);
            events.Enqueue($"start:{language}");
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            try
            {
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                events.Enqueue("timed-out");
            }

            events.Enqueue($"end:{language}");
            return FakeChatClient.Reply(_spanishLines, 10, 2);
        };

        await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        events.Take(2).Should().OnlyContain(e => e.StartsWith("start:", StringComparison.Ordinal));
        events.Should().NotContain("timed-out");
        _saved.Should().HaveCount(6);
    }

    [Test]
    public async Task No_system_message_contains_roster_names_which_arrive_as_user_data()
    {
        _chat.Returns(_inventedLines).Returns(_cleanLines);
        _chat.DefaultResponse = FakeChatClient.Reply(_spanishLines, 10, 2);
        string[] names = [.. _context.RosterNames, .. _context.OtherKnownNames];

        await Workflow().RunAsync(_moment, _context, CancellationToken.None);

        _chat.Requests.Should().HaveCount(4);
        foreach (var request in _chat.Requests)
        {
            var system = string.Join('\n', request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            names.Should().OnlyContain(name => !system.Contains(name, StringComparison.OrdinalIgnoreCase));
            request.Messages.First(m => m.Role == ChatRole.User).Text.Should().Contain("Tom Okafor").And.Contain("Sam Hart");
        }
    }

    private static MeterListener FallbackListener(ConcurrentQueue<string> reasons)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument is { Meter.Name: SquadMetrics.MeterName, Name: SquadMetrics.FallbackCounterName })
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == SquadMetrics.ReasonTag && tag.Value is string reason)
                {
                    reasons.Enqueue(reason);
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static string AgentOf(FakeChatRequest request) =>
        request.Options?.AdditionalProperties?[SquadAgentChatOptions.AgentPropertyKey] as string ?? "";

    private static string LanguageOf(FakeChatRequest request) =>
        request.Messages.Any(m => m.Role == ChatRole.User && m.Text.Contains("\"language\":\"fr\"", StringComparison.Ordinal))
            ? "fr"
            : "es";

    private CommentaryWorkflow Workflow(params string[] languages) =>
        new(
            _chat,
            _store.Object,
            Options.Create(new SquadOptions { Languages = languages.Length == 0 ? ["en", "es", "fr"] : [.. languages] }),
            new SquadMetrics(_provider.GetRequiredService<IMeterFactory>()),
            NullLogger<CommentaryWorkflow>.Instance);
}
