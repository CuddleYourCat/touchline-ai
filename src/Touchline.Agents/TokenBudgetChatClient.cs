using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Touchline.Agents;

/// <summary>
/// The squad's token budget, enforced in one place (ADR-043). A decorator over the model
/// client (the Decorator pattern, through <see cref="DelegatingChatClient"/>), so no agent can
/// forget a cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which agent?</b> Every call must name its agent in
/// <see cref="ChatOptions.AdditionalProperties"/> under
/// <see cref="SquadAgentChatOptions.AgentPropertyKey"/> (use
/// <see cref="SquadAgentChatOptions.ForAgent"/>). One client per model then serves several
/// agents, and the Agent Framework carries the property through from an agent's default
/// options. A call that names no agent is refused: an uncapped call is the cost risk this
/// class exists to stop. The property is removed before the request leaves the process.
/// </para>
/// <para>
/// <b>Output:</b> <see cref="ChatOptions.MaxOutputTokens"/> is set to the agent's cap, or kept
/// if the caller asked for less.
/// </para>
/// <para>
/// <b>Input: trim, then refuse.</b> Input is estimated at four characters a token (no
/// tokenizer, so it's approximate and errs on the generous side for English). Over
/// <see cref="SquadOptions.MaxInputTokens"/> (tool definitions included), conversation turns are dropped, keeping
/// every system message, the first user message (the question) and the last message. The oldest
/// tool call goes first, together with its results, then the oldest other turn. If that still
/// doesn't fit, the call is refused with
/// <see cref="SquadBudgetExceededException"/> and never reaches the model.
/// </para>
/// <para>
/// <b>Telemetry:</b> the provider's reported input and output tokens are added to the
/// <see cref="SquadMetrics.TokenCounterName"/> counter, tagged by agent and model.
/// </para>
/// </remarks>
public sealed class TokenBudgetChatClient : DelegatingChatClient
{
    private readonly string _size;
    private readonly string _model;
    private readonly SquadOptions _options;
    private readonly SquadMetrics _metrics;

    /// <summary>Wraps a model client in the squad's token budget.</summary>
    /// <param name="innerClient">The model client.</param>
    /// <param name="size">The model size it calls, <see cref="SquadModels.Small"/> or <see cref="SquadModels.Large"/>. A call from an agent that runs on the other size is refused.</param>
    /// <param name="model">The model deployment it calls, used to tag telemetry.</param>
    /// <param name="options">The squad's caps.</param>
    /// <param name="metrics">Where token usage is recorded.</param>
    public TokenBudgetChatClient(IChatClient innerClient, string size, string model, SquadOptions options, SquadMetrics metrics)
        : base(innerClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(size);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        _size = size is SquadModels.Small or SquadModels.Large
            ? size
            : throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown model size.");
        _model = model;
        _options = options;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (agent, budgeted, trimmed) = Prepare(messages, options);
        var response = await base.GetResponseAsync(trimmed, budgeted, cancellationToken).ConfigureAwait(false);
        Record(agent, response.Usage);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (agent, budgeted, trimmed) = Prepare(messages, options);
        await foreach (var update in base.GetStreamingResponseAsync(trimmed, budgeted, cancellationToken).ConfigureAwait(false))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                Record(agent, usage.Details);
            }

            yield return update;
        }
    }

    private (SquadAgent Agent, ChatOptions Options, List<ChatMessage> Messages) Prepare(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var agent = SquadAgentChatOptions.ReadAgent(options)
            ?? throw new InvalidOperationException(
                $"A squad model call must name its agent in ChatOptions.AdditionalProperties[\"{SquadAgentChatOptions.AgentPropertyKey}\"]. Use ChatOptions.ForAgent(...).");

        var agentSize = SquadAgentChatOptions.ModelFor(agent);
        if (agentSize != _size)
        {
            throw new InvalidOperationException(
                $"The {agent} runs on the {agentSize} model (ADR-007), but this is the {_size} model's client.");
        }

        var budgeted = options!.Clone();
        int cap = _options.MaxOutputTokens.For(agent);
        budgeted.MaxOutputTokens = budgeted.MaxOutputTokens is int requested ? Math.Min(requested, cap) : cap;
        budgeted.AdditionalProperties = WithoutAgent(budgeted.AdditionalProperties);

        var trimmed = InputBudget.Trim([.. messages], _options.MaxInputTokens, agent, InputBudget.Estimate(budgeted.Tools));
        return (agent, budgeted, trimmed);
    }

    private static AdditionalPropertiesDictionary? WithoutAgent(AdditionalPropertiesDictionary? properties)
    {
        if (properties is null)
        {
            return null;
        }

        var copy = new AdditionalPropertiesDictionary(properties);
        copy.Remove(SquadAgentChatOptions.AgentPropertyKey);
        return copy.Count == 0 ? null : copy;
    }

    private void Record(SquadAgent agent, UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        _metrics.RecordTokens(agent, _model, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);
    }
}

/// <summary>
/// Thrown when a call's input can't be trimmed under <see cref="SquadOptions.MaxInputTokens"/>:
/// the tool definitions, system prompt, question and latest message alone are over budget. The
/// model is never called.
/// </summary>
public sealed class SquadBudgetExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SquadBudgetExceededException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SquadBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SquadBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Estimates and trims a call's input (see <see cref="TokenBudgetChatClient"/>).</summary>
internal static class InputBudget
{
    /// <summary>Roughly four characters to a token for English text.</summary>
    internal const double CharactersPerToken = 4.0;

    /// <summary>The per-message overhead of the chat format (role and separators).</summary>
    internal const int MessageOverheadTokens = 4;

    internal static int Estimate(ChatMessage message)
    {
        long characters = 0;
        foreach (var content in message.Contents)
        {
            characters += content switch
            {
                TextContent text => text.Text?.Length ?? 0,
                FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                FunctionCallContent call => call.Name.Length
                    + (call.Arguments is null ? 0 : JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions).Length),
                _ => 0
            };
        }

        return MessageOverheadTokens + (int)Math.Ceiling(characters / CharactersPerToken);
    }

    internal static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    /// <summary>
    /// Estimates the tool definitions sent with a call: each function's name, description and
    /// JSON schema count towards the input, because the provider sends them to the model.
    /// </summary>
    internal static int Estimate(IList<AITool>? tools)
    {
        if (tools is null)
        {
            return 0;
        }

        long characters = 0;
        foreach (var function in tools.OfType<AIFunction>())
        {
            characters += function.Name.Length
                + (function.Description?.Length ?? 0)
                + (function.JsonSchema.ValueKind == JsonValueKind.Undefined ? 0 : function.JsonSchema.GetRawText().Length);
        }

        return (int)Math.Ceiling(characters / CharactersPerToken);
    }

    /// <summary>
    /// Trims <paramref name="messages"/> to fit <paramref name="maxInputTokens"/>, alongside
    /// <paramref name="fixedTokens"/> that can't be trimmed (the tool definitions). System
    /// messages, the first user message (the fan's question in a tool loop) and the last message
    /// are never removed. The oldest tool call goes first, with its results; then the oldest
    /// other turn.
    /// </summary>
    internal static List<ChatMessage> Trim(List<ChatMessage> messages, int maxInputTokens, SquadAgent agent, int fixedTokens = 0)
    {
        int total = fixedTokens + Estimate(messages);
        if (total <= maxInputTokens)
        {
            return messages;
        }

        var kept = new List<ChatMessage>(messages);
        var firstUser = kept.Find(m => m.Role == ChatRole.User);
        var last = kept.Count > 0 ? kept[^1] : null;

        bool Protected(ChatMessage message) =>
            message.Role == ChatRole.System || ReferenceEquals(message, firstUser) || ReferenceEquals(message, last);

        while (total > maxInputTokens)
        {
            var (start, count) = OldestToolCall(kept, Protected);
            if (count == 0)
            {
                start = kept.FindIndex(m => !Protected(m) && m.Role != ChatRole.Tool && !IsToolCall(m));
                count = start < 0 ? 0 : 1;
            }

            if (count == 0)
            {
                throw Exceeded(agent, total, maxInputTokens);
            }

            for (int i = 0; i < count; i++)
            {
                total -= Estimate(kept[start]);
                kept.RemoveAt(start);
            }
        }

        return kept;
    }

    // The oldest assistant tool call that can go, with the tool results that follow it: a tool
    // result without its call is rejected by the API. A call whose results include a protected
    // message stays.
    private static (int Start, int Count) OldestToolCall(List<ChatMessage> messages, Func<ChatMessage, bool> isProtected)
    {
        for (int start = 0; start < messages.Count; start++)
        {
            if (!IsToolCall(messages[start]) || isProtected(messages[start]))
            {
                continue;
            }

            int end = start + 1;
            while (end < messages.Count && messages[end].Role == ChatRole.Tool)
            {
                end++;
            }

            bool removable = true;
            for (int i = start + 1; i < end; i++)
            {
                removable &= !isProtected(messages[i]);
            }

            if (removable)
            {
                return (start, end - start);
            }
        }

        return (-1, 0);
    }

    private static bool IsToolCall(ChatMessage message) =>
        message.Role == ChatRole.Assistant && message.Contents.Any(c => c is FunctionCallContent);

    private static SquadBudgetExceededException Exceeded(SquadAgent agent, int total, int max) =>
        new($"The {agent}'s input is about {total} tokens after trimming, over the {max}-token budget. The call was not made.");
}
