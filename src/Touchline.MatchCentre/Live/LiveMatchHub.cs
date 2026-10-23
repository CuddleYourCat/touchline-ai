using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;
using Touchline.Storage;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// Reads a match's stored events strictly between two sequences, to fill a gap in the change feed.
/// </summary>
/// <param name="matchId">The match.</param>
/// <param name="after">The last sequence the hub has.</param>
/// <param name="before">The first sequence it holds after the gap.</param>
/// <param name="cancellationToken">Stops the read.</param>
/// <returns>The stored events in the gap, in any order.</returns>
internal delegate Task<IReadOnlyList<MatchEvent>> EventBackfill(string matchId, long after, long before, CancellationToken cancellationToken);

/// <summary>
/// One live match on one replica (ADR-037): a single <see cref="MatchStreamProjector"/> fed by
/// the match's stored events and then the change feed, fanned out to every open stream. However
/// many fans watch, the match is read from Cosmos DB once.
/// </summary>
/// <remarks>
/// One lock guards the projection, the history and the subscriber list, so a new stream's
/// backlog and its live messages join with nothing missed and nothing sent twice. Nothing inside
/// the lock waits: writes to the subscribers' channels never block, and a gap's backfill query
/// runs outside it.
/// </remarks>
internal sealed partial class LiveMatchHub
{
    /// <summary>How long a hub outlives full time, or its last subscriber.</summary>
    public static readonly TimeSpan EvictAfter = TimeSpan.FromMinutes(10);

    /// <summary>The most change-feed events held while loading or backfilling; a safety cap.</summary>
    public const int MaxPending = 1000;

    private readonly MatchStreamProjector _projector;
    private readonly TimeProvider _time;
    private readonly int _buffer;
    private readonly EventBackfill _backfill;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<(long Sequence, IReadOnlyList<LiveMessage> Messages)> _history = [];
    private readonly Dictionary<string, Arrival> _commentary = [];
    private readonly List<MatchEvent> _pending = [];
    private readonly SortedDictionary<long, MatchEvent> _held = [];
    private readonly HashSet<long> _skipped = [];
    private readonly List<Subscriber> _subscribers = [];
    private bool _backfilling;
    private bool _loaded;
    private bool _faulted;
    private bool _closed;
    private bool _capLogged;
    private DateTimeOffset? _fullTimeAt;
    private DateTimeOffset _idleSince;

    /// <summary>Creates an empty hub, which buffers the change feed until it's loaded.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="detector">The key-moment detector.</param>
    /// <param name="time">The clock, for eviction.</param>
    /// <param name="buffer">How many messages a subscriber can fall behind before its stream is ended.</param>
    /// <param name="backfill">Reads the stored events in a gap.</param>
    /// <param name="logger">Logs gaps, skips and overflows.</param>
    public LiveMatchHub(string matchId, KeyMomentDetector detector, TimeProvider time, int buffer, EventBackfill backfill, ILogger logger)
    {
        MatchId = matchId;
        _projector = new MatchStreamProjector(matchId, detector);
        _time = time;
        _buffer = buffer;
        _backfill = backfill;
        _logger = logger;
        _idleSince = time.GetUtcNow();
    }

    /// <summary>The match.</summary>
    public string MatchId { get; }

    /// <summary>Completes when the stored events are loaded; set by the registry.</summary>
    public Task Ready { get; set; } = Task.CompletedTask;

    /// <summary>How many change-feed events are waiting for the load; for tests.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Loads the match as stored when the hub was created, then any change-feed events that
    /// arrived while it was loading.
    /// </summary>
    /// <param name="events">The stored events.</param>
    /// <param name="lines">The stored commentary.</param>
    public void Load(IEnumerable<MatchEvent> events, IEnumerable<CommentaryLine> lines)
    {
        Gap? gap = null;
        lock (_gate)
        {
            // The store is the truth, so a hole in it is permanent and skipped.
            foreach (var matchEvent in events.OrderBy(e => e.Sequence))
            {
                Project(matchEvent);
            }

            // A line from the feed is newer than the stored copy, so the stored one doesn't replace it.
            foreach (var line in lines)
            {
                _commentary.TryAdd(CosmosCommentaryStore.DocumentId(line), new Arrival(line, line.Sequence));
            }

            // Lines loaded with the hub count as arriving at their moment, but never after the stream.
            var last = _projector.State.LastSequence;
            foreach (var (id, arrival) in _commentary.ToList())
            {
                _commentary[id] = arrival with { Sequence = Math.Min(arrival.Sequence, last) };
            }

            _loaded = true;
            foreach (var matchEvent in _pending.OrderBy(e => e.Sequence))
            {
                var found = Accept(matchEvent);
                gap ??= found;
            }

            _pending.Clear();
        }

        StartBackfill(gap);
    }

    /// <summary>Marks the load as failed: the hub ignores the change feed from now on.</summary>
    public void Fault()
    {
        lock (_gate)
        {
            _faulted = true;
            _pending.Clear();
            _held.Clear();
        }
    }

    /// <summary>
    /// Takes an event from the change feed. One the hub already has (a repeat, or one the stored
    /// load included) is dropped by sequence; one after a gap is held while the gap is backfilled
    /// from the store, and the gap is skipped only if the store doesn't have it either.
    /// </summary>
    /// <param name="matchEvent">The event.</param>
    public void OnEvent(MatchEvent matchEvent)
    {
        Gap? gap;
        lock (_gate)
        {
            if (_faulted || _closed)
            {
                return;
            }

            if (!_loaded)
            {
                // A dropped event is a gap after the load, which the backfill fills.
                if (_pending.Count < MaxPending)
                {
                    _pending.Add(matchEvent);
                }
                else
                {
                    LogCapOnce();
                }

                return;
            }

            gap = Accept(matchEvent);
        }

        StartBackfill(gap);
    }

    /// <summary>
    /// Takes a commentary line from the change feed and sends it to every stream whose viewer it
    /// suits, unless that viewer already has a better line for the moment. A model line
    /// replacing a template line is sent again, as an upgrade.
    /// </summary>
    /// <param name="line">The line.</param>
    public void OnCommentary(CommentaryLine line)
    {
        lock (_gate)
        {
            if (_faulted || _closed)
            {
                return;
            }

            // It arrived now, so it's sent with the last sequence the stream has sent: a
            // reconnect after that replays it, and the stream's IDs never move back.
            var arrived = _loaded ? _projector.State.LastSequence : line.Sequence;
            _commentary[CosmosCommentaryStore.DocumentId(line)] = new Arrival(line, arrived);
            if (!_loaded)
            {
                return;
            }

            var message = new LiveMessage(StreamEvents.Commentary, LiveMessage.IdFor(arrived), line);
            List<Subscriber>? overflowed = null;
            foreach (var subscriber in _subscribers)
            {
                if (subscriber.After >= arrived || !subscriber.Offer(line))
                {
                    continue;
                }

                if (!subscriber.Channel.Writer.TryWrite(message))
                {
                    (overflowed ??= []).Add(subscriber);
                }
            }

            End(overflowed);
        }
    }

    /// <summary>
    /// Opens a stream on the hub.
    /// </summary>
    /// <param name="after">
    /// Null for "from now", which starts with a state snapshot; otherwise the last sequence the
    /// client has seen, and the backlog replays everything after it.
    /// </param>
    /// <param name="viewer">Whose commentary to send.</param>
    /// <returns>The subscription, which the caller disposes when the stream ends.</returns>
    public Subscription Subscribe(long? after, Viewer viewer)
    {
        lock (_gate)
        {
            var subscriber = new Subscriber(viewer, after, Channel.CreateBounded<LiveMessage>(new BoundedChannelOptions(_buffer)
            {
                // A slow stream (a fan on a poor connection) must never hold up the others or the
                // change-feed reader. Dropping its messages could lose moments and goals, so a
                // full buffer ends its stream instead: the client reconnects with Last-Event-ID
                // and gets a correct replay. TryWrite never waits in this mode; it returns false.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }));

            var state = _projector.State;
            var backlog = new List<LiveMessage>();
            if (after is null)
            {
                backlog.Add(new LiveMessage(StreamEvents.State, LiveMessage.IdFor(state.LastSequence), LiveState.From(state)));
            }
            else
            {
                Replay(after.Value, subscriber, backlog);
            }

            if (state.FullTime)
            {
                // The client closes only on full time, so a finished match's stream always ends with it.
                var fullTime = backlog.FindLast(m => m.Event == StreamEvents.FullTime)
                    ?? new LiveMessage(StreamEvents.FullTime, LiveMessage.IdFor(state.LastSequence), LiveState.From(state));
                backlog.RemoveAll(m => m.Event == StreamEvents.FullTime);
                backlog.Add(fullTime);
                subscriber.Channel.Writer.TryComplete();
            }
            else if (_closed)
            {
                subscriber.Channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(subscriber);
            }

            return new Subscription(backlog, subscriber.Channel.Reader, () => Unsubscribe(subscriber));
        }
    }

    /// <summary>Says whether the hub can be dropped: 10 minutes after full time, or with nobody watching for 10 minutes.</summary>
    /// <param name="now">The time now.</param>
    /// <returns>True when it's expired.</returns>
    public bool IsExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            return (_fullTimeAt is { } fullTime && now - fullTime >= EvictAfter)
                || (_subscribers.Count == 0 && now - _idleSince >= EvictAfter);
        }
    }

    /// <summary>Ends every open stream; called when the hub is evicted.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Channel.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    // The history after `after`, with the best suitable line per moment where that line arrived
    // after `after`. One pass over each, merged by arrival.
    private void Replay(long after, Subscriber subscriber, List<LiveMessage> backlog)
    {
        var best = _commentary.Values
            .Select(a => (a.Line, Arrived: a.Sequence, Rank: subscriber.Rank(a.Line)))
            .Where(r => r.Rank <= CommentarySelection.WorstInMode)
            .GroupBy(r => r.Line.MomentId)
            .Select(g => g.OrderBy(r => r.Rank).ThenByDescending(r => r.Arrived).First())
            .ToList();
        foreach (var line in best)
        {
            // Sent now, or before the reconnect: either way the client has it.
            subscriber.Sent[line.Line.MomentId] = line.Rank!.Value;
        }

        var due = best.Where(r => r.Arrived > after).OrderBy(r => r.Arrived).ToList();
        var next = 0;
        foreach (var (sequence, messages) in _history)
        {
            if (sequence <= after)
            {
                continue;
            }

            backlog.AddRange(messages);
            for (; next < due.Count && due[next].Arrived <= sequence; next++)
            {
                backlog.Add(new LiveMessage(StreamEvents.Commentary, LiveMessage.IdFor(sequence), due[next].Line));
            }
        }

        for (; next < due.Count; next++)
        {
            backlog.Add(new LiveMessage(StreamEvents.Commentary, LiveMessage.IdFor(_projector.State.LastSequence), due[next].Line));
        }
    }

    // Takes a loaded hub's feed event: projects it, holds it behind a backfill, or drops a repeat.
    // Returns the gap to backfill, which the caller starts outside the lock.
    private Gap? Accept(MatchEvent matchEvent)
    {
        var last = _projector.State.LastSequence;
        if (matchEvent.MatchId != MatchId)
        {
            return null;
        }

        if (matchEvent.Sequence <= last)
        {
            if (_skipped.Contains(matchEvent.Sequence))
            {
                LogLateEvent(_logger, MatchId, matchEvent.Sequence);
            }

            return null;
        }

        if (_backfilling)
        {
            Hold(matchEvent);
            return null;
        }

        if (matchEvent.Sequence > last + 1)
        {
            Hold(matchEvent);
            _backfilling = true;
            return new Gap(last, matchEvent.Sequence);
        }

        Broadcast(matchEvent.Sequence, Project(matchEvent));
        return null;
    }

    private void Hold(MatchEvent matchEvent)
    {
        if (_held.Count < MaxPending)
        {
            _held.TryAdd(matchEvent.Sequence, matchEvent);
        }
        else
        {
            LogCapOnce();
        }
    }

    private void StartBackfill(Gap? gap)
    {
        if (gap is { } g)
        {
            _ = BackfillAsync(g);
        }
    }

    private async Task BackfillAsync(Gap gap)
    {
        LogBackfilling(_logger, MatchId, gap.After, gap.Before);
        IReadOnlyList<MatchEvent> found;
        try
        {
            found = await _backfill(MatchId, gap.After, gap.Before, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogBackfillFailed(_logger, exception, MatchId, gap.After, gap.Before);
            found = [];
        }

        Gap? next = null;
        lock (_gate)
        {
            _backfilling = false;
            if (_faulted || _closed)
            {
                _held.Clear();
                return;
            }

            foreach (var matchEvent in found.Where(e => e.Sequence > gap.After && e.Sequence < gap.Before))
            {
                _held.TryAdd(matchEvent.Sequence, matchEvent);
            }

            var held = _held.Values.ToList();
            _held.Clear();
            foreach (var matchEvent in held)
            {
                var last = _projector.State.LastSequence;
                if (next is not null)
                {
                    _held[matchEvent.Sequence] = matchEvent;
                }
                else if (matchEvent.Sequence > last + 1 && matchEvent.Sequence > gap.Before)
                {
                    // A new gap beyond the one just filled: hold the rest for its own backfill.
                    _held[matchEvent.Sequence] = matchEvent;
                    _backfilling = true;
                    next = new Gap(last, matchEvent.Sequence);
                }
                else
                {
                    // A hole the backfill couldn't fill is skipped (and logged) by Project.
                    Broadcast(matchEvent.Sequence, Project(matchEvent));
                }
            }
        }

        StartBackfill(next);
    }

    // Projects an event the hub hasn't had, or returns null for a repeat.
    private IReadOnlyList<LiveMessage>? Project(MatchEvent matchEvent)
    {
        var last = _projector.State.LastSequence;
        if (matchEvent.MatchId != MatchId || matchEvent.Sequence <= last)
        {
            return null;
        }

        if (matchEvent.Sequence > last + 1)
        {
            for (var missing = last + 1; missing < matchEvent.Sequence; missing++)
            {
                _skipped.Add(missing);
            }

            LogSkipped(_logger, MatchId, last + 1, matchEvent.Sequence - 1);
        }

        var wasFullTime = _projector.State.FullTime;
        var messages = _projector.Apply(matchEvent).Messages;
        _history.Add((matchEvent.Sequence, messages));
        if (_projector.State.FullTime && !wasFullTime)
        {
            _fullTimeAt = _time.GetUtcNow();
        }

        return messages;
    }

    private void Broadcast(long sequence, IReadOnlyList<LiveMessage>? messages)
    {
        if (messages is null)
        {
            return;
        }

        List<Subscriber>? overflowed = null;
        foreach (var subscriber in _subscribers)
        {
            // A client ahead of the hub (it was on another replica) already has these.
            if (subscriber.After >= sequence)
            {
                continue;
            }

            foreach (var message in messages)
            {
                if (!subscriber.Channel.Writer.TryWrite(message))
                {
                    (overflowed ??= []).Add(subscriber);
                    break;
                }
            }
        }

        End(overflowed);
    }

    // Ends the streams that fell too far behind, so their clients reconnect and replay.
    private void End(List<Subscriber>? overflowed)
    {
        if (overflowed is null)
        {
            return;
        }

        foreach (var subscriber in overflowed)
        {
            subscriber.Channel.Writer.TryComplete();
            _subscribers.Remove(subscriber);
            LogOverflow(_logger, MatchId, _buffer);
        }

        if (_subscribers.Count == 0)
        {
            _idleSince = _time.GetUtcNow();
        }
    }

    private void LogCapOnce()
    {
        if (!_capLogged)
        {
            _capLogged = true;
            LogCapHit(_logger, MatchId, MaxPending);
        }
    }

    private void Unsubscribe(Subscriber subscriber)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(subscriber) && _subscribers.Count == 0)
            {
                _idleSince = _time.GetUtcNow();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Live hub for {MatchId} is backfilling the gap between {After} and {Before}")]
    private static partial void LogBackfilling(ILogger logger, string matchId, long after, long before);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live hub for {MatchId} couldn't backfill the gap between {After} and {Before}")]
    private static partial void LogBackfillFailed(ILogger logger, Exception exception, string matchId, long after, long before);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live hub for {MatchId} skipped sequences {From} to {To}, which the store doesn't have")]
    private static partial void LogSkipped(ILogger logger, string matchId, long from, long to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live hub for {MatchId} got event {Sequence} late, after skipping it")]
    private static partial void LogLateEvent(ILogger logger, string matchId, long sequence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live hub for {MatchId} hit its cap of {Cap} held events, so later ones wait for a backfill")]
    private static partial void LogCapHit(ILogger logger, string matchId, int cap);

    [LoggerMessage(Level = LogLevel.Information, Message = "A stream of {MatchId} fell {Buffer} messages behind, so it was ended for the client to reconnect")]
    private static partial void LogOverflow(ILogger logger, string matchId, int buffer);

    private readonly record struct Gap(long After, long Before);

    // A commentary line and the hub's sequence when it arrived.
    private sealed record Arrival(CommentaryLine Line, long Sequence);

    private sealed class Subscriber(Viewer viewer, long? after, Channel<LiveMessage> channel)
    {
        public long? After { get; } = after;

        public Channel<LiveMessage> Channel { get; } = channel;

        // The best rank sent so far, per moment.
        public Dictionary<string, int> Sent { get; } = [];

        public int? Rank(CommentaryLine line) => CommentarySelection.Rank(line, viewer.Mode, viewer.Language);

        // Records and allows a line no worse than the best already sent for its moment.
        public bool Offer(CommentaryLine line)
        {
            if (Rank(line) is not { } rank || rank > CommentarySelection.WorstInMode
                || (Sent.TryGetValue(line.MomentId, out var best) && rank > best))
            {
                return false;
            }

            Sent[line.MomentId] = rank;
            return true;
        }
    }
}

/// <summary>
/// One stream's place on a hub.
/// </summary>
/// <param name="backlog">What to send first: the snapshot, or the replay after <c>after</c>.</param>
/// <param name="live">The messages after the backlog; completes when the hub is evicted or the stream falls too far behind.</param>
/// <param name="leave">Takes the stream off the hub.</param>
internal sealed class Subscription(IReadOnlyList<LiveMessage> backlog, ChannelReader<LiveMessage> live, Action leave) : IDisposable
{
    /// <summary>What to send first.</summary>
    public IReadOnlyList<LiveMessage> Backlog { get; } = backlog;

    /// <summary>The live messages; completes when the hub is evicted, the stream overflows, or the match was already over.</summary>
    public ChannelReader<LiveMessage> Live { get; } = live;

    /// <inheritdoc />
    public void Dispose() => leave();
}
