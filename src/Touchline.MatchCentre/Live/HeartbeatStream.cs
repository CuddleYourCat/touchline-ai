using System.Runtime.CompilerServices;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// Adds heartbeats to a live stream. Proxies and load balancers close a connection that has
/// been quiet for a while (Container Apps' ingress after about four minutes), and a match can
/// go quiet for longer than that at half-time.
/// </summary>
internal static class HeartbeatStream
{
    /// <summary>
    /// Passes every message through, and sends a heartbeat whenever none has gone for <paramref name="interval"/>.
    /// </summary>
    /// <param name="source">The stream.</param>
    /// <param name="interval">The longest quiet spell.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">Ends the stream.</param>
    /// <returns>The stream with heartbeats.</returns>
    /// <remarks>
    /// One timer runs at a time, and a message arriving doesn't reset it: when it fires, it
    /// checks how long the stream has really been quiet, and either sends a heartbeat or waits out
    /// the rest of the interval. So a busy stream costs a timer per interval, not per message.
    /// </remarks>
    public static async IAsyncEnumerable<LiveMessage> WithHeartbeats(
        IAsyncEnumerable<LiveMessage> source,
        TimeSpan interval,
        TimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The source gets its own token, so stopping early (a consumer that disposes without
        // cancelling) can still cancel a read in flight instead of waiting on it forever.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var messages = source.GetAsyncEnumerator(stop.Token);
        Task<bool>? next = null;
        Task? timer = null;
        var lastSent = time.GetUtcNow();
        try
        {
            while (true)
            {
                // The pending read is kept across heartbeats: starting a second MoveNextAsync
                // while one is in flight isn't allowed.
                next ??= messages.MoveNextAsync().AsTask();
                timer ??= Task.Delay(Max(TimeSpan.Zero, interval - (time.GetUtcNow() - lastSent)), time, cancellationToken);

                if (await Task.WhenAny(next, timer) == next)
                {
                    if (!await next)
                    {
                        yield break;
                    }

                    next = null;
                    lastSent = time.GetUtcNow();
                    yield return messages.Current;
                    continue;
                }

                timer = null;
                cancellationToken.ThrowIfCancellationRequested();
                if (time.GetUtcNow() - lastSent >= interval)
                {
                    lastSent = time.GetUtcNow();
                    yield return new LiveMessage(StreamEvents.Heartbeat, null, new Heartbeat(lastSent));
                }
            }
        }
        finally
        {
            // Disposing while a read is in flight throws, so cancel the read and let it finish first.
            if (next is not null)
            {
                await stop.CancelAsync();
                try
                {
                    await next;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await messages.DisposeAsync();
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
