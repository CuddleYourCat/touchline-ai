using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.MatchCentre.Live;
using Touchline.Storage;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// This replica's live match hubs (ADR-037): one per match with someone watching, created by the
/// first stream and fed by the <see cref="ChangeFeedReader"/>.
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
/// <param name="commentary">The stored commentary, loaded with a new hub.</param>
/// <param name="detector">The key-moment detector.</param>
/// <param name="time">The clock, for eviction.</param>
/// <param name="logger">Logs a failed load, and the hubs' gaps and overflows.</param>
/// <param name="buffer">How many messages a stream can fall behind before it's ended.</param>
internal sealed partial class LiveMatchHubs(
    CosmosClient client,
    ICommentaryStore commentary,
    KeyMomentDetector detector,
    TimeProvider time,
    ILogger<LiveMatchHubs> logger,
    int buffer = LiveMatchHubs.DefaultBuffer)
{
    /// <summary>The default per-stream buffer: several minutes of a busy match.</summary>
    public const int DefaultBuffer = 256;

    /// <summary>A match's whole event history, in its own partition.</summary>
    internal const string EventsQuery = "SELECT * FROM c ORDER BY c.sequence";

    /// <summary>The events in a gap, in the match's own partition.</summary>
    internal const string GapQuery = "SELECT * FROM c WHERE c.sequence > @last AND c.sequence < @next ORDER BY c.sequence";

    private readonly Dictionary<string, LiveMatchHub> _hubs = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _feedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the change-feed reader has taken its start point.</summary>
    public Task FeedStarted => _feedStarted.Task;

    /// <summary>How many hubs this replica has; for tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _hubs.Count;
            }
        }
    }

    private Container Events => client.GetContainer(CosmosNames.Database, CosmosNames.Events);

    /// <summary>
    /// Says the change-feed reader has its starting continuation. Until then no hub loads, so no
    /// event can fall between a hub's load query and the feed's start point.
    /// </summary>
    public void MarkFeedStarted() => _feedStarted.TrySetResult();

    /// <summary>
    /// Gets the match's hub, creating and loading it on the first stream.
    /// </summary>
    /// <param name="matchId">The match, which the caller has checked exists.</param>
    /// <param name="cancellationToken">Stops waiting; the load itself carries on for the next stream.</param>
    /// <returns>The loaded hub.</returns>
    public async Task<LiveMatchHub> GetAsync(string matchId, CancellationToken cancellationToken)
    {
        LiveMatchHub hub;
        lock (_gate)
        {
            if (!_hubs.TryGetValue(matchId, out hub!))
            {
                hub = new LiveMatchHub(matchId, detector, time, buffer, BackfillAsync, logger);
                hub.Ready = LoadAsync(hub);
                _hubs[matchId] = hub;
            }
        }

        await hub.Ready.WaitAsync(cancellationToken);
        return hub;
    }

    /// <summary>Hands a changed event to its match's hub, if this replica has one.</summary>
    /// <param name="matchEvent">The event.</param>
    public void Dispatch(MatchEvent matchEvent) => Find(matchEvent.MatchId)?.OnEvent(matchEvent);

    /// <summary>Hands a changed commentary line to its match's hub, if this replica has one.</summary>
    /// <param name="line">The line.</param>
    public void Dispatch(CommentaryLine line) => Find(line.MatchId)?.OnCommentary(line);

    /// <summary>Drops the hubs that have expired, ending their streams.</summary>
    /// <returns>How many were dropped.</returns>
    public int EvictExpired()
    {
        var now = time.GetUtcNow();
        List<LiveMatchHub> expired;
        lock (_gate)
        {
            expired = [.. _hubs.Values.Where(hub => hub.Ready.IsCompletedSuccessfully && hub.IsExpired(now))];
            foreach (var hub in expired)
            {
                _hubs.Remove(hub.MatchId);
            }
        }

        foreach (var hub in expired)
        {
            hub.Close();
        }

        return expired.Count;
    }

    private LiveMatchHub? Find(string matchId)
    {
        lock (_gate)
        {
            return _hubs.GetValueOrDefault(matchId);
        }
    }

    // Not cancelled by the first stream's token: other streams may be waiting on the same load.
    private async Task LoadAsync(LiveMatchHub hub)
    {
        await Task.Yield();
        try
        {
            await _feedStarted.Task;
            var partition = new PartitionKey(hub.MatchId);
            var events = await CosmosQueries.ReadAllAsync<MatchEvent>(Events, new QueryDefinition(EventsQuery), partition, CancellationToken.None);
            IReadOnlyList<CommentaryLine> lines;
            try
            {
                lines = await commentary.GetAsync(hub.MatchId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Commentary is an extra: the stream goes out without the earlier lines.
                LogCommentaryUnavailable(logger, exception, hub.MatchId);
                lines = [];
            }

            hub.Load(events, lines);
        }
        catch (Exception exception)
        {
            // A failed load isn't kept, whether or not a stream is still waiting, so nothing
            // leaks and the next stream tries again.
            hub.Fault();
            lock (_gate)
            {
                if (_hubs.GetValueOrDefault(hub.MatchId) == hub)
                {
                    _hubs.Remove(hub.MatchId);
                }
            }

            LogLoadFailed(logger, exception, hub.MatchId);
            throw;
        }
    }

    private async Task<IReadOnlyList<MatchEvent>> BackfillAsync(string matchId, long after, long before, CancellationToken cancellationToken) =>
        await CosmosQueries.ReadAllAsync<MatchEvent>(
            Events,
            new QueryDefinition(GapQuery).WithParameter("@last", after).WithParameter("@next", before),
            new PartitionKey(matchId),
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored commentary for {MatchId} couldn't be read, so its live hub starts without it")]
    private static partial void LogCommentaryUnavailable(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live hub for {MatchId} couldn't load, so it was dropped")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string matchId);
}
