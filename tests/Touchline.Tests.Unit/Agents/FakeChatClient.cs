using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Touchline.Tests.Unit.Agents;

/// <summary>
/// A scripted <see cref="IChatClient"/> for unit tests: records every request and returns
/// queued responses, so no test calls a live model. Safe to call concurrently.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<Func<ChatResponse>> _script = new();
    private readonly List<FakeChatRequest> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<FakeChatRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The response used when the script is empty.</summary>
    public ChatResponse DefaultResponse { get; set; } = Reply("OK", inputTokens: 10, outputTokens: 2);

    /// <summary>When set, answers every request instead of the script, for tests that route or gate by request.</summary>
    public Func<FakeChatRequest, CancellationToken, Task<ChatResponse>>? Handler { get; set; }

    public static ChatResponse Reply(string text, long inputTokens, long outputTokens) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        };

    public FakeChatClient Returns(string text, long inputTokens = 10, long outputTokens = 2)
    {
        lock (_gate)
        {
            _script.Enqueue(() => Reply(text, inputTokens, outputTokens));
        }

        return this;
    }

    public FakeChatClient Throws(Exception exception)
    {
        lock (_gate)
        {
            _script.Enqueue(() => throw exception);
        }

        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = Record(messages, options);
        return Handler is { } handler ? handler(request, cancellationToken) : Task.FromResult(Next());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Record(messages, options);
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

    private FakeChatRequest Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var request = new FakeChatRequest([.. messages], options);
        lock (_gate)
        {
            _requests.Add(request);
        }

        return request;
    }

    private ChatResponse Next()
    {
        Func<ChatResponse>? next;
        lock (_gate)
        {
            _script.TryDequeue(out next);
        }

        return next is null ? DefaultResponse : next();
    }
}

/// <summary>One request the fake received.</summary>
internal sealed record FakeChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
