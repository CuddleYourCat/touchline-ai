using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Agents;

namespace Touchline.Tests.Unit.Agents;

[TestFixture]
public sealed class TokenBudgetChatClientTests
{
    private const string _model = "small-deployment";

    private ServiceProvider _provider = null!;
    private SquadMetrics _metrics = null!;
    private MeterListener _listener = null!;
    private List<(long Value, Dictionary<string, object?> Tags)> _measurements = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = _provider.GetRequiredService<IMeterFactory>();
        _metrics = new SquadMetrics(meterFactory);
        _measurements = [];

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SquadMetrics.MeterName
                    && instrument.Meter.Scope == meterFactory
                    && instrument.Name == SquadMetrics.TokenCounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            _measurements.Add((value, copy));
        });
        _listener.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    [TestCase(SquadAgent.Commentator, 150)]
    [TestCase(SquadAgent.Localiser, 200)]
    [TestCase(SquadAgent.Analyst, 600)]
    [TestCase(SquadAgent.Producer, 1200)]
    public async Task Sets_the_agents_output_cap(SquadAgent agent, int expectedCap)
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, size: SquadAgentChatOptions.ModelFor(agent));

        await client.GetResponseAsync("Describe the goal.", new ChatOptions().ForAgent(agent));

        inner.Requests.Should().ContainSingle()
            .Which.Options!.MaxOutputTokens.Should().Be(expectedCap);
    }

    [Test]
    public async Task Keeps_a_lower_cap_the_caller_asked_for()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, size: SquadModels.Large);

        await client.GetResponseAsync("Hi", new ChatOptions { MaxOutputTokens = 40 }.ForAgent(SquadAgent.Producer));

        inner.Requests[0].Options!.MaxOutputTokens.Should().Be(40);
    }

    [Test]
    public async Task Removes_the_agent_property_before_the_request_leaves()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner);
        var options = new ChatOptions().ForAgent(SquadAgent.Commentator);

        await client.GetResponseAsync("Hi", options);

        inner.Requests[0].Options!.AdditionalProperties.Should().BeNull();
        options.AdditionalProperties.Should().ContainKey(SquadAgentChatOptions.AgentPropertyKey);
    }

    [Test]
    public async Task Refuses_a_call_that_names_no_agent()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner);

        var call = () => client.GetResponseAsync("Hi", new ChatOptions());

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*name its agent*");
        inner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Counts_input_and_output_tokens_by_agent_and_model()
    {
        var inner = new FakeChatClient().Returns("What a strike!", inputTokens: 120, outputTokens: 40);
        var client = Budget(inner);

        await client.GetResponseAsync("Describe the goal.", new ChatOptions().ForAgent(SquadAgent.Commentator));

        _measurements.Should().HaveCount(2);
        _measurements.Should().Contain(m => m.Value == 120 && Tagged(m.Tags, SquadAgent.Commentator, "input"));
        _measurements.Should().Contain(m => m.Value == 40 && Tagged(m.Tags, SquadAgent.Commentator, "output"));
    }

    [Test]
    public async Task Streaming_applies_the_cap_and_counts_tokens()
    {
        var inner = new FakeChatClient().Returns("Recap text", inputTokens: 900, outputTokens: 700);
        var client = Budget(inner, size: SquadModels.Large);

        await foreach (var _ in client.GetStreamingResponseAsync("Recap", new ChatOptions().ForAgent(SquadAgent.Producer)))
        {
        }

        inner.Requests[0].Options!.MaxOutputTokens.Should().Be(1200);
        _measurements.Should().Contain(m => m.Value == 900 && Tagged(m.Tags, SquadAgent.Producer, "input"));
        _measurements.Should().Contain(m => m.Value == 700 && Tagged(m.Tags, SquadAgent.Producer, "output"));
    }

    [Test]
    public async Task Trims_the_oldest_turns_after_the_first_user_message_when_input_is_over_budget()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, maxInputTokens: 300, size: SquadModels.Large);
        var system = new ChatMessage(ChatRole.System, "You are the Analyst.");
        var latest = new ChatMessage(ChatRole.User, "Why did that goal happen?");
        var history = Enumerable.Range(0, 10)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, new string('x', 400)))
            .ToList();

        await client.GetResponseAsync([system, .. history, latest], new ChatOptions().ForAgent(SquadAgent.Analyst));

        var sent = inner.Requests[0].Messages;
        sent.Should().Equal([system, history[0], history[9], latest], ReferenceEquals);
        sent.Sum(m => 4 + (int)Math.Ceiling(m.Text.Length / 4.0)).Should().BeLessThanOrEqualTo(300);
    }

    [Test]
    public async Task Drops_the_oldest_tool_call_before_the_question_in_a_tool_loop()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, maxInputTokens: 256, size: SquadModels.Large);
        var system = new ChatMessage(ChatRole.System, "You are the Analyst.");
        var question = new ChatMessage(ChatRole.User, "Why did that goal happen?");
        var call1 = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_events")]);
        var result1 = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new string('r', 1200))]);
        var call2 = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "get_roster")]);
        var result2 = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c2", "Okafor, Mensah")]);

        await client.GetResponseAsync(
            [system, question, call1, result1, call2, result2],
            new ChatOptions().ForAgent(SquadAgent.Analyst));

        inner.Requests[0].Messages.Should().Equal([system, question, call2, result2], ReferenceEquals);
    }

    [Test]
    public async Task Counts_tool_definitions_towards_the_input_budget()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, maxInputTokens: 256, size: SquadModels.Large);
        var tool = AIFunctionFactory.Create(() => "roster", "get_roster", new string('d', 2000));
        var options = new ChatOptions { Tools = [tool] }.ForAgent(SquadAgent.Analyst);

        InputBudget.Estimate(options.Tools).Should().BeGreaterThan(500);

        var call = () => client.GetResponseAsync("Who scored?", options);

        await call.Should().ThrowAsync<SquadBudgetExceededException>();
        inner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Refuses_an_agent_on_the_wrong_model_size()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, size: SquadModels.Large);

        var call = () => client.GetResponseAsync("Describe the goal.", new ChatOptions().ForAgent(SquadAgent.Commentator));

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Commentator*small*large*");
        inner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Drops_a_tool_call_together_with_its_result()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, maxInputTokens: 256, size: SquadModels.Large);
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_roster")]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new string('r', 1200))]);
        var latest = new ChatMessage(ChatRole.User, "Who scored?");

        await client.GetResponseAsync([call, result, latest], new ChatOptions().ForAgent(SquadAgent.Analyst));

        inner.Requests[0].Messages.Should().ContainSingle().Which.Should().BeSameAs(latest);
    }

    [Test]
    public async Task Refuses_input_that_cannot_be_trimmed_under_budget()
    {
        var inner = new FakeChatClient();
        var client = Budget(inner, maxInputTokens: 256, size: SquadModels.Large);
        var huge = new ChatMessage(ChatRole.User, new string('q', 4000));

        var call = () => client.GetResponseAsync([huge], new ChatOptions().ForAgent(SquadAgent.Analyst));

        await call.Should().ThrowAsync<SquadBudgetExceededException>().WithMessage("*Analyst*256*");
        inner.Requests.Should().BeEmpty();
        _measurements.Should().BeEmpty();
    }

    [Test]
    public async Task A_model_outage_propagates_and_records_no_tokens()
    {
        var inner = new FakeChatClient().Throws(new HttpRequestException("Foundry is unavailable"));
        var client = Budget(inner);

        var call = () => client.GetResponseAsync("Hi", new ChatOptions().ForAgent(SquadAgent.Commentator));

        await call.Should().ThrowAsync<HttpRequestException>();
        _measurements.Should().BeEmpty();
    }

    private static bool Tagged(Dictionary<string, object?> tags, SquadAgent agent, string type) =>
        Equals(tags[SquadMetrics.AgentTag], agent.ToString())
        && Equals(tags[SquadMetrics.ModelTag], _model)
        && Equals(tags[SquadMetrics.TokenTypeTag], type);

    private TokenBudgetChatClient Budget(FakeChatClient inner, int maxInputTokens = 4000, string size = SquadModels.Small) =>
        new(inner, size, _model, new SquadOptions { MaxInputTokens = maxInputTokens }, _metrics);
}
