extern alias processor;
extern alias simulator;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using processor::Touchline.EventProcessor.Processing;
using simulator::Touchline.Simulator.Engine;
using Touchline.Agents;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.Tests.Unit.Agents;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>The full-time recaps (ADR-030, amended 23 Oct): written once by the processor, six per match.</summary>
[TestFixture]
public class RecapWriterTests
{
    private static readonly IReadOnlyDictionary<string, TeamProfile> _teams = TeamProfileCatalog.LoadEmbedded();

    private InMemoryMatchStore _matches = null!;
    private InMemoryRecapStore _recaps = null!;
    private FakeChatClient _chat = null!;
    private ServiceProvider _services = null!;
    private SquadOptions _options = null!;
    private string _matchId = null!;

    [SetUp]
    public async Task SetUp()
    {
        _matches = new InMemoryMatchStore();
        _recaps = new InMemoryRecapStore();
        _chat = new FakeChatClient { DefaultResponse = Reply() };
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _options = new SquadOptions { Languages = ["en", "es", "fr"] };

        var match = new MatchSimulator(_teams["north-bank-fc"], _teams["thames-valley-united"], 42).Play().ToList();
        _matchId = match[0].MatchId;
        var processor = new MatchEventProcessor(
            _matches, new NullMomentPublisher(), new KeyMomentDetector(), new PipelineTelemetry(_services.GetRequiredService<IMeterFactory>()),
            NullLogger<MatchEventProcessor>.Instance);
        await processor.ProcessAsync(
            [.. match.Select(e => new IncomingEvent(EventEnvelope.For(e), BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(e, DomainJson.Options))))],
            CancellationToken.None);
    }

    [TearDown]
    public void TearDown()
    {
        _chat.Dispose();
        _services.Dispose();
    }

    [Test]
    public async Task A_finished_match_gets_six_recaps_one_per_language_and_mode()
    {
        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 6, 0, 0, 0));
        _recaps.Stored.Keys.Should().BeEquivalentTo(
            from language in new[] { "en", "es", "fr" }
            from mode in new[] { ViewingMode.Casual, ViewingMode.Analyst }
            select (_matchId, mode, language));
        _chat.Requests.Should().HaveCount(6);
        _chat.Requests.Should().OnlyContain(r => r.Messages[0].Text == ProducerRecap.SystemPrompt);
    }

    [Test]
    public async Task Recaps_already_stored_are_skipped_without_a_model_call()
    {
        await _recaps.SaveAsync(new MatchRecap(_matchId, "Stored", "Stored.", ViewingMode.Casual, "en", CommentarySource.Model), CancellationToken.None);
        await _recaps.SaveAsync(new MatchRecap(_matchId, "Stored", "Stored.", ViewingMode.Analyst, "fr", CommentarySource.Model), CancellationToken.None);

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 4, 2, 0, 0));
        _chat.Requests.Should().HaveCount(4);
        _recaps.Stored[(_matchId, ViewingMode.Casual, "en")].Headline.Should().Be("Stored");
    }

    [Test]
    public async Task A_failed_model_call_stores_nothing_for_that_pair_and_the_rest_carry_on()
    {
        _chat.Throws(new HttpRequestException("Foundry is down."));

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 5, 0, 1, 0));
        _recaps.Stored.Should().HaveCount(5).And.NotContainKey((_matchId, ViewingMode.Casual, "en"));
    }

    [TestCase(429)]
    [TestCase(503)]
    public async Task A_transient_model_error_is_counted_for_redelivery_and_not_on_the_attempt_marker(int status)
    {
        _chat.Throws(ModelFailuresTests.Failure(status));

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 5, 0, 0, 0, TransientFailures: 1));
        _recaps.Stored.Should().HaveCount(5).And.NotContainKey((_matchId, ViewingMode.Casual, "en"));
        _recaps.Attempts.Should().BeEmpty("the model never answered, so the redelivery gets a fair try");
    }

    [Test]
    public async Task A_content_filter_block_is_a_permanent_fallback_on_the_attempt_marker()
    {
        _chat.Throws(ModelFailuresTests.Failure(400, """{"error":{"code":"content_filter"}}"""));

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 5, 0, 1, 0));
        _recaps.Attempts.Should().ContainKey((_matchId, ViewingMode.Casual, "en"));
    }

    [Test]
    public async Task A_failed_save_is_counted_and_the_rest_carry_on()
    {
        _recaps.FailFor = (ViewingMode.Analyst, "es");

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 5, 0, 0, 1));
        _recaps.Stored.Should().HaveCount(5);
        _recaps.Attempts.Should().BeEmpty("a store failure isn't a model fallback");
    }

    [Test]
    public async Task A_model_fallback_is_counted_on_the_pairs_attempt_marker()
    {
        _chat.Throws(new HttpRequestException("Foundry is down."));

        await Writer().WriteAsync(_matchId, CancellationToken.None);

        _recaps.Attempts.Should().Equal(new Dictionary<(string, ViewingMode, string), int> { [(_matchId, ViewingMode.Casual, "en")] = 1 });
    }

    [Test]
    public async Task A_pair_that_fell_back_twice_is_not_tried_again()
    {
        _recaps.Attempts[(_matchId, ViewingMode.Casual, "en")] = RecapWriter.MaxModelAttempts;

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 5, 0, 0, 0, GivenUp: 1));
        _chat.Requests.Should().HaveCount(5);
        _recaps.Stored.Should().NotContainKey((_matchId, ViewingMode.Casual, "en"));
    }

    [Test]
    public async Task A_pair_that_fell_back_once_is_tried_again()
    {
        _recaps.Attempts[(_matchId, ViewingMode.Casual, "en")] = 1;

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Should().Be(new RecapRun(RecapRunStatus.Done, 6, 0, 0, 0));
    }

    [Test]
    public async Task Without_models_nothing_is_read_written_or_called()
    {
        _options.ModelsAvailable = false;

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Status.Should().Be(RecapRunStatus.NoModels);
        _recaps.Stored.Should().BeEmpty();
        _chat.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task A_match_not_at_full_time_is_not_recapped()
    {
        var record = _matches.Matches[_matchId];
        _matches.Matches[_matchId] = record with { State = record.State with { FullTime = false } };

        var run = await Writer().WriteAsync(_matchId, CancellationToken.None);

        run.Status.Should().Be(RecapRunStatus.NotFinished);
        _chat.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task The_facts_come_from_the_stored_match_with_its_rosters_and_key_moments()
    {
        var record = _matches.Matches[_matchId];
        var facts = StoredMatchFacts.From(record, await _matches.GetEventsAsync(_matchId, CancellationToken.None), new KeyMomentDetector());

        var sheet = (await facts.GetSummaryAsync(_matchId, CancellationToken.None))!;
        var moments = await facts.GetKeyMomentsAsync(_matchId, CancellationToken.None);
        var events = await facts.GetEventsAsync(_matchId, 0, ProducerRecap.LastMinute, CancellationToken.None);

        sheet.FullTime.Should().BeTrue();
        sheet.HomeGoals.Should().Be(record.State.Home.Goals);
        sheet.HomePlayers.Should().HaveCount(11);
        sheet.SquadNames.Should().Contain(_teams["north-bank-fc"].Squad.Select(p => p.Name));
        moments.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(MatchFactLimits.MaxMoments);
        events.Should().HaveCountLessThanOrEqualTo(MatchFactLimits.MaxEvents);
    }

    private RecapWriter Writer() => new(
        _matches,
        _recaps,
        new KeyMomentDetector(),
        _chat,
        Options.Create(_options),
        new SquadMetrics(_services.GetRequiredService<IMeterFactory>()),
        NullLoggerFactory.Instance);

    // No names at all, so the roster guardrail always passes.
    private static ChatResponse Reply() =>
        FakeChatClient.Reply(
            JsonSerializer.Serialize(new { headline = "Full time", paragraphs = new[] { "A close contest, settled late." } }),
            inputTokens: 100,
            outputTokens: 20);

    private sealed class NullMomentPublisher : IKeyMomentPublisher
    {
        public Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryRecapStore : IRecapStore
    {
        public Dictionary<(string MatchId, ViewingMode Mode, string Language), MatchRecap> Stored { get; } = [];

        public Dictionary<(string MatchId, ViewingMode Mode, string Language), int> Attempts { get; } = [];

        public (ViewingMode Mode, string Language)? FailFor { get; set; }

        public Task<MatchRecap?> GetAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.GetValueOrDefault((matchId, mode, language)));

        public Task<bool> SaveAsync(MatchRecap recap, CancellationToken cancellationToken)
        {
            if (FailFor == (recap.Mode, recap.Language))
            {
                throw new HttpRequestException("Cosmos DB is unavailable.");
            }

            return Task.FromResult(Stored.TryAdd((recap.MatchId, recap.Mode, recap.Language), recap));
        }

        public Task<int> GetAttemptsAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken) =>
            Task.FromResult(Attempts.GetValueOrDefault((matchId, mode, language)));

        public Task SaveAttemptsAsync(string matchId, ViewingMode mode, string language, int attempts, CancellationToken cancellationToken)
        {
            Attempts[(matchId, mode, language)] = attempts;
            return Task.CompletedTask;
        }
    }
}
