using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Season;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Live;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// Serves the recorded match as if it were live (ADR-027), so the UI has realistic data before
/// the live read side exists, and the Match Centre still has a match to show when the pipeline
/// is cold.
/// </summary>
/// <remarks>
/// <para>
/// The match replays in <b>laps</b> on one shared clock, which starts when the service does.
/// Lap 1 kicks off at once, plays at <see cref="DemoOptions.Speed"/>, pauses for
/// <see cref="DemoOptions.LapGap"/> after full time, and lap 2 kicks off. Each lap is its own
/// match, <c>demo-1</c>, <c>demo-2</c> and so on, so stream IDs never go backwards within a
/// match. Every endpoint and every stream agrees on how far a lap has got.
/// </para>
/// <para>
/// Commentary and overlays here are stand-ins (the detector's reason, and a plain card) until
/// the agent and moment-tier lanes supply the real ones.
/// </para>
/// </remarks>
internal sealed class DemoMatchSource : IMatchReadModel, ILiveFeed
{
    /// <summary>Every demo match ID starts with this.</summary>
    public const string IdPrefix = "demo-";

    /// <summary>How long an overlay card shows for.</summary>
    public const int OverlayDurationMs = 6000;

    private readonly IReadOnlyList<MatchEvent> _events;
    private readonly TimeSpan[] _offsets;
    private readonly MatchState[] _states;
    private readonly List<(int Index, KeyMoment Moment)> _moments = [];
    private readonly KeyMoment?[] _momentAt;
    private readonly Dictionary<string, IReadOnlyList<LiveMessage>[]> _projections = new(StringComparer.Ordinal);
    private readonly MatchMetadata _metadata;
    private readonly IReadOnlyList<PlayerSummary> _homeLineup;
    private readonly IReadOnlyList<PlayerSummary> _awayLineup;
    private readonly KeyMomentDetector _detector;
    private readonly IReadOnlyDictionary<string, TeamProfile> _teams;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _start;
    private readonly TimeSpan _lapLength;

    /// <summary>Prepares the replay. The first lap kicks off now.</summary>
    /// <param name="events">The recorded match, from sequence 1, metadata first.</param>
    /// <param name="teams">The team profiles, for line-up names and the shape model.</param>
    /// <param name="detector">The key-moment detector.</param>
    /// <param name="options">The replay speed and the gap between laps.</param>
    /// <param name="time">The clock.</param>
    public DemoMatchSource(
        IReadOnlyList<MatchEvent> events,
        IReadOnlyDictionary<string, TeamProfile> teams,
        KeyMomentDetector detector,
        IOptions<DemoOptions> options,
        TimeProvider time)
    {
        if (events is not [{ Metadata: { } metadata }, ..] || events.Where((e, i) => e.Sequence != i + 1).Any())
        {
            throw new ArgumentException("A recording starts with its metadata event and numbers its events 1, 2, 3 and so on.", nameof(events));
        }

        _events = events;
        _metadata = metadata;
        _detector = detector;
        _teams = teams;
        _time = time;

        var speed = options.Value.Speed;
        _offsets = [.. Elapsed(events).Select(elapsed => elapsed / speed)];
        _lapLength = _offsets[^1] + options.Value.LapGap;
        _start = time.GetUtcNow();

        _states = new MatchState[events.Count];
        _momentAt = new KeyMoment?[events.Count];
        var state = MatchState.KickOff(events[0].MatchId);
        for (var i = 0; i < events.Count; i++)
        {
            if (detector.Detect(events[i], state) is { } moment)
            {
                _moments.Add((i, moment));
                _momentAt[i] = moment;
            }

            state = state.Apply(events[i]);
            _states[i] = state;
        }

        _homeLineup = Lineup(metadata.Home, teams);
        _awayLineup = Lineup(metadata.Away, teams);
    }

    /// <summary>The match ID of a lap.</summary>
    /// <param name="lap">The lap, from 1.</param>
    /// <returns>For example <c>demo-3</c>.</returns>
    public static string IdFor(int lap) => string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{lap}");

    /// <inheritdoc />
    public Task<MatchList> ListMatchesAsync(MatchStatus? status, string? season, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var current = (int)((now - _start) / _lapLength) + 1;
        IReadOnlyList<MatchSummary> matches =
        [
            .. new[] { current, current - 1 }
                .Where(lap => lap >= 1)
                .Select(lap => Find(IdFor(lap), now))
                .OfType<Lap>()
                .Select(Summary)
                .Where(match => status is null || match.Status == status)
                .Where(match => season is null || match.Season == season)
        ];
        return Task.FromResult(new MatchList(matches));
    }

    /// <inheritdoc />
    public Task<MatchDetail?> GetMatchAsync(string matchId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(matchId, _time.GetUtcNow()) is { } lap
            ? new MatchDetail(Summary(lap), _metadata.Venue, _metadata.Referee.Name, _homeLineup, _awayLineup, LiveState.From(StateOf(lap)))
            : null);

    /// <inheritdoc />
    public Task<EventPage?> GetEventsAsync(string matchId, long after, int limit, CancellationToken cancellationToken)
    {
        if (Find(matchId, _time.GetUtcNow()) is not { } lap)
        {
            return Task.FromResult<EventPage?>(null);
        }

        var from = (int)Math.Clamp(after, 0, lap.Played);
        var count = Math.Min(limit, lap.Played - from);
        List<MatchEvent> events = [.. _events.Skip(from).Take(count).Select(e => Relabel(e, matchId))];
        long? next = from + count < lap.Played ? events[^1].Sequence : null;
        return Task.FromResult<EventPage?>(new EventPage(events, next));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MomentView>?> GetMomentsAsync(string matchId, Viewer viewer, CancellationToken cancellationToken)
    {
        if (Find(matchId, _time.GetUtcNow()) is not { } lap)
        {
            return Task.FromResult<IReadOnlyList<MomentView>?>(null);
        }

        IReadOnlyList<MomentView> moments =
        [
            .. MomentsOf(lap).Select(moment => MomentViews.From(moment, matchId, Commentary(moment, matchId, viewer)))
        ];
        return Task.FromResult<IReadOnlyList<MomentView>?>(moments);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MomentumReading>?> GetMomentumAsync(string matchId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(matchId, _time.GetUtcNow()) is { } lap
            ? Momentum.Read(_events.Take(lap.Played))
            : null);

    /// <summary>The demo match is an exhibition, so no player has a season card.</summary>
    /// <param name="season">The season.</param>
    /// <param name="playerId">The player.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Always null.</returns>
    public Task<PlayerSeason?> GetPlayerSeasonAsync(string season, string playerId, CancellationToken cancellationToken) =>
        Task.FromResult<PlayerSeason?>(null);

    /// <inheritdoc />
    public async IAsyncEnumerable<LiveMessage> FollowAsync(
        string matchId,
        long? after,
        Viewer viewer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Find(matchId, _time.GetUtcNow()) is not { } lap)
        {
            yield break;
        }

        // Every follower of a lap shares one projection of it, so a late joiner doesn't replay the match.
        var projected = MessagesFor(matchId);
        var from = (int)Math.Clamp(after ?? lap.Played, 0, _events.Count);
        if (after is null)
        {
            var state = from == 0 ? MatchState.KickOff(matchId) : _states[from - 1] with { MatchId = matchId };
            yield return new LiveMessage(StreamEvents.State, LiveMessage.IdFor(from), LiveState.From(state));
        }

        // A reconnect replays what has already happened with only the latest shape in it.
        var played = Math.Min(lap.Played, _events.Count);
        var fullTimeSent = false;
        if (after is not null && from < played)
        {
            var replay = new List<LiveMessage>();
            for (var i = from; i < played; i++)
            {
                replay.AddRange(MessagesAt(i, projected, matchId, viewer));
            }

            foreach (var message in ReplayShapes.LatestOnly(replay))
            {
                fullTimeSent |= message.Event == StreamEvents.FullTime;
                yield return message;
            }

            from = played;
        }

        for (var i = from; i < _events.Count; i++)
        {
            var wait = lap.KickOff + _offsets[i] - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, cancellationToken);
            }

            foreach (var message in MessagesAt(i, projected, matchId, viewer))
            {
                fullTimeSent |= message.Event == StreamEvents.FullTime;
                yield return message;
            }
        }

        // A follower who joins at or after the final whistle still hears it, so the client
        // knows the match is over and stops reconnecting.
        if (!fullTimeSent)
        {
            var final = _states[^1] with { MatchId = matchId };
            yield return new LiveMessage(StreamEvents.FullTime, LiveMessage.IdFor(final.LastSequence), LiveState.From(final));
        }
    }

    // One event's shared messages, then its commentary and overlay. Commentary depends on the
    // viewer, so it's added per follower rather than shared.
    private IEnumerable<LiveMessage> MessagesAt(int index, IReadOnlyList<LiveMessage>[] projected, string matchId, Viewer viewer)
    {
        foreach (var message in projected[index])
        {
            yield return message;
        }

        if (_momentAt[index] is { } moment)
        {
            var id = LiveMessage.IdFor(moment.Trigger.Sequence);
            yield return new LiveMessage(StreamEvents.Commentary, id, Commentary(moment, matchId, viewer));
            yield return new LiveMessage(StreamEvents.Overlay, id, Overlay(moment, matchId));
        }
    }

    /// <summary>
    /// The state and key moments of a demo match so far, for the stand-in squad.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <returns>The state and moments, or null if there's no such match.</returns>
    internal (MatchState State, IReadOnlyList<KeyMoment> Moments)? Snapshot(string matchId) =>
        Find(matchId, _time.GetUtcNow()) is { } lap ? (StateOf(lap), [.. MomentsOf(lap)]) : null;

    /// <summary>The home and away team names, for the stand-in squad.</summary>
    internal (string Home, string Away) TeamNames => (_metadata.Home.Name, _metadata.Away.Name);

    // How far into the match each event happens, with the second half following straight on
    // from the end of the first: the demo skips the half-time break.
    private static IEnumerable<TimeSpan> Elapsed(IReadOnlyList<MatchEvent> events)
    {
        var firstHalfLength = events.Where(e => e.Time.Period == MatchPeriod.FirstHalf).Max(e => e.Time.Clock);
        var secondHalfStart = MatchTime.At(MatchPeriod.SecondHalf, 45).Clock;
        var latest = TimeSpan.Zero;
        foreach (var matchEvent in events)
        {
            var elapsed = matchEvent.Time.Period == MatchPeriod.FirstHalf
                ? matchEvent.Time.Clock
                : firstHalfLength + (matchEvent.Time.Clock - secondHalfStart);
            latest = elapsed > latest ? elapsed : latest;
            yield return latest;
        }
    }

    private static IReadOnlyList<PlayerSummary> Lineup(TeamSheet sheet, IReadOnlyDictionary<string, TeamProfile> teams) =>
        [
            .. sheet.StartingEleven.Select(id =>
                PlayerSummary.From(teams[sheet.Id].FindPlayer(id)
                    ?? throw new InvalidOperationException($"Player '{id}' isn't in {sheet.Id}'s squad."), sheet.Id))
        ];

    private static MatchEvent Relabel(MatchEvent matchEvent, string matchId) =>
        matchEvent.MatchId == matchId ? matchEvent : matchEvent with { MatchId = matchId };

    private static CommentaryLine Commentary(KeyMoment moment, string matchId, Viewer viewer) =>
        new(matchId,
            MomentIds.For(matchId, moment.Trigger.Sequence),
            moment.Trigger.Sequence,
            MomentTier.Key,
            CommentarySource.Template,
            moment.Reason,
            viewer.Mode,
            "en");

    private static OverlayCard Overlay(KeyMoment moment, string matchId) =>
        new($"{MomentIds.For(matchId, moment.Trigger.Sequence)}:overlay",
            ClockReading.From(moment.Trigger.Time),
            moment.Kind.ToString().ToLowerInvariant(),
            OverlayDurationMs,
            new OverlayPayload(Title(moment.Kind), null, moment.Trigger.Team, moment.Trigger.PlayerId));

    private static string Title(KeyMomentKind kind) => kind switch
    {
        KeyMomentKind.Goal => "GOAL",
        KeyMomentKind.BigChance => "BIG CHANCE",
        KeyMomentKind.PenaltyAwarded => "PENALTY",
        KeyMomentKind.RedCard => "RED CARD",
        KeyMomentKind.PenaltySaved => "PENALTY SAVED",
        KeyMomentKind.PenaltyMissed => "PENALTY MISSED",
        KeyMomentKind.ClearedOffLine => "OFF THE LINE",
        _ => kind.ToString().ToUpperInvariant()
    };

    // The stream messages for each event of a lap, built once for all its followers. Only the
    // two newest laps are kept: the live one and the one just finished.
    private IReadOnlyList<LiveMessage>[] MessagesFor(string matchId)
    {
        lock (_projections)
        {
            if (_projections.TryGetValue(matchId, out var cached))
            {
                return cached;
            }

            var projector = new MatchStreamProjector(matchId, _detector, _teams);
            var projected = _events.Select(e => projector.Apply(Relabel(e, matchId)).Messages).ToArray();
            _projections[matchId] = projected;

            foreach (var stale in _projections.Keys.OrderByDescending(LapNumber).Skip(2).ToList())
            {
                _projections.Remove(stale);
            }

            return projected;
        }
    }

    private static int LapNumber(string matchId) =>
        int.Parse(matchId.AsSpan(IdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture);

    private Lap? Find(string matchId, DateTimeOffset now)
    {
        if (!matchId.StartsWith(IdPrefix, StringComparison.Ordinal)
            || !int.TryParse(matchId.AsSpan(IdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number < 1
            || IdFor(number) != matchId)
        {
            return null;
        }

        var kickOff = _start + ((number - 1) * _lapLength);
        if (kickOff > now)
        {
            return null;
        }

        // The number of events whose time has come: the first event whose offset is still ahead.
        var elapsed = now - kickOff;
        var index = Array.BinarySearch(_offsets, elapsed);
        var played = index >= 0 ? LastAt(index) + 1 : ~index;
        return new Lap(matchId, kickOff, played);
    }

    // Several events can share an offset, and BinarySearch finds any one of them.
    private int LastAt(int index)
    {
        while (index + 1 < _offsets.Length && _offsets[index + 1] == _offsets[index])
        {
            index++;
        }

        return index;
    }

    private MatchState StateOf(Lap lap) => _states[lap.Played - 1] with { MatchId = lap.MatchId };

    private IEnumerable<KeyMoment> MomentsOf(Lap lap) =>
        _moments.Where(m => m.Index < lap.Played).Select(m => m.Moment);

    private MatchSummary Summary(Lap lap)
    {
        var state = StateOf(lap);
        return new MatchSummary(
            lap.MatchId,
            _metadata.Kind,
            state.FullTime ? MatchStatus.FullTime : MatchStatus.Live,
            new MatchTeam(_metadata.Home.Id, _metadata.Home.Name, _metadata.Home.ShortName, TeamViews.CrestUrl(_metadata.Home.Id)),
            new MatchTeam(_metadata.Away.Id, _metadata.Away.Name, _metadata.Away.ShortName, TeamViews.CrestUrl(_metadata.Away.Id)),
            new Score(state.Home.Goals, state.Away.Goals),
            ClockReading.From(state.Time),
            lap.KickOff,
            _metadata.Season,
            _metadata.Round,
            "synthetic");
    }

    private readonly record struct Lap(string MatchId, DateTimeOffset KickOff, int Played);
}
