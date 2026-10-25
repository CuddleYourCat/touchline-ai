using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;
using Touchline.Storage;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Starts fan matches on the full pipeline (ADR-032, ADR-050): snapshots both teams into a match
/// request, writes it to <c>match-requests</c>, then starts the simulator job with the request's
/// ID. The limits (one live, five a day, the <c>Idempotency-Key</c>, refunds) are the next slice.
/// </summary>
/// <remarks>
/// A start that fails marks the request <see cref="FanMatchStatus.Failed"/> and answers
/// <see cref="LaunchOutcome.Unavailable"/>, which the endpoint turns into a calm <c>503</c>. Logs
/// carry the request ID and the exception, never the template, the teams or a fan's names.
/// </remarks>
/// <param name="teams">The team store, to snapshot each side.</param>
/// <param name="requests">Where the request is written.</param>
/// <param name="job">Starts the simulator job.</param>
/// <param name="time">The clock, for the request's times and ID.</param>
/// <param name="options">How long a request counts as playing.</param>
/// <param name="logger">Logs a failed write or start.</param>
internal sealed partial class FanMatchLauncher(
    ITeamStore teams,
    IFanMatchRequestStore requests,
    ISimulatorJob job,
    TimeProvider time,
    IOptions<FanMatchOptions> options,
    ILogger<FanMatchLauncher> logger) : IMatchLauncher
{
    /// <summary>The plain reason a fan sees when the match can't be started.</summary>
    internal const string StartFailedReason = "The match couldn't be started just now. Please try again in a moment.";

    /// <summary>The reason stored on a request whose job didn't start.</summary>
    internal const string JobFailedReason = "The simulator job couldn't be started.";

    private const int _idAttempts = 3;

    /// <inheritdoc />
    public async Task<MatchLaunch> LaunchAsync(MatchRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var home = await teams.GetAsync(request.HomeTeamId, cancellationToken);
        var away = await teams.GetAsync(request.AwayTeamId, cancellationToken);
        if (home is null || away is null)
        {
            return Unavailable("One of the teams has just been deleted.");
        }

        FanMatchSide homeSide, awaySide;
        try
        {
            homeSide = new FanMatchSide(home.Detail.Team.Id, FanTeamSpecs.From(home.Detail), request.Home);
            awaySide = new FanMatchSide(away.Detail.Team.Id, FanTeamSpecs.From(away.Detail), request.Away);

            // The engine builds the same profiles; building them here first means a team that can't
            // be played is refused before anything is written or started.
            var catalogue = TeamProfileCatalog.LoadEmbedded();
            _ = FanMatchRequest.ProfileOf(homeSide, catalogue);
            _ = FanMatchRequest.ProfileOf(awaySide, catalogue);
        }
        catch (ArgumentException exception)
        {
            LogUnplayable(logger, exception, request.HomeTeamId, request.AwayTeamId);
            return Unavailable("One of the teams can't be played right now.");
        }

        var now = time.GetUtcNow().ToUniversalTime();
        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(int.MaxValue);
        FanMatchRequest? written = null;
        try
        {
            for (var attempt = 0; attempt < _idAttempts && written is null; attempt++)
            {
                var candidate = new FanMatchRequest(FanMatchIds.New(now), now, seed, now, homeSide, awaySide, FanMatchStatus.Scheduled);
                if (await requests.CreateAsync(candidate, cancellationToken))
                {
                    written = candidate;
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogWriteFailed(logger, exception);
            return Unavailable(StartFailedReason);
        }

        if (written is null)
        {
            return Unavailable(StartFailedReason);
        }

        try
        {
            await job.StartAsync(written.Id, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogStartFailed(logger, exception, written.Id);
            await MarkFailedAsync(written.Id);
            return Unavailable(StartFailedReason);
        }

        LogStarted(logger, written.Id);
        var streamUrl = $"/api/matches/{Uri.EscapeDataString(written.Id)}/stream";
        return new MatchLaunch(LaunchOutcome.Accepted, new MatchAccepted(written.Id, MatchStatus.Scheduled, streamUrl), null, null);
    }

    /// <inheritdoc />
    public async Task<bool> IsTeamPlayingAsync(string teamId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = options.Value;
        var candidates = await requests.ListActiveAsync(now - settings.ActiveFor, cancellationToken);
        foreach (var request in candidates.Where(r => r.Home.TeamId == teamId || r.Away.TeamId == teamId))
        {
            // A request past its pick-up deadline is marked failed here and stops counting.
            var settled = await FanMatchRequestStatus.SettleAsync(requests, request, now, settings, cancellationToken);
            if (settled.LivenessAt(now, settings.ScheduledFor, settings.ActiveFor) == FanMatchLiveness.Active)
            {
                return true;
            }
        }

        return false;
    }

    private static MatchLaunch Unavailable(string reason) => new(LaunchOutcome.Unavailable, null, null, reason);

    // The caller may have gone, and the write must still land, so it has its own short deadline.
    private async Task MarkFailedAsync(string requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await requests.SetStatusAsync(requestId, FanMatchStatus.Failed, JobFailedReason, [FanMatchStatus.Scheduled], timeout.Token);
        }
        catch (Exception exception)
        {
            LogMarkFailed(logger, exception, requestId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the simulator job for fan match {MatchId}")]
    private static partial void LogStarted(ILogger logger, string matchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The simulator job couldn't be started for fan match {MatchId}; the request is marked failed")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A fan match request couldn't be written")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fan match request {MatchId} couldn't be marked failed")]
    private static partial void LogMarkFailed(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A stored team can't be played: {HomeTeamId} against {AwayTeamId}")]
    private static partial void LogUnplayable(ILogger logger, Exception exception, string homeTeamId, string awayTeamId);
}

/// <summary>Maps the Match Centre's stored team onto the engine's fan-team spec (ADR-050).</summary>
internal static class FanTeamSpecs
{
    /// <summary>
    /// The snapshot of a team for a match request: a fan team's spec, or null for a built-in team,
    /// which the engine takes from its embedded catalogue.
    /// </summary>
    /// <param name="team">The stored team.</param>
    /// <returns>The spec, or null for a built-in team.</returns>
    /// <exception cref="ArgumentException">A fan team has no manager, or a player has no archetype.</exception>
    public static FanTeamSpec? From(TeamDetail team)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (team.Team.Origin != TeamOrigin.Fan)
        {
            return null;
        }

        var manager = team.Manager ?? throw new ArgumentException($"Fan team '{team.Team.Id}' has no manager.", nameof(team));
        return new FanTeamSpec(
            team.Team.Id,
            team.Team.Name,
            team.Team.ShortName,
            team.Team.Venue,
            team.Team.Colours,
            new FanManagerSpec(manager.Name, manager.Style),
            [
                .. team.Squad.Select(p => new FanPlayerSpec(
                    p.Id,
                    p.Name,
                    p.ShirtNumber,
                    p.PreferredFoot,
                    p.Archetype ?? throw new ArgumentException($"Player '{p.Id}' has no archetype.", nameof(team))))
            ]);
    }
}
