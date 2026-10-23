using System.Runtime.CompilerServices;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// The live stream over the Cosmos DB change feed (ADR-029, ADR-037): every stream of a match on
/// this replica shares the match's <see cref="Live.LiveMatchHub"/>.
/// </summary>
/// <param name="hubs">The replica's hubs.</param>
internal sealed class CosmosLiveFeed(LiveMatchHubs hubs) : ILiveFeed
{
    /// <inheritdoc />
    /// <remarks>
    /// Loads the match's hub, which waits a bounded time for the change feed to start, so the
    /// stream that follows finds it ready.
    /// </remarks>
    public Task PrepareAsync(string matchId, CancellationToken cancellationToken) => hubs.GetAsync(matchId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// With no <paramref name="after"/> the stream starts with a state snapshot, as the demo does;
    /// otherwise it replays the hub's messages after it. Then it follows live, and ends after
    /// <see cref="StreamEvents.FullTime"/> or when the hub is evicted.
    /// </remarks>
    public async IAsyncEnumerable<LiveMessage> FollowAsync(
        string matchId,
        long? after,
        Viewer viewer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var hub = await hubs.GetAsync(matchId, cancellationToken);
        using var subscription = hub.Subscribe(after, viewer);
        foreach (var message in subscription.Backlog)
        {
            yield return message;
            if (message.Event == StreamEvents.FullTime)
            {
                yield break;
            }
        }

        await foreach (var message in subscription.Live.ReadAllAsync(cancellationToken))
        {
            yield return message;
            if (message.Event == StreamEvents.FullTime)
            {
                yield break;
            }
        }
    }
}
