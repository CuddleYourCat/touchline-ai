using Microsoft.Extensions.Options;
using Touchline.Domain.Analytics;
using Touchline.Domain.Matches;
using Touchline.Domain.Officials;
using Touchline.Domain.Season;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Storage;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// A fan match that hasn't kicked off yet (ADR-050). Its first event lands 30 to 60 seconds after
/// the <c>202</c>, and until then the Cosmos DB read side has no match for its ID. This wraps that
/// read side: a fan match with no stored match is served from its request, as
/// <see cref="MatchStatus.Scheduled"/>, with the line-ups the engine will play and no events, so
/// the page and its stream can open at once. The stream needs nothing more: its hub loads an empty
/// history and the change feed hands it the events when they come.
/// </summary>
/// <remarks>
/// A request that failed, or was never picked up within <see cref="FanMatchOptions.ScheduledFor"/>,
/// throws <see cref="FanMatchNotStartedException"/>, which the API answers with a <c>410</c>. A
/// stale scheduled request is marked failed on the way.
/// </remarks>
/// <param name="inner">The Cosmos DB read side.</param>
/// <param name="requests">The match requests.</param>
/// <param name="options">The pick-up deadline.</param>
/// <param name="time">The clock.</param>
internal sealed class FanMatchReadModel(
    IMatchReadModel inner,
    IFanMatchRequestStore requests,
    IOptions<FanMatchOptions> options,
    TimeProvider time) : IMatchReadModel
{
    /// <inheritdoc />
    public Task<MatchList> ListMatchesAsync(MatchStatus? status, string? season, CancellationToken cancellationToken) =>
        inner.ListMatchesAsync(status, season, cancellationToken);

    /// <inheritdoc />
    public async Task<MatchDetail?> GetMatchAsync(string matchId, CancellationToken cancellationToken) =>
        await inner.GetMatchAsync(matchId, cancellationToken)
        ?? (await ScheduledAsync(matchId, cancellationToken) is { } request ? Detail(request) : null);

    /// <inheritdoc />
    public async Task<EventPage?> GetEventsAsync(string matchId, long after, int limit, CancellationToken cancellationToken) =>
        await inner.GetEventsAsync(matchId, after, limit, cancellationToken)
        ?? (await ScheduledAsync(matchId, cancellationToken) is null ? null : new EventPage([], null));

    /// <inheritdoc />
    public async Task<IReadOnlyList<MomentView>?> GetMomentsAsync(string matchId, Viewer viewer, CancellationToken cancellationToken) =>
        await inner.GetMomentsAsync(matchId, viewer, cancellationToken)
        ?? (await ScheduledAsync(matchId, cancellationToken) is null ? null : []);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MomentumReading>?> GetMomentumAsync(string matchId, CancellationToken cancellationToken) =>
        await inner.GetMomentumAsync(matchId, cancellationToken)
        ?? (await ScheduledAsync(matchId, cancellationToken) is null ? null : []);

    /// <inheritdoc />
    public Task<PlayerSeason?> GetPlayerSeasonAsync(string season, string playerId, CancellationToken cancellationToken) =>
        inner.GetPlayerSeasonAsync(season, playerId, cancellationToken);

    /// <summary>
    /// The request of a fan match that's waiting for its first event, or null when the ID isn't a
    /// fan match's or has no request (a plain 404).
    /// </summary>
    /// <exception cref="FanMatchNotStartedException">The match failed, or was never picked up.</exception>
    private async Task<FanMatchRequest?> ScheduledAsync(string matchId, CancellationToken cancellationToken)
    {
        if (!FanMatchIds.IsFanMatch(matchId) || await requests.GetAsync(matchId, cancellationToken) is not { } request)
        {
            return null;
        }

        request = await FanMatchRequestStatus.SettleAsync(requests, request, time.GetUtcNow(), options.Value, cancellationToken);
        return request.Status switch
        {
            FanMatchStatus.Scheduled or FanMatchStatus.Running => request,
            FanMatchStatus.Failed => throw new FanMatchNotStartedException(matchId),
            // Finished with no stored match: the processor hasn't caught up, or never will. Not found.
            _ => null,
        };
    }

    private static MatchDetail Detail(FanMatchRequest request)
    {
        var catalogue = TeamProfileCatalog.LoadEmbedded();
        var home = FanMatchRequest.ProfileOf(request.Home, catalogue);
        var away = FanMatchRequest.ProfileOf(request.Away, catalogue);
        var kickOff = MatchState.KickOff(request.Id);
        return new MatchDetail(
            new MatchSummary(
                request.Id,
                MatchKind.Exhibition,
                MatchStatus.Scheduled,
                new MatchTeam(home.Id, home.Name, home.ShortName, TeamViews.CrestUrl(home.Id)),
                new MatchTeam(away.Id, away.Name, away.ShortName, TeamViews.CrestUrl(away.Id)),
                new Score(0, 0),
                ClockReading.From(kickOff.Time),
                request.KickOffUtc,
                null,
                null,
                "synthetic"),
            home.Venue,
            // The engine's own choice when no referee is given, which uses no random draw.
            RefereeCatalog.ForSeed(request.Seed).Name,
            Lineup(home, request.Home.Sheet),
            Lineup(away, request.Away.Sheet),
            LiveState.From(kickOff),
            Bench(home, request.Home.Sheet),
            Bench(away, request.Away.Sheet));
    }

    // The same eleven, in the same order, as the engine will put in the metadata.
    private static IReadOnlyList<PlayerSummary> Lineup(TeamProfile team, MatchdaySheet? sheet) =>
        [.. StartingElevens.For(team, sheet).Select(p => PlayerSummary.From(p, team.Id))];

    // The same bench as the engine will put in the metadata (ADR-064).
    private static IReadOnlyList<PlayerSummary> Bench(TeamProfile team, MatchdaySheet? sheet) =>
        [.. StartingElevens.Bench(team, sheet).Select(p => PlayerSummary.From(p, team.Id))];
}

/// <summary>
/// Settles a fan match request's status against its deadlines (ADR-050), for every reader that
/// cares whether it still holds its teams.
/// </summary>
internal static class FanMatchRequestStatus
{
    /// <summary>The reason stored on a request whose execution never ran.</summary>
    public const string NotPickedUpReason = "The match didn't start.";

    /// <summary>
    /// The request as it stands now: a scheduled one past its pick-up deadline is marked failed
    /// (conditionally, so a late pick-up by the job wins) and returned as failed.
    /// </summary>
    /// <param name="requests">The store.</param>
    /// <param name="request">The request as read.</param>
    /// <param name="now">The time now.</param>
    /// <param name="options">The deadlines.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The request, with its status settled.</returns>
    public static async Task<FanMatchRequest> SettleAsync(
        IFanMatchRequestStore requests, FanMatchRequest request, DateTimeOffset now, FanMatchOptions options, CancellationToken cancellationToken)
    {
        if (request.LivenessAt(now, options.ScheduledFor, options.ActiveFor) != FanMatchLiveness.NotPickedUp)
        {
            return request;
        }

        if (await requests.SetStatusAsync(request.Id, FanMatchStatus.Failed, NotPickedUpReason, [FanMatchStatus.Scheduled], cancellationToken))
        {
            return request with { Status = FanMatchStatus.Failed, Reason = NotPickedUpReason, UpdatedUtc = now };
        }

        // The job moved it on in the meantime: read what it is now.
        return await requests.GetAsync(request.Id, cancellationToken) ?? request;
    }
}

/// <summary>A fan match that couldn't be started, or that its job never picked up (ADR-050). The API answers <c>410</c>.</summary>
/// <param name="matchId">The match.</param>
public sealed class FanMatchNotStartedException(string matchId) : Exception($"Fan match '{matchId}' didn't start.")
{
    /// <summary>The match.</summary>
    public string MatchId { get; } = matchId;
}
