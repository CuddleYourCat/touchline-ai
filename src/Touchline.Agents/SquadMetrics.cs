using System.Diagnostics.Metrics;

namespace Touchline.Agents;

/// <summary>
/// The squad's OpenTelemetry instruments (ADR-007, ADR-043). Hosts export them by adding the
/// <see cref="MeterName"/> meter, which the service defaults do.
/// </summary>
public sealed class SquadMetrics
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "Touchline.Agents";

    /// <summary>The token counter's name.</summary>
    public const string TokenCounterName = "touchline.agents.tokens";

    /// <summary>The tag naming the agent.</summary>
    public const string AgentTag = "touchline.agent";

    /// <summary>The tag naming the model deployment, following the OpenTelemetry GenAI conventions.</summary>
    public const string ModelTag = "gen_ai.request.model";

    /// <summary>The tag saying whether tokens were <c>input</c> or <c>output</c>, following the GenAI conventions.</summary>
    public const string TokenTypeTag = "gen_ai.token.type";

    private readonly Counter<long> _tokens;

    /// <summary>Creates the squad's meter from the host's meter factory.</summary>
    /// <param name="meterFactory">The host's meter factory, which owns the meter's lifetime.</param>
    public SquadMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _tokens = meter.CreateCounter<long>(
            TokenCounterName,
            unit: "{token}",
            description: "Tokens sent to and received from the squad's models, by agent and model.");
    }

    /// <summary>Records one call's token usage.</summary>
    /// <param name="agent">The agent that made the call.</param>
    /// <param name="model">The model deployment it called.</param>
    /// <param name="inputTokens">Tokens sent, as the provider reported them.</param>
    /// <param name="outputTokens">Tokens received, as the provider reported them.</param>
    public void RecordTokens(SquadAgent agent, string model, long inputTokens, long outputTokens)
    {
        Record(agent, model, "input", inputTokens);
        Record(agent, model, "output", outputTokens);
    }

    private void Record(SquadAgent agent, string model, string type, long tokens)
    {
        if (tokens <= 0)
        {
            return;
        }

        _tokens.Add(
            tokens,
            new KeyValuePair<string, object?>(AgentTag, agent.ToString()),
            new KeyValuePair<string, object?>(ModelTag, model),
            new KeyValuePair<string, object?>(TokenTypeTag, type));
    }
}
