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
/// ID, within the limits: one live at a time, a few a day, and an <c>Idempotency-Key</c> that makes a repeat
/// return the same match. The order is: the key, one live, the day's slot, one live again, the request,
/// the job; a failure after the slot gives it back.
/// </summary>
/// <remarks>
/// A start that fails marks the request <see cref="FanMatchStatus.Failed"/> and answers
/// <see cref="LaunchOutcome.Unavailable"/>, which the endpoint turns into a calm <c>503</c>. Logs
/// carry the request ID and the exception, never the template, the teams or a fan's names.
/// </remarks>
/// <param name="teams">The team store, to snapshot each side.</param>
/// <param name="requests">Where the request is written.</param>
/// <param name="limits">The day counters and the claimed keys.</param>
/// <param name="job">Starts the simulator job.</param>
/// <param name="time">The clock, for the request's times and ID.</param>
/// <param name="options">The limits and how long a request counts as playing.</param>
/// <param name="logger">Logs a failed write or start.</param>
internal sealed partial class FanMatchLauncher(
    ITeamStore teams,
    IFanMatchRequestStore requests,
    IFanMatchLimitStore limits,
    ISimulatorJob job,
    TimeProvider time,
    IOptions<FanMatchOptions> options,
    ILogger<FanMatchLauncher> logger) : IMatchLauncher
{
    /// <inheritdoc />
    public bool CanLaunch => true;

    /// <summary>The plain reason a fan sees when the match can't be started.</summary>
    internal const string StartFailedReason = "The match couldn't be started just now. Please try again in a moment.";

    /// <summary>The reason stored on a request whose job didn't start.</summary>
    internal const string JobFailedReason = "The simulator job couldn't be started.";

    /// <summary>The plain reason a fan sees while another fan match is being played.</summary>
    internal const string LiveReason = "A fan match is already being played. Watch it, or try again in a few minutes.";

    // A claimed key with no request yet is a first attempt still writing it, for this long.
    private static readonly TimeSpan _inFlightFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _minimumWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(10);

    // How long a launch that has taken its slot may take to write its request and start the job.
    private static readonly TimeSpan _launchTimeout = TimeSpan.FromSeconds(30);

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
        var settings = options.Value;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(int.MaxValue);
        var id = FanMatchIds.New(now);

        // 1 and 2, under the caller's token: nothing is spent yet, so a client that goes away here
        // simply aborts, letting its key go.
        try
        {
            // 1. The idempotency key: a repeat returns the match the key already started, and spends nothing.
            if (idempotencyKey is not null
                && await limits.TryClaimKeyAsync(idempotencyKey, id, now, cancellationToken) is { } claim
                && await ReplayAsync(idempotencyKey, claim, id, now, cancellationToken) is { } replay)
            {
                return replay;
            }

            // 2. One live at a time.
            if (await LiveAsync(now, settings, cancellationToken) is { } live)
            {
                await ReleaseKeyAsync(idempotencyKey, id);
                return live;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseKeyAsync(idempotencyKey, id);
            throw;
        }
        catch (Exception exception)
        {
            LogLimitsFailed(logger, exception);
            await ReleaseKeyAsync(idempotencyKey, id);
            return Unavailable(StartFailedReason);
        }

        // From the day's slot on, the caller's token no longer applies: a launch that has started
        // spending finishes (the match starts) or cleans up (the slot is refunded, the key released
        // or left on a failed request), even if the client has gone. The work has its own deadline.
        using var work = new CancellationTokenSource(_launchTimeout);
        var token = work.Token;
        var slotTaken = false;
        try
        {
            // 3. The day's slot. Then one live again, which narrows (but can't close) the window in
            // which two launches at once both pass the first check.
            if (!await limits.TryTakeDaySlotAsync(day, settings.DailyLimit, token))
            {
                await ReleaseKeyAsync(idempotencyKey, id);
                return DayFull(now, settings.DailyLimit);
            }

            slotTaken = true;
            if (await LiveAsync(now, settings, token) is { } raced)
            {
                await RefundAsync(day, id);
                await ReleaseKeyAsync(idempotencyKey, id);
                return raced;
            }

            // 4. The request.
            var written = new FanMatchRequest(id, now, seed, now, homeSide, awaySide, FanMatchStatus.Scheduled);
            if (!await requests.CreateAsync(written, token))
            {
                throw new InvalidOperationException($"Request ID {id} is already taken.");
            }
        }
        catch (Exception exception)
        {
            LogWriteFailed(logger, exception);
            if (slotTaken)
            {
                await RefundAsync(day, id);
            }

            await ReleaseKeyAsync(idempotencyKey, id);
            return Unavailable(StartFailedReason);
        }

        // 5. The job. A start that fails gives the slot back; the key stays on the failed request, so
        // a retry with it starts afresh. A start that times out is treated as failed: if ARM did start
        // it, the job finds the request failed and refuses to play it.
        try
        {
            await job.StartAsync(id, token);
        }
        catch (Exception exception)
        {
            LogStartFailed(logger, exception, id);
            await MarkFailedAsync(id);
            await RefundAsync(day, id);
            return Unavailable(StartFailedReason);
        }

        LogStarted(logger, id);
        return Accepted(id);
    }

    /// <summary>
    /// What a repeat of a claimed key gets: the same match while its request is live, scheduled,
    /// finished or still being written; or null, having moved the key to <paramref name="id"/>, when
    /// the first attempt failed (or was abandoned before writing its request), so this one goes ahead.
    /// </summary>
    private async Task<MatchLaunch?> ReplayAsync(string key, IdempotencyClaim claim, string id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var first = await requests.GetAsync(claim.RequestId, cancellationToken);
        var inFlight = first is null && now - claim.ClaimedUtc < _inFlightFor;
        if (inFlight || first is { Status: not FanMatchStatus.Failed })
        {
            LogReplayed(logger, claim.RequestId);
            return Accepted(claim.RequestId);
        }

        if (await limits.TryReclaimKeyAsync(key, claim, id, now, cancellationToken))
        {
            return null;
        }

        // Another repeat moved the key first: that one is now the attempt in flight.
        return await limits.TryClaimKeyAsync(key, id, now, cancellationToken) is { } current ? Accepted(current.RequestId) : null;
    }

    // The newest active fan match decides the wait: its start, plus a typical match, at least 30 seconds.
    private async Task<MatchLaunch?> LiveAsync(DateTimeOffset now, FanMatchOptions settings, CancellationToken cancellationToken)
    {
        DateTimeOffset? newest = null;
        foreach (var candidate in await requests.ListActiveAsync(now - settings.ActiveFor, cancellationToken))
        {
            var settled = await FanMatchRequestStatus.SettleAsync(requests, candidate, now, settings, cancellationToken);
            if (settled.LivenessAt(now, settings.ScheduledFor, settings.ActiveFor) == FanMatchLiveness.Active
                && (newest is null || settled.CreatedUtc > newest))
            {
                newest = settled.CreatedUtc;
            }
        }

        if (newest is not { } created)
        {
            return null;
        }

        var wait = created + settings.TypicalMatchLength - now;
        return new MatchLaunch(LaunchOutcome.Busy, null, wait < _minimumWait ? _minimumWait : wait, LiveReason);
    }

    private static MatchLaunch DayFull(DateTimeOffset now, int limit)
    {
        var midnight = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).AddDays(1), TimeOnly.MinValue, TimeSpan.Zero);
        var played = limit switch { 5 => "five", 10 => "ten", _ => limit.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        return new MatchLaunch(
            LaunchOutcome.Busy, null, midnight - now, $"Today's {played} fan matches have been played. The next slot opens at midnight UTC.");
    }

    private static MatchLaunch Accepted(string id) =>
        new(LaunchOutcome.Accepted, new MatchAccepted(id, MatchStatus.Scheduled, $"/api/matches/{Uri.EscapeDataString(id)}/stream"), null, null);

    // A refund that can't be written is logged, never thrown: the fan's answer doesn't depend on it.
    private async Task RefundAsync(DateOnly day, string id)
    {
        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            await limits.ReturnDaySlotAsync(day, timeout.Token);
        }
        catch (Exception exception)
        {
            LogRefundFailed(logger, exception, id);
        }
    }

    private async Task ReleaseKeyAsync(string? key, string id)
    {
        if (key is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            await limits.ReleaseKeyAsync(key, id, timeout.Token);
        }
        catch (Exception exception)
        {
            // The claim then holds no request; after a minute a repeat takes it over (ReplayAsync).
            LogReleaseFailed(logger, exception, id);
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "A repeated Idempotency-Key returned fan match {MatchId}")]
    private static partial void LogReplayed(ILogger logger, string matchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The fan match limits couldn't be read, so no match was started")]
    private static partial void LogLimitsFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The day's slot for fan match {MatchId} couldn't be given back")]
    private static partial void LogRefundFailed(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Idempotency-Key for fan match {MatchId} couldn't be released")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string matchId);

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
