using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Season;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Live;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Storage;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// The Match Centre's read model over what the Event Processor and the season projection store
/// in Cosmos DB (ADR-023, ADR-025, ADR-029): <c>matches</c>, <c>events</c> and
/// <c>player-seasons</c>, with commentary from <c>commentary</c>.
/// </summary>
/// <remarks>
/// Every read goes through the stream API with <c>DomainJson</c>, as the Event Processor's store
/// does, because the SDK's default serialiser doesn't know the domain's System.Text.Json options.
/// Moments are rebuilt by re-running the <see cref="KeyMomentDetector"/> over the stored events,
/// so they always agree with what the processor published. Each is given the viewer's line of
/// commentary, picked by <see cref="CommentarySelection"/>.
/// </remarks>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
/// <param name="teams">The built-in team profiles, for line-up names.</param>
/// <param name="detector">Finds the key moments in the stored events.</param>
/// <param name="commentary">The commentary the agents have written.</param>
/// <param name="logger">Logs a failed commentary read, after which the moments go out without it.</param>
public sealed partial class CosmosMatchReadModel(
    CosmosClient client,
    IReadOnlyDictionary<string, TeamProfile> teams,
    KeyMomentDetector detector,
    ICommentaryStore commentary,
    ILogger<CosmosMatchReadModel> logger) : IMatchReadModel
{
    /// <summary>Every match and event is labelled as generated (CLAUDE.md club and IP rules).</summary>
    private const string _dataSource = "synthetic";

    /// <summary>A page of events after a sequence, in order, with one extra to detect a next page.</summary>
    internal const string EventPageQuery = "SELECT TOP @take * FROM c WHERE c.sequence > @after ORDER BY c.sequence";

    /// <summary>How many finished matches keep their events in memory.</summary>
    internal const int FinishedMatchesCached = 20;

    private readonly RecentCache<IReadOnlyList<MatchEvent>> _finished = new(FinishedMatchesCached);

    private Container Matches => client.GetContainer(CosmosNames.Database, CosmosNames.Matches);

    private Container Events => client.GetContainer(CosmosNames.Database, CosmosNames.Events);

    private Container PlayerSeasons => client.GetContainer(CosmosNames.Database, CosmosNames.PlayerSeasons);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchSummary>> ListMatchesAsync(MatchStatus? status, string? season, CancellationToken cancellationToken)
    {
        // A stored match has had at least one event, so it's never scheduled.
        if (status == MatchStatus.Scheduled)
        {
            return [];
        }

        var filters = new List<string> { "IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata)" };
        if (status is not null)
        {
            filters.Add("c.state.fullTime = @fullTime");
        }

        if (season is not null)
        {
            filters.Add("c.metadata.season = @season");
        }

        var query = new QueryDefinition($"SELECT * FROM c WHERE {string.Join(" AND ", filters)}");
        if (status is { } fullTimeWanted)
        {
            query = query.WithParameter("@fullTime", fullTimeWanted == MatchStatus.FullTime);
        }

        if (season is not null)
        {
            query = query.WithParameter("@season", season);
        }

        // The one query that fans out across partitions. The list is small (a season is a few
        // dozen matches), so it's sorted here rather than with a cross-partition ORDER BY.
        var stored = await CosmosQueries.ReadAllAsync<StoredMatch>(Matches, query, partition: null, cancellationToken);
        return
        [
            .. stored
                .Where(match => match.Metadata is not null)
                .Select(match => Summary(match, match.Metadata!))
                .OrderByDescending(summary => summary.KickOffUtc)
        ];
    }

    /// <inheritdoc />
    public async Task<MatchDetail?> GetMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        if (await ReadMatchAsync(matchId, cancellationToken) is not { Metadata: { } metadata } match)
        {
            return null;
        }

        return new MatchDetail(
            Summary(match, metadata),
            metadata.Venue,
            metadata.Referee.Name,
            Lineup(metadata.Home),
            Lineup(metadata.Away),
            LiveState.From(match.State));
    }

    /// <inheritdoc />
    public async Task<EventPage?> GetEventsAsync(string matchId, long after, int limit, CancellationToken cancellationToken)
    {
        if (await ReadMatchAsync(matchId, cancellationToken) is null)
        {
            return null;
        }

        // One more than asked for, to know whether another page follows without a second query.
        // A single-partition ORDER BY comes back in order, so the results are used as they are.
        var query = new QueryDefinition(EventPageQuery)
            .WithParameter("@take", limit + 1)
            .WithParameter("@after", after);
        var events = await CosmosQueries.ReadAllAsync<MatchEvent>(Events, query, new PartitionKey(matchId), cancellationToken);

        List<MatchEvent> page = [.. events.Take(limit)];
        long? nextAfter = events.Count > limit ? page[^1].Sequence : null;
        return new EventPage(page, nextAfter);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MomentView>?> GetMomentsAsync(string matchId, Viewer viewer, CancellationToken cancellationToken)
    {
        if (await ReadEventsAsync(matchId, cancellationToken) is not { } events)
        {
            return null;
        }

        // Commentary changes while a match is live (a model line replaces a template line), so
        // it's read fresh every time, even for a finished match: one single-partition query.
        var lines = (await ReadCommentaryAsync(matchId, cancellationToken)).ToLookup(line => line.MomentId);

        var moments = new List<MomentView>();
        var state = MatchState.KickOff(matchId);
        foreach (var matchEvent in events)
        {
            // The processor skips sequences it lost (MatchEventProcessor), so the stored events
            // can have gaps. Skipping them here too replays the match as the processor saw it.
            for (var missing = state.LastSequence + 1; missing < matchEvent.Sequence; missing++)
            {
                state = state.Skip(missing);
            }

            if (detector.Detect(matchEvent, state) is { } moment)
            {
                var line = CommentarySelection.Pick(lines[MomentIds.For(matchId, matchEvent.Sequence)], viewer.Mode, viewer.Language);
                moments.Add(MomentViews.From(moment, matchId, line));
            }

            state = state.Apply(matchEvent);
        }

        return moments;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MomentumReading>?> GetMomentumAsync(string matchId, CancellationToken cancellationToken) =>
        await ReadEventsAsync(matchId, cancellationToken) is { } events ? Momentum.Read(events) : null;

    /// <inheritdoc />
    public Task<PlayerSeason?> GetPlayerSeasonAsync(string season, string playerId, CancellationToken cancellationToken)
    {
        var id = $"{season}:{playerId}";
        return CosmosIds.IsValid(id)
            ? CosmosQueries.ReadOneAsync<PlayerSeason>(PlayerSeasons, id, new PartitionKey(season), cancellationToken)
            : Task.FromResult<PlayerSeason?>(null);
    }

    // Commentary is an extra on top of the moments, so a failed read degrades to moments with no
    // line instead of failing the whole request. Cancellation still propagates.
    private async Task<IReadOnlyList<CommentaryLine>> ReadCommentaryAsync(string matchId, CancellationToken cancellationToken)
    {
        try
        {
            return await commentary.GetAsync(matchId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCommentaryUnavailable(logger, exception, matchId);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commentary for {MatchId} couldn't be read, so its moments are served without it")]
    private static partial void LogCommentaryUnavailable(ILogger logger, Exception exception, string matchId);

    // An ID Cosmos DB would refuse can't name a stored match, so it reads as "no such match"
    // rather than failing as a server error.
    private Task<StoredMatch?> ReadMatchAsync(string matchId, CancellationToken cancellationToken) =>
        CosmosIds.IsValid(matchId)
            ? CosmosQueries.ReadOneAsync<StoredMatch>(Matches, matchId, new PartitionKey(matchId), cancellationToken)
            : Task.FromResult<StoredMatch?>(null);

    private async Task<IReadOnlyList<MatchEvent>?> ReadEventsAsync(string matchId, CancellationToken cancellationToken)
    {
        if (_finished.TryGet(matchId, out var cached))
        {
            return cached;
        }

        if (await ReadMatchAsync(matchId, cancellationToken) is not { } match)
        {
            return null;
        }

        var stored = await CosmosQueries.ReadAllAsync<MatchEvent>(
            Events, new QueryDefinition("SELECT * FROM c ORDER BY c.sequence"), new PartitionKey(matchId), cancellationToken);
        IReadOnlyList<MatchEvent> events = [.. stored];

        // A finished match never changes, so its events are kept. A live match isn't cached:
        // slice 3's per-match hub will serve those from one shared read.
        if (match.State.FullTime)
        {
            _finished.Add(matchId, events);
        }

        return events;
    }

    private static MatchSummary Summary(StoredMatch match, MatchMetadata metadata) =>
        new(match.MatchId,
            metadata.Kind,
            match.State.FullTime ? MatchStatus.FullTime : MatchStatus.Live,
            new MatchTeam(metadata.Home.Id, metadata.Home.Name, metadata.Home.ShortName, TeamViews.CrestUrl(metadata.Home.Id)),
            new MatchTeam(metadata.Away.Id, metadata.Away.Name, metadata.Away.ShortName, TeamViews.CrestUrl(metadata.Away.Id)),
            new Score(match.State.Home.Goals, match.State.Away.Goals),
            ClockReading.From(match.State.Time),
            metadata.KickOffUtc,
            metadata.Season,
            metadata.Round,
            _dataSource);

    private IReadOnlyList<PlayerSummary> Lineup(TeamSheet sheet)
    {
        var profile = teams.GetValueOrDefault(sheet.Id);
        return
        [
            .. sheet.StartingEleven.Select((id, index) =>
                profile?.FindPlayer(id) is { } player
                    ? PlayerSummary.From(player, sheet.Id)
                    : Unknown(id, index, sheet.Id))
        ];
    }

    // A fan team isn't in the built-in catalogue until the team-builder lane stores them, so its
    // players are shown by ID, numbered in team-sheet order, with the first as the goalkeeper.
    private static PlayerSummary Unknown(string playerId, int index, string teamId) =>
        new(playerId, playerId, index + 1, index == 0 ? PlayerPosition.Goalkeeper : PlayerPosition.Midfielder, teamId, null, PreferredFoot.Right);

    /// <summary>
    /// A <c>matches</c> document as the Event Processor writes it (its <c>MatchRecord</c>).
    /// </summary>
    /// <param name="Id">The document ID, which is the match ID.</param>
    /// <param name="MatchId">The match, and the partition key.</param>
    /// <param name="Metadata">Who, where and when, or null if the processor hasn't seen it yet.</param>
    /// <param name="State">The live state after the last stored batch.</param>
    internal sealed record StoredMatch(string Id, string MatchId, MatchMetadata? Metadata, MatchState State);
}
