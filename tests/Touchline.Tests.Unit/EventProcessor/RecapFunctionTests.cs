extern alias processor;
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
using Touchline.Agents;
using Touchline.Domain;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
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
