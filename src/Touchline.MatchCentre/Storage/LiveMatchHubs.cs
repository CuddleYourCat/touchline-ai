using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
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
/// <param name="options">
/// How long a stream waits for the change feed to start, or null for the <see cref="LiveFeedOptions"/>
/// defaults. Every parameter resolves from DI, so the container builds this class without a factory.
/// </param>
/// <param name="teams">The team profiles, for the shape model, or null to send no shapes.</param>
/// <param name="notable">The notable-moment detector, or null for one over <paramref name="detector"/>.</param>
/// <param name="matchTeams">
/// Each match's team profiles (ADR-050), read when its hub is created, so a fan match's moments,
/// lines and shapes use its fan players. Null for <paramref name="teams"/> alone.
/// </param>
internal sealed partial class LiveMatchHubs(
    CosmosClient client,
    ICommentaryStore commentary,
    KeyMomentDetector detector,
    TimeProvider time,
    ILogger<LiveMatchHubs> logger,
    int buffer = LiveMatchHubs.DefaultBuffer,
    IOptions<LiveFeedOptions>? options = null,
    IReadOnlyDictionary<string, TeamProfile>? teams = null,
    NotableMomentDetector? notable = null,
    IMatchTeamProfiles? matchTeams = null)
{
    /// <summary>The default per-stream buffer: several minutes of a busy match.</summary>
    public const int DefaultBuffer = 256;

    /// <summary>
    /// After a wait for the feed start times out, how long later streams are refused at once,
    /// before one stream may wait again.
    /// </summary>
    public static readonly TimeSpan FailFastWindow = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _feedStartTimeout = (options?.Value ?? new LiveFeedOptions()).StartTimeout;

    /// <summary>A match's whole event history, in its own partition.</summary>
    internal const string EventsQuery = "SELECT * FROM c ORDER BY c.sequence";

    /// <summary>The events in a gap, in the match's own partition.</summary>
    internal const string GapQuery = "SELECT * FROM c WHERE c.sequence > @last AND c.sequence < @next ORDER BY c.sequence";

    private readonly Dictionary<string, LiveMatchHub> _hubs = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _feedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? _failFastUntil;
    private bool _probing;

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
    /// <returns>The loaded hub. One that's already loaded comes back at once, without waiting.</returns>
    /// <exception cref="TimeoutException">
    /// The change feed hasn't started within the bounded wait, as when Cosmos DB is down: the
    /// stream fails fast instead of hanging, and no hub is created. For
    /// <see cref="FailFastWindow"/> after such a timeout, later calls throw at once.
    /// </exception>
    public async Task<LiveMatchHub> GetAsync(string matchId, CancellationToken cancellationToken)
    {
        LiveMatchHub hub;
        lock (_gate)
        {
            // A loaded hub only exists once the feed has started, so the stream that follows a
            // PrepareAsync gets the same hub straight back.
            if (_hubs.TryGetValue(matchId, out var loaded) && loaded.Ready.IsCompletedSuccessfully)
            {
                return loaded;
            }
        }

        await WaitForFeedAsync(cancellationToken);

        // Read outside the lock; a fan match's roster never changes, so the hub keeps it for life.
        var profiles = matchTeams is null ? null : await matchTeams.ForMatchAsync(matchId, cancellationToken);

        lock (_gate)
        {
            if (!_hubs.TryGetValue(matchId, out hub!))
            {
                // Line-ups are null until the B-lane wiring reads them from the stored match
                // document; until then shapes start with the metadata event (sequence 1).
                hub = profiles is null
                    ? new LiveMatchHub(matchId, detector, time, buffer, BackfillAsync, logger, teams, lineups: null, notable)
                    : new LiveMatchHub(
                        matchId, detector.ForTeams(profiles), time, buffer, BackfillAsync, logger, profiles, lineups: null,
                        notable?.ForTeams(profiles));
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

    // The bounded wait for the feed start. After a timeout, calls fail at once for FailFastWindow;
    // then one call (the probe) waits again while the others keep failing fast.
    private async Task WaitForFeedAsync(CancellationToken cancellationToken)
    {
        if (_feedStarted.Task.IsCompleted)
        {
            return;
        }

        bool probe;
        lock (_gate)
        {
            probe = _failFastUntil is not null;
            if (probe && (_probing || time.GetUtcNow() < _failFastUntil))
            {
                throw new TimeoutException("The change feed hasn't started, and a recent wait for it timed out.");
            }

            _probing |= probe;
        }

        try
        {
            await _feedStarted.Task.WaitAsync(_feedStartTimeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                _failFastUntil = time.GetUtcNow() + FailFastWindow;
            }

            throw;
        }
        finally
        {
            if (probe)
            {
                lock (_gate)
                {
                    _probing = false;
                }
            }
        }
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
