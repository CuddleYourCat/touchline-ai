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

    /// <summary>The fallback counter's name: a model line replaced by a template, or a language skipped.</summary>
    public const string FallbackCounterName = "touchline.agents.fallbacks";

    /// <summary>The tag saying why a fallback happened, such as <c>failed</c> or <c>guardrail</c>.</summary>
    public const string ReasonTag = "touchline.fallback.reason";

    /// <summary>The tag naming the agent.</summary>
    public const string AgentTag = "touchline.agent";

    /// <summary>The tag naming the model deployment, following the OpenTelemetry GenAI conventions.</summary>
    public const string ModelTag = "gen_ai.request.model";

    /// <summary>The tag saying whether tokens were <c>input</c> or <c>output</c>, following the GenAI conventions.</summary>
    public const string TokenTypeTag = "gen_ai.token.type";

    /// <summary>The answer counter's name: every fan question answered, by agent and outcome.</summary>
    public const string AnswerCounterName = "touchline.agents.answers";

    /// <summary>The tag saying how an answer was produced: <c>model</c> or <c>fallback</c>.</summary>
    public const string OutcomeTag = "touchline.answer.outcome";

    /// <summary>The recap counter's name: every full-time recap the Producer wrote, by outcome.</summary>
    public const string RecapCounterName = "touchline.agents.recaps";

    /// <summary>The tag saying how a recap was written: <c>model</c>, <c>fallback</c>, or <c>skipped</c> when one was already stored.</summary>
    public const string RecapOutcomeTag = "touchline.recap.outcome";

    private readonly Counter<long> _tokens;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _answers;
    private readonly Counter<long> _recaps;

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
        _fallbacks = meter.CreateCounter<long>(
            FallbackCounterName,
            unit: "{fallback}",
            description: "Times an agent's output was replaced by a template or skipped, by agent and reason.");
        _answers = meter.CreateCounter<long>(
            AnswerCounterName,
            unit: "{answer}",
            description: "Fan questions answered, by agent and whether the model's answer or a safe fallback was sent.");
        _recaps = meter.CreateCounter<long>(
            RecapCounterName,
            unit: "{recap}",
            description: "Full-time recaps, by whether the model wrote it, the template stood in, or one was already stored.");
    }

    /// <summary>Records one recap the Producer wrote, or skipped.</summary>
    /// <param name="outcome"><c>model</c>, <c>fallback</c> or <c>skipped</c>.</param>
    public void RecordRecap(string outcome) =>
        _recaps.Add(
            1,
            new KeyValuePair<string, object?>(AgentTag, nameof(SquadAgent.Producer)),
            new KeyValuePair<string, object?>(RecapOutcomeTag, outcome));

    /// <summary>Records one answer to a fan's question.</summary>
    /// <param name="agent">The agent that answered.</param>
    /// <param name="outcome"><c>model</c> or <c>fallback</c>.</param>
    public void RecordAnswer(SquadAgent agent, string outcome) =>
        _answers.Add(
            1,
            new KeyValuePair<string, object?>(AgentTag, agent.ToString()),
            new KeyValuePair<string, object?>(OutcomeTag, outcome));

    /// <summary>Records one fallback: a template line used, or a language skipped.</summary>
    /// <param name="agent">The agent whose output was replaced or skipped.</param>
    /// <param name="reason">Why, such as <c>failed</c>, <c>guardrail</c> or <c>store-failed</c>.</param>
    public void RecordFallback(SquadAgent agent, string reason) =>
        _fallbacks.Add(
            1,
            new KeyValuePair<string, object?>(AgentTag, agent.ToString()),
            new KeyValuePair<string, object?>(ReasonTag, reason));

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
