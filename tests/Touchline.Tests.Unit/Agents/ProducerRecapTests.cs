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
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;

namespace Touchline.Tests.Unit.Agents;

[TestFixture]
public sealed class ProducerRecapTests
{
    private const string _matchId = "demo-1";

    private static readonly MatchFactSheet _sheet = new(
        _matchId,
        "North Bank FC",
        "Riverside Athletic",
        1,
        0,
        "90:00",
        FullTime: true,
        TeamStats.Empty,
        TeamStats.Empty,
        ["Tom Okafor", "Kwame Mensah"],
        ["Luca Bianchi", "Sam Hart"],
        ["North Bank FC", "North Bank", "Riverside Athletic", "Riverside", "Harbour Lane", "Dana Whitfield"]);

    private static readonly MomentFact _goal = new("demo-1:412", "23:00", "Goal", TeamSide.Home, "Goal: North Bank FC lead 1-0.");

    private static readonly EventFact _shot = new("event:demo-1:412", "23:00", MatchEventType.Shot, TeamSide.Home, "Tom Okafor", "Goal");

    private ServiceProvider _provider = null!;
    private FakeChatClient _chat = null!;
    private Mock<IMatchFacts> _facts = null!;
    private SquadOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _chat = new FakeChatClient();
        _options = new SquadOptions();
        _facts = new Mock<IMatchFacts>();
        _facts.Setup(f => f.GetSummaryAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync(_sheet);
        _facts.Setup(f => f.GetKeyMomentsAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync([_goal]);
        _facts.Setup(f => f.GetEventsAsync(_matchId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([_shot]);
    }

    [TearDown]
    public void TearDown()
    {
        _chat.Dispose();
        _provider.Dispose();
    }

    [Test]
    public async Task The_recap_is_written_from_the_facts_only_with_a_constant_system_prompt()
    {
        _chat.Returns(Reply("North Bank FC edge it", "Tom Okafor scored on 23 minutes."));

        var recap = await Producer().RecapAsync(_matchId, Viewer.Default, CancellationToken.None);

        recap.Should().Be(new MatchRecap(_matchId, "North Bank FC edge it", "Tom Okafor scored on 23 minutes.", ViewingMode.Casual, "en", CommentarySource.Model));
        var request = _chat.Requests.Should().ContainSingle().Subject;
        request.Messages.Should().HaveCount(2);
        request.Messages[0].Role.Should().Be(ChatRole.System);
        request.Messages[0].Text.Should().Be(ProducerRecap.SystemPrompt);
        var user = request.Messages[1].Text;
        user.Should().Contain("Goal: North Bank FC lead 1-0.").And.Contain("Tom Okafor").And.Contain("\"homeGoals\":1");
        _facts.Verify(f => f.GetEventsAsync(_matchId, 0, ProducerRecap.LastMinute, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task The_Producer_names_itself_and_caps_its_output_tokens()
    {
        _options.MaxOutputTokens.Producer = 321;
        _chat.Returns(Reply("North Bank FC edge it", "A tight match."));

        await Producer().RecapAsync(_matchId, Viewer.Default, CancellationToken.None);

        var options = _chat.Requests.Single().Options!;
        options.MaxOutputTokens.Should().Be(321);
        options.AdditionalProperties![SquadAgentChatOptions.AgentPropertyKey].Should().Be(nameof(SquadAgent.Producer));
    }

    [Test]
    public async Task The_recap_is_written_in_the_viewers_language_and_mode()
    {
        _chat.Returns(Reply("North Bank FC gana", "Tom Okafor marcó en el minuto 23."));
        var viewer = SpanishAnalyst();

        var recap = await Producer().RecapAsync(_matchId, viewer, CancellationToken.None);

        recap.Language.Should().Be("es");
        recap.Mode.Should().Be(ViewingMode.Analyst);
        recap.Text.Should().Be("Tom Okafor marcó en el minuto 23.");
        var user = _chat.Requests.Single().Messages[1].Text;
        user.Should().Contain("\"language\":\"es\"").And.Contain("\"mode\":\"Analyst\"");
    }

    [Test]
    public async Task An_unknown_name_gets_one_corrective_retry()
    {
        _chat.Returns(Reply("Bukayo Saka wins it", "A great finish."))
            .Returns(Reply("North Bank FC edge it", "Tom Okafor scored."));

        var recap = await Producer().RecapAsync(_matchId, Viewer.Default, CancellationToken.None);

        recap.Headline.Should().Be("North Bank FC edge it");
        _chat.Requests.Should().HaveCount(2);
        _chat.Requests[1].Messages.Last().Text.Should().Contain("aren't in this match");
    }

    [Test]
    public async Task Two_guardrail_misses_give_the_template_recap_and_count_a_fallback()
    {
        var fallbacks = new ConcurrentQueue<string>();
        using var listener = Listen(SquadMetrics.FallbackCounterName, SquadMetrics.ReasonTag, fallbacks);
        _chat.Returns(Reply("Bukayo Saka wins it", "A great finish."))
            .Returns(Reply("Bukayo Saka again", "Another finish."));

        var recap = await Producer().RecapAsync(_matchId, SpanishAnalyst(), CancellationToken.None);

        recap.Should().Be(ProducerRecap.Template(_sheet, [_goal], SpanishAnalyst()));
        recap.Language.Should().Be("en");
        recap.Text.Should().Contain("Goal: North Bank FC lead 1-0.");
        _chat.Requests.Should().HaveCount(ProducerRecap.MaxAttempts);
        fallbacks.Should().Equal("guardrail");
    }

    [Test]
    public async Task A_model_failure_gives_the_template_recap()
    {
        var fallbacks = new ConcurrentQueue<string>();
        using var listener = Listen(SquadMetrics.FallbackCounterName, SquadMetrics.ReasonTag, fallbacks);
        _chat.Throws(new HttpRequestException("Foundry is down."));

        var recap = await Producer().RecapAsync(_matchId, Viewer.Default, CancellationToken.None);

        recap.Headline.Should().Be("North Bank FC 1–0 Riverside Athletic");
        recap.Text.Should().StartWith("Full time: North Bank FC 1, Riverside Athletic 0.");
        fallbacks.Should().Equal("failed");
    }

    [Test]
    public async Task A_prompt_the_content_filter_blocks_counts_a_content_filter_fallback()
    {
        var fallbacks = new ConcurrentQueue<string>();
        using var listener = Listen(SquadMetrics.FallbackCounterName, SquadMetrics.ReasonTag, fallbacks);
        _chat.Throws(ModelFailuresTests.Failure(400, """{"error":{"code":"content_filter"}}"""));

        var produced = await Producer().WriteAsync(_matchId, Viewer.Default, CancellationToken.None);

        produced.Should().Be(new ProducedRecap(ProducerRecap.Template(_sheet, [_goal], Viewer.Default), FromModel: false));
        fallbacks.Should().Equal("content_filter");
    }

    [Test]
    public async Task A_completion_the_content_filter_cut_off_counts_a_content_filter_fallback()
    {
        var fallbacks = new ConcurrentQueue<string>();
        using var listener = Listen(SquadMetrics.FallbackCounterName, SquadMetrics.ReasonTag, fallbacks);
        var filtered = Reply("North Bank FC edge it", "Tom Okafor scored.");
        filtered.FinishReason = ChatFinishReason.ContentFilter;
        _chat.Returns(filtered);

        var produced = await Producer().WriteAsync(_matchId, Viewer.Default, CancellationToken.None);

        produced.FromModel.Should().BeFalse();
        produced.TransientFailure.Should().BeFalse("a content-filter block is permanent (ADR-052)");
        fallbacks.Should().Equal("content_filter");
    }

    [TestCase(429)]
    [TestCase(503)]
    public async Task A_transient_model_error_is_marked_so_the_caller_can_retry(int status)
    {
        _chat.Throws(ModelFailuresTests.Failure(status));

        var produced = await Producer().WriteAsync(_matchId, Viewer.Default, CancellationToken.None);

        produced.FromModel.Should().BeFalse();
        produced.TransientFailure.Should().BeTrue();
    }

    [Test]
    public async Task A_guardrail_miss_is_not_transient()
    {
        _chat.Returns(Reply("Bukayo Saka wins it", "A great finish."))
            .Returns(Reply("Bukayo Saka again", "Another finish."));

        var produced = await Producer().WriteAsync(_matchId, Viewer.Default, CancellationToken.None);

        produced.FromModel.Should().BeFalse();
        produced.TransientFailure.Should().BeFalse();
    }

    [Test]
    public async Task A_reply_that_is_not_a_recap_gives_the_template()
    {
        _chat.Returns("I can't help with that.");

        var recap = await Producer().RecapAsync(_matchId, Viewer.Default, CancellationToken.None);

        recap.Should().Be(ProducerRecap.Template(_sheet, [_goal], Viewer.Default));
    }

    [Test]
    public async Task Without_models_the_template_is_used_and_no_call_is_made()
    {
        _options.ModelsAvailable = false;

        var recap = await Producer().RecapAsync(_matchId, Viewer.Default, CancellationToken.None);

        recap.Should().Be(ProducerRecap.Template(_sheet, [_goal], Viewer.Default));
        _chat.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task A_model_recap_is_marked_as_the_models_and_a_template_is_not()
    {
        var recaps = new ConcurrentQueue<string>();
        using var listener = Listen(SquadMetrics.RecapCounterName, SquadMetrics.RecapOutcomeTag, recaps);
        _chat.Returns(Reply("North Bank FC edge it", "Tom Okafor scored."))
            .Throws(new HttpRequestException("Foundry is down."));
        var producer = Producer();

        var model = await producer.WriteAsync(_matchId, Viewer.Default, CancellationToken.None);
        var template = await producer.WriteAsync(_matchId, Viewer.Default, CancellationToken.None);

        model.FromModel.Should().BeTrue();
        template.FromModel.Should().BeFalse();
        model.Recap.Source.Should().Be(CommentarySource.Model);
        template.Recap.Source.Should().Be(CommentarySource.Template);
        recaps.Should().Equal("model", "fallback");
    }

    [Test]
    public async Task A_match_that_is_not_at_full_time_has_no_recap()
    {
        _facts.Setup(f => f.GetSummaryAsync(_matchId, It.IsAny<CancellationToken>())).ReturnsAsync(_sheet with { FullTime = false });

        var act = () => Producer().WriteAsync(_matchId, Viewer.Default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _chat.Requests.Should().BeEmpty();
    }

    [Test]
    public void The_template_does_not_repeat_a_clock_the_reason_already_starts_with()
    {
        var chance = new MomentFact("demo-1:300", "15:04", "BigChance", TeamSide.Home, "15:04: Big chance for North Bank FC, xG 0.41, Saved.");

        var recap = ProducerRecap.Template(_sheet, [chance, _goal], Viewer.Default);

        recap.Text.Should().Be(
            "Full time: North Bank FC 1, Riverside Athletic 0. The key moments: "
            + "15:04: Big chance for North Bank FC, xG 0.41, Saved. "
            + "23:00 Goal: North Bank FC lead 1-0.");
    }

    private MeterListener Listen(string instrumentName, string tag, ConcurrentQueue<string> values)
    {
        var factory = _provider.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var pair in tags)
            {
                if (pair.Key == tag)
                {
                    values.Enqueue(pair.Value?.ToString() ?? "");
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static Viewer SpanishAnalyst()
    {
        var (viewer, _) = Viewer.Create(ViewingMode.Analyst, "es", null, null, null, _ => true, _ => true);
        return viewer!;
    }

    private static ChatResponse Reply(string headline, params string[] paragraphs) =>
        FakeChatClient.Reply(
            System.Text.Json.JsonSerializer.Serialize(new { headline, paragraphs }),
            inputTokens: 100,
            outputTokens: 50);

    private ProducerRecap Producer() => new(
        _chat,
        _facts.Object,
        Options.Create(_options),
        new SquadMetrics(_provider.GetRequiredService<IMeterFactory>()),
        NullLogger<ProducerRecap>.Instance);
}

internal static class ProducerRecapTestExtensions
{
    public static async Task<MatchRecap> RecapAsync(this ProducerRecap producer, string matchId, Viewer viewer, CancellationToken cancellationToken) =>
        (await producer.WriteAsync(matchId, viewer, cancellationToken)).Recap;
}
