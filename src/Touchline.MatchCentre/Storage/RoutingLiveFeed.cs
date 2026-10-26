using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// The live stream behind the API (ADR-036): <c>demo-*</c> IDs replay the recording, every other
/// ID follows the Cosmos DB change feed.
/// </summary>
/// <param name="demo">The recorded demo match.</param>
/// <param name="cosmos">The change-feed stream, or null when Cosmos DB isn't configured.</param>
/// <param name="logger">Logs a Cosmos DB failure.</param>
internal sealed partial class RoutingLiveFeed(ILiveFeed demo, ILiveFeed? cosmos, ILogger<RoutingLiveFeed> logger) : ILiveFeed
{
    /// <inheritdoc />
    /// <remarks>
    /// A Cosmos DB stream that fails after it has started can't become a <c>503</c> any more:
    /// the failure is logged once and the stream ends, so the client reconnects.
    /// </remarks>
    public IAsyncEnumerable<LiveMessage> FollowAsync(
        string matchId, long? after, Viewer viewer, StreamReplay? replay, CancellationToken cancellationToken) =>
        MatchSources.ServesFromCosmos(matchId, cosmos is not null)
            ? EndOnFailureAsync(cosmos!.FollowAsync(matchId, after, viewer, replay, cancellationToken), matchId, cancellationToken)
            : demo.FollowAsync(matchId, after, viewer, replay, cancellationToken);

    /// <inheritdoc />
    public Task PrepareAsync(string matchId, CancellationToken cancellationToken)
    {
        var source = Pick(matchId);
        return source == demo
            ? demo.PrepareAsync(matchId, cancellationToken)
            : MatchSources.GuardAsync(
                async () =>
                {
                    await source.PrepareAsync(matchId, cancellationToken);
                    return true;
                },
                $"the live stream of '{matchId}'",
                logger,
                cancellationToken);
    }

    private ILiveFeed Pick(string matchId) => MatchSources.ServesFromCosmos(matchId, cosmos is not null) ? cosmos! : demo;

    // A yield can't sit inside a try with a catch, so the enumerator is driven by hand.
    private async IAsyncEnumerable<LiveMessage> EndOnFailureAsync(
        IAsyncEnumerable<LiveMessage> messages,
        string matchId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = messages.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (MatchSources.IsOutage(exception, cancellationToken))
                {
                    LogStreamEnded(logger, exception, matchId);
                }
                else
                {
                    LogStreamFailed(logger, exception, matchId);
                }

                break;
            }

            yield return enumerator.Current;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live stream of {MatchId} lost Cosmos DB after it started, so it ended")]
    private static partial void LogStreamEnded(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The live stream of {MatchId} failed after it started, so it ended")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception, string matchId);
}
