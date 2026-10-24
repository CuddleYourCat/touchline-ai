extern alias processor;
extern alias simulator;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using processor::Touchline.EventProcessor;
using processor::Touchline.EventProcessor.Processing;
using simulator::Touchline.Simulator.Engine;
using Touchline.Agents;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.Search;
using Touchline.Tests.Unit.Agents;
using Touchline.Tests.Unit.Search;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>The possession indexer's writer and its Service Bus adapter (ADR-039, ADR-056).</summary>
[TestFixture]
public class PossessionIndexTests
{
    private ServiceProvider _services = null!;
    private FakeChatClient _chat = null!;
    private Mock<ServiceBusMessageActions> _actions = null!;
    private Mock<IEmbeddingGenerator<string, Embedding<float>>> _embeddings = null!;
    private Mock<IPossessionIndexer> _indexer = null!;
    private List<List<string>> _embeddingCalls = null!;
    private List<IReadOnlyList<PossessionDocument>> _upserts = null!;
    private int _embeddingFailures;
    private Exception? _upsertFailure;
    private InMemoryMatchStore _matches = null!;
    private string _matchId = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _chat = new FakeChatClient();
        _actions = new Mock<ServiceBusMessageActions>();
        _embeddingCalls = [];
        _upserts = [];
        _embeddingFailures = 0;
        _upsertFailure = null;

        _embeddings = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        _embeddings
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
            {
                var texts = values.ToList();
                _embeddingCalls.Add(texts);
                if (_embeddingFailures > 0)
                {
                    _embeddingFailures--;
                    throw new HttpRequestException("embedding service unavailable");
                }

                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                    texts.Select(t => new Embedding<float>(Vector(t)))));
            });

        _indexer = new Mock<IPossessionIndexer>();
        _indexer.SetupGet(i => i.Enabled).Returns(true);
        _indexer
            .Setup(i => i.UpsertAsync(It.IsAny<IReadOnlyList<PossessionDocument>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<PossessionDocument> documents, CancellationToken _) =>
            {
                if (_upsertFailure is not null)
                {
                    throw _upsertFailure;
                }

                _upserts.Add(documents);
                return Task.CompletedTask;
            });

        (_matches, _matchId) = await FinishedMatchAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _chat.Dispose();
        _services.Dispose();
    }

    [Test]
    public async Task One_embedding_call_covers_the_match_and_every_document_is_upserted_with_its_vector()
    {
        var run = await Writer().WriteAsync(_matchId, 1, CancellationToken.None);

        run.Status.Should().Be(PossessionIndexStatus.Done);
        _embeddingCalls.Should().ContainSingle();
        var documents = _upserts.Should().ContainSingle().Subject;
        run.Indexed.Should().Be(documents.Count);
        _embeddingCalls[0].Should().Equal(documents.Select(d => d.Summary));
        documents.Should().OnlyContain(d => d.SummaryVector != null && d.SummaryVector.Length == PossessionIndexWriter.VectorDimensions);
        documents.Select(d => d.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith(_matchId + "_", StringComparison.Ordinal));
        documents.Select(d => d.SummaryVector![0]).Should().Equal(documents.Select(d => (float)d.Summary.Length), "each vector is the one embedded from its own summary");
    }

    [Test]
    public async Task A_rerun_produces_the_same_ids_so_the_index_is_rewritten_not_grown()
    {
        var writer = Writer();

        await writer.WriteAsync(_matchId, 1, CancellationToken.None);
        await writer.WriteAsync(_matchId, 1, CancellationToken.None);

        _upserts.Should().HaveCount(2);
        _upserts[1].Select(d => d.Id).Should().Equal(_upserts[0].Select(d => d.Id));
    }

    [Test]
    public async Task The_embedding_text_is_capped_by_the_configured_limit()
    {
        var writer = Writer(new PossessionSearchOptions { MaxEmbeddingInputChars = 50, MaxEmbeddingDocumentsPerMatch = 5 });

        await writer.WriteAsync(_matchId, 1, CancellationToken.None);

        _embeddingCalls.Should().ContainSingle().Which.Should().HaveCount(5).And.OnlyContain(t => t.Length <= 50);
        _upserts.Single().Should().HaveCount(5);
    }

    [Test]
    public async Task A_match_not_at_full_time_is_reported_and_nothing_is_embedded()
    {
        var run = await Writer().WriteAsync("m-404", 1, CancellationToken.None);

        run.Status.Should().Be(PossessionIndexStatus.NotFinished);
        _embeddingCalls.Should().BeEmpty();
        _upserts.Should().BeEmpty();
    }

    [Test]
    public async Task With_a_null_indexer_nothing_is_read_or_embedded()
    {
        var matches = new InMemoryMatchStore { OnCall = call => throw new InvalidOperationException($"unexpected read: {call}") };
        var writer = new PossessionIndexWriter(
            matches, new KeyMomentDetector(), Summaries(), new NullPossessionIndexer(), new EmbeddedTeamProfiles(), Metrics(),
            Options.Create(new PossessionSearchOptions()), NullLogger<PossessionIndexWriter>.Instance, _embeddings.Object);

        var run = await writer.WriteAsync(_matchId, 1, CancellationToken.None);

        run.Status.Should().Be(PossessionIndexStatus.Disabled);
        _embeddingCalls.Should().BeEmpty();
    }

    [Test]
    public async Task A_model_written_key_moment_summary_is_embedded_in_place_of_the_template()
    {
        const string Summary = "Theo Lindqvist and Elias Brook moved the ball for North Bank FC, and the move ended in a goal.";
        _chat.DefaultResponse = FakeChatClient.Reply(Summary, 100, 20);

        await Writer(squad: new SquadOptions()).WriteAsync(_matchId, 1, CancellationToken.None);

        _upserts.Single().Count(d => d.Summary == Summary).Should().BeGreaterThan(0);
        _embeddingCalls.Single().Should().Contain(Summary);
    }

    [Test]
    public async Task The_function_completes_a_finished_match()
    {
        await Function().RunAsync(Message(_matchId, 1), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task The_function_abandons_a_match_not_yet_finished(int deliveryCount)
    {
        await Function().RunAsync(Message("m-404", deliveryCount), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.AbandonMessageAsync));
    }

    [Test]
    public async Task The_function_dead_letters_a_match_still_not_finished_on_the_last_delivery()
    {
        await Function().RunAsync(Message("m-404", PossessionIndexFunction.MaxNotFinishedDeliveries), _actions.Object, CancellationToken.None);

        var settlement = _actions.Invocations.Should().ContainSingle().Subject;
        settlement.Method.Name.Should().Be(nameof(ServiceBusMessageActions.DeadLetterMessageAsync));
        settlement.Arguments.Should().Contain(PossessionIndexFunction.NotFinishedReason);
    }

    [Test]
    public async Task The_function_dead_letters_an_unreadable_message()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("not json"), messageId: "bad");

        await Function().RunAsync(message, _actions.Object, CancellationToken.None);

        var settlement = _actions.Invocations.Should().ContainSingle().Subject;
        settlement.Method.Name.Should().Be(nameof(ServiceBusMessageActions.DeadLetterMessageAsync));
        settlement.Arguments.Should().Contain(PossessionIndexFunction.UnreadableReason);
    }

    [Test]
    public async Task The_function_completes_without_reading_anything_when_Search_is_off()
    {
        var matches = new InMemoryMatchStore { OnCall = call => throw new InvalidOperationException($"unexpected read: {call}") };
        var writer = new PossessionIndexWriter(
            matches, new KeyMomentDetector(), Summaries(), new NullPossessionIndexer(), new EmbeddedTeamProfiles(), Metrics(),
            Options.Create(new PossessionSearchOptions()), NullLogger<PossessionIndexWriter>.Instance);

        await new PossessionIndexFunction(writer, NullLogger<PossessionIndexFunction>.Instance)
            .RunAsync(Message(_matchId, 1), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
    }

    [Test]
    public async Task Chaos_an_embedding_outage_abandons_the_message_with_no_upsert_and_the_redelivery_indexes_the_same_ids()
    {
        _embeddingFailures = 1;
        var function = Function();

        await function.RunAsync(Message(_matchId, 1), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.AbandonMessageAsync));
        _upserts.Should().BeEmpty("documents are never indexed without vectors");

        _actions.Invocations.Clear();
        await function.RunAsync(Message(_matchId, 2), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
        _upserts.Should().ContainSingle().Which.Select(d => d.Id).Should().Equal(
            PossessionSummarizer.Summarize(_matchId, await _matches.GetEventsAsync(_matchId, CancellationToken.None), new KeyMomentDetector(), PossessionRoster.From((await _matches.GetMatchAsync(_matchId, CancellationToken.None))!.Metadata)).Select(p => p.Document.Id));
    }

    [Test]
    public async Task Chaos_the_redelivery_after_an_embedding_failure_makes_no_chat_calls_and_indexes_the_same_ids()
    {
        _chat.DefaultResponse = FakeChatClient.Reply("Theo Lindqvist moved the ball for North Bank FC, and the move ended in a goal.", 100, 20);
        _embeddingFailures = 1;
        var function = new PossessionIndexFunction(Writer(squad: new SquadOptions()), NullLogger<PossessionIndexFunction>.Instance);

        await function.RunAsync(Message(_matchId, 1), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.AbandonMessageAsync));
        var callsOnFirstDelivery = _chat.Requests.Count;
        callsOnFirstDelivery.Should().BeGreaterThan(0);
        var firstTexts = _embeddingCalls[0];

        _actions.Invocations.Clear();
        await function.RunAsync(Message(_matchId, 2), _actions.Object, CancellationToken.None);

        Settlements().Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
        _chat.Requests.Should().HaveCount(callsOnFirstDelivery, "a redelivery uses the templates and pays for no model calls");
        var documents = _upserts.Should().ContainSingle().Subject;
        documents.Should().HaveCount(firstTexts.Count, "the same possessions are indexed");
        documents.Select(d => d.Id).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Players_are_named_from_the_injected_team_profiles()
    {
        var profiles = new Mock<ITeamProfiles>();
        profiles.SetupGet(p => p.All).Returns(new Dictionary<string, TeamProfile>());
        var writer = new PossessionIndexWriter(
            _matches, new KeyMomentDetector(), Summaries(), _indexer.Object, profiles.Object, Metrics(),
            Options.Create(new PossessionSearchOptions()), NullLogger<PossessionIndexWriter>.Instance, _embeddings.Object);

        await writer.WriteAsync(_matchId, 1, CancellationToken.None);

        profiles.VerifyGet(p => p.All, Times.AtLeastOnce);
        _upserts.Single().Should().OnlyContain(d => d.Players.Count == 0, "no profile means no known players");
    }

    [Test]
    public async Task Chaos_an_index_that_rejects_part_of_the_batch_leaves_the_message_unsettled_so_it_is_redelivered()
    {
        _upsertFailure = new PossessionIndexingException("3 of 215 possession documents were rejected");

        var act = () => Function().RunAsync(Message(_matchId, 1), _actions.Object, CancellationToken.None);

        await act.Should().ThrowAsync<PossessionIndexingException>();
        _actions.Invocations.Should().BeEmpty("a message that throws is never completed");
    }

    [Test]
    public async Task A_wrong_number_of_vectors_is_treated_as_an_embedding_failure()
    {
        _embeddings
            .Setup(e => e.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[PossessionIndexWriter.VectorDimensions])]));

        var run = await Writer().WriteAsync(_matchId, 1, CancellationToken.None);

        run.Status.Should().Be(PossessionIndexStatus.EmbeddingFailed);
        _upserts.Should().BeEmpty();
    }

    private static float[] Vector(string text)
    {
        var vector = new float[PossessionIndexWriter.VectorDimensions];
        vector[0] = text.Length;
        return vector;
    }

    private List<string> Settlements() => [.. _actions.Invocations.Select(i => i.Method.Name)];

    private SearchMetrics Metrics() => new(_services.GetRequiredService<IMeterFactory>());

    private KeyMomentSummaryWriter Summaries(SquadOptions? squad = null) => new(
        _chat,
        Options.Create(squad ?? new SquadOptions { ModelsAvailable = false }),
        Options.Create(new PossessionSearchOptions()),
        Metrics(),
        NullLogger<KeyMomentSummaryWriter>.Instance);

    private PossessionIndexWriter Writer(PossessionSearchOptions? options = null, SquadOptions? squad = null) => new(
        _matches,
        new KeyMomentDetector(),
        Summaries(squad),
        _indexer.Object,
        new EmbeddedTeamProfiles(),
        Metrics(),
        Options.Create(options ?? new PossessionSearchOptions()),
        NullLogger<PossessionIndexWriter>.Instance,
        _embeddings.Object);

    private PossessionIndexFunction Function() => new(Writer(), NullLogger<PossessionIndexFunction>.Instance);

    private async Task<(InMemoryMatchStore Matches, string MatchId)> FinishedMatchAsync()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        var match = new MatchSimulator(teams["north-bank-fc"], teams["thames-valley-united"], 42).Play().ToList();
        var matches = new InMemoryMatchStore();
        var processor = new MatchEventProcessor(
            matches, new NullMomentPublisher(), new KeyMomentDetector(), new PipelineTelemetry(_services.GetRequiredService<IMeterFactory>()),
            NullLogger<MatchEventProcessor>.Instance);
        await processor.ProcessAsync(
            [.. match.Select(e => new IncomingEvent(EventEnvelope.For(e), BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(e, DomainJson.Options))))],
            CancellationToken.None);
        return (matches, match[0].MatchId);
    }

    private static ServiceBusReceivedMessage Message(string matchId, int deliveryCount) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new MatchFinished(matchId), DomainJson.Options)),
            messageId: $"{matchId}:finished",
            deliveryCount: deliveryCount);

    private sealed class NullMomentPublisher : IKeyMomentPublisher
    {
        public Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
