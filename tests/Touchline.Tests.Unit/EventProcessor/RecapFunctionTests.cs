extern alias processor;
extern alias simulator;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
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
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.Tests.Unit.Agents;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>How the recap host settles a "match finished" for a match it can't find at full time.</summary>
[TestFixture]
public class RecapFunctionTests
{
    private FakeChatClient _chat = null!;
    private ServiceProvider _services = null!;
    private Mock<ServiceBusMessageActions> _actions = null!;

    [SetUp]
    public void SetUp()
    {
        _chat = new FakeChatClient();
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _actions = new Mock<ServiceBusMessageActions>();
    }

    [TearDown]
    public void TearDown()
    {
        _chat.Dispose();
        _services.Dispose();
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task A_match_not_yet_finished_is_abandoned_so_it_is_delivered_again(int deliveryCount)
    {
        await Function().RunAsync(Message("m-404", deliveryCount), _actions.Object, CancellationToken.None);

        _actions.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(ServiceBusMessageActions.AbandonMessageAsync));
    }

    [Test]
    public async Task A_match_still_not_finished_on_the_third_delivery_is_dead_lettered()
    {
        await Function().RunAsync(Message("m-404", RecapFunction.MaxNotFinishedDeliveries), _actions.Object, CancellationToken.None);

        var settlement = _actions.Invocations.Should().ContainSingle().Subject;
        settlement.Method.Name.Should().Be(nameof(ServiceBusMessageActions.DeadLetterMessageAsync));
        settlement.Arguments.Should().Contain(RecapFunction.NotFinishedReason);
    }

    [Test]
    public async Task Chaos_a_model_503_abandons_the_message_and_the_redelivery_stores_each_recap_once()
    {
        var (matches, matchId) = await FinishedMatchAsync();
        var recaps = new CountingRecapStore();
        // No names at all, so the roster guardrail always passes.
        _chat.DefaultResponse = FakeChatClient.Reply(
            JsonSerializer.Serialize(new { headline = "Full time", paragraphs = new[] { "A close contest, settled late." } }), 100, 20);
        _chat.Throws(ModelFailuresTests.Failure(503));
        var function = Function(matches, recaps, ["en"]);

        await function.RunAsync(Message(matchId, 1), _actions.Object, CancellationToken.None);

        _actions.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(ServiceBusMessageActions.AbandonMessageAsync));
        recaps.Saves.Should().ContainSingle("the other mode was written before the redelivery");
        recaps.Attempts.Should().BeEmpty();

        _actions.Invocations.Clear();
        await function.RunAsync(Message(matchId, 2), _actions.Object, CancellationToken.None);

        _actions.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
        recaps.Saves.Should().HaveCount(2).And.OnlyHaveUniqueItems("nothing is stored twice");
    }

    [Test]
    public async Task A_content_filter_block_completes_the_message_without_a_redelivery()
    {
        var (matches, matchId) = await FinishedMatchAsync();
        _chat.Throws(ModelFailuresTests.Failure(400, """{"error":{"code":"content_filter"}}"""));

        await Function(matches, new CountingRecapStore(), ["en"]).RunAsync(Message(matchId, 1), _actions.Object, CancellationToken.None);

        _actions.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(ServiceBusMessageActions.CompleteMessageAsync));
    }

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

    private RecapFunction Function(InMemoryMatchStore matches, IRecapStore recaps, IList<string> languages) => new(
        new RecapWriter(
            matches,
            recaps,
            new KeyMomentDetector(),
            _chat,
            Options.Create(new SquadOptions { Languages = languages }),
            new SquadMetrics(_services.GetRequiredService<IMeterFactory>()),
            NullLoggerFactory.Instance),
        NullLogger<RecapFunction>.Instance);

    private sealed class NullMomentPublisher : IKeyMomentPublisher
    {
        public Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Records every save, so a recap stored twice would show.</summary>
    private sealed class CountingRecapStore : IRecapStore
    {
        private readonly Dictionary<(string, ViewingMode, string), MatchRecap> _stored = [];

        public List<(string MatchId, ViewingMode Mode, string Language)> Saves { get; } = [];

        public Dictionary<(string, ViewingMode, string), int> Attempts { get; } = [];

        public Task<MatchRecap?> GetAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken) =>
            Task.FromResult(_stored.GetValueOrDefault((matchId, mode, language)));

        public Task<bool> SaveAsync(MatchRecap recap, CancellationToken cancellationToken)
        {
            Saves.Add((recap.MatchId, recap.Mode, recap.Language));
            return Task.FromResult(_stored.TryAdd((recap.MatchId, recap.Mode, recap.Language), recap));
        }

        public Task<int> GetAttemptsAsync(string matchId, ViewingMode mode, string language, CancellationToken cancellationToken) =>
            Task.FromResult(Attempts.GetValueOrDefault((matchId, mode, language)));

        public Task SaveAttemptsAsync(string matchId, ViewingMode mode, string language, int attempts, CancellationToken cancellationToken)
        {
            Attempts[(matchId, mode, language)] = attempts;
            return Task.CompletedTask;
        }
    }

    private RecapFunction Function() => new(
        new RecapWriter(
            new InMemoryMatchStore(),
            Mock.Of<IRecapStore>(),
            new KeyMomentDetector(),
            _chat,
            Options.Create(new SquadOptions { Languages = ["en"] }),
            new SquadMetrics(_services.GetRequiredService<IMeterFactory>()),
            NullLoggerFactory.Instance),
        NullLogger<RecapFunction>.Instance);

    private static ServiceBusReceivedMessage Message(string matchId, int deliveryCount) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new MatchFinished(matchId), DomainJson.Options)),
            messageId: $"{matchId}:finished",
            deliveryCount: deliveryCount);
}
