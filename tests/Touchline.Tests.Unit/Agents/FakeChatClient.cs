using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Touchline.Tests.Unit.Agents;

/// <summary>
/// A scripted <see cref="IChatClient"/> for unit tests: records every request and returns
/// queued responses, so no test calls a live model.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<Func<ChatResponse>> _script = new();

    public List<FakeChatRequest> Requests { get; } = [];

    /// <summary>The response used when the script is empty.</summary>
    public ChatResponse DefaultResponse { get; set; } = Reply("OK", inputTokens: 10, outputTokens: 2);

    public static ChatResponse Reply(string text, long inputTokens, long outputTokens) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        };

    public FakeChatClient Returns(string text, long inputTokens = 10, long outputTokens = 2)
    {
        _script.Enqueue(() => Reply(text, inputTokens, outputTokens));
        return this;
    }

    public FakeChatClient Throws(Exception exception)
    {
        _script.Enqueue(() => throw exception);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(new FakeChatRequest([.. messages], options));
        return Task.FromResult(Next());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(new FakeChatRequest([.. messages], options));
        foreach (var update in Next().ToChatResponseUpdates())
        {
            await Task.Yield();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private ChatResponse Next() => _script.Count > 0 ? _script.Dequeue()() : DefaultResponse;
}

/// <summary>One request the fake received.</summary>
internal sealed record FakeChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
