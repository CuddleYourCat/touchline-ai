using Microsoft.Extensions.Options;
using Touchline.Domain.Matches;

namespace Touchline.Simulator.Publishing;

/// <summary>
/// The simulator worker: plays each configured match and publishes its events at match pace
/// (ADR-023), then idles until the host stops, or stops it when <see cref="SimulatorOptions.ExitWhenDone"/> is set (ADR-024).
/// </summary>
/// <param name="options">The matches to play.</param>
/// <param name="sender">Where events go.</param>
/// <param name="time">The clock used for the waits between batches.</param>
/// <param name="season">Who is suspended, read before each fixture (ADR-025).</param>
/// <param name="lifetime">Stopped once every match is played, when the worker runs as a job.</param>
/// <param name="logger">Progress and failures.</param>
public sealed partial class MatchPublisher(
    IOptions<SimulatorOptions> options,
    IMatchEventSender sender,
    TimeProvider time,
    ISeasonReader season,
    IHostApplicationLifetime lifetime,
    ILogger<MatchPublisher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        try
        {
            foreach (var plan in settings.Matches)
            {
                await PlayAsync(plan, stoppingToken);
            }

            LogAllPlayed(logger, settings.Matches.Count, settings.ExitWhenDone ? "Stopping the host." : "Idling until the host stops.");
        }
        catch (Exception exception) when (settings.ExitWhenDone && !stoppingToken.IsCancellationRequested)
        {
            // A job execution with a failed match must show as failed, not succeeded. Only the host's
            // own shutdown is exempt: an SDK timeout is also an OperationCanceledException.
            LogMatchFailed(logger, exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (settings.ExitWhenDone)
            {
                lifetime.StopApplication();
            }
        }
    }

    /// <summary>
    /// Plays one match and publishes it, waiting between batches as the pacing says.
    /// </summary>
    /// <param name="plan">The match to play.</param>
    /// <param name="cancellationToken">Stops the match part-way, for a graceful shutdown.</param>
    /// <returns>A task that completes once the whole match has been published.</returns>
    /// <exception cref="InvalidOperationException">
    /// A fixture's round has already been played, or the round before it still hasn't been projected
    /// after <see cref="ProjectionWait"/>, so its bans can't be known.
    /// </exception>
    public async Task PlayAsync(MatchPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Only a fixture counts towards the season, so only a fixture has anyone suspended.
        var banned = plan is { Kind: MatchKind.Fixture, Season: { } fixtureSeason, Round: { } round }
            ? (await ReadSeasonForAsync(fixtureSeason, round, cancellationToken)).BannedFor(round)
            : null;

        var simulator = plan.CreateSimulator(time, banned);
        LogKickOff(logger, simulator.MatchId, plan.Seed, plan.Speed);
        if (banned is { Count: > 0 })
        {
            LogSuspended(logger, simulator.MatchId, string.Join(", ", banned.Order(StringComparer.Ordinal)));
        }

        var published = 0;
        foreach (var batch in MatchPacer.Pace(simulator.Play(), plan.Speed))
        {
            if (batch.DelayBefore > TimeSpan.Zero)
            {
                await Task.Delay(batch.DelayBefore, time, cancellationToken);
            }

            await sender.SendAsync(batch.Events, cancellationToken);
            published += batch.Events.Count;
        }

        LogFullTime(logger, simulator.MatchId, published);
    }

    /// <summary>
    /// How long a fixture waits for the previous round's projection before giving up. Long enough
    /// for a whole match at the default 10x (about 9 minutes) plus its processing, so rounds can be
    /// started back to back, and well inside the job's 30-minute replica timeout.
    /// </summary>
    public static TimeSpan ProjectionWait { get; } = TimeSpan.FromMinutes(12);

    private static readonly TimeSpan _projectionPoll = TimeSpan.FromSeconds(5);

    // The season as it stands for a round: the round can't already be played (re-running one is
    // what a replay is for, ADR-020), and the round before must be projected, or its bans would be
    // missed. Projection lags the last event by a batch or two and a change feed poll, so wait a little.
    private async Task<SeasonStatus> ReadSeasonForAsync(string fixtureSeason, int round, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var status = await season.ReadAsync(fixtureSeason, cancellationToken);
            if (status.HasPlayed(round))
            {
                throw new InvalidOperationException(
                    $"Round {round} of {fixtureSeason} has already been played. Play it again as a replay, which doesn't count towards the season.");
            }

            if (status.LatestRound >= round - 1)
            {
                return status;
            }

            if (waited >= ProjectionWait)
            {
                throw new InvalidOperationException(
                    $"Round {round - 1} of {fixtureSeason} hasn't been projected after {ProjectionWait.TotalMinutes} minutes (the latest is round {status.LatestRound}), so round {round}'s bans aren't known. Play the rounds in order.");
            }

            LogWaitingForProjection(logger, fixtureSeason, round - 1);
            await Task.Delay(_projectionPoll, time, cancellationToken);
            waited += _projectionPoll;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for {Season} round {Round} to be projected before kick-off")]
    private static partial void LogWaitingForProjection(ILogger logger, string season, int round);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kick-off: publishing {MatchId} (seed {Seed}) at {Speed}x")]
    private static partial void LogKickOff(ILogger logger, string matchId, int seed, double speed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Suspended for {MatchId}: {Players}")]
    private static partial void LogSuspended(ILogger logger, string matchId, string players);

    [LoggerMessage(Level = LogLevel.Information, Message = "Full time: published {Count} events for {MatchId}")]
    private static partial void LogFullTime(ILogger logger, string matchId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "All {Count} configured matches played. {Next}")]
    private static partial void LogAllPlayed(ILogger logger, int count, string next);

    [LoggerMessage(Level = LogLevel.Error, Message = "A match failed to publish. The run is marked as failed.")]
    private static partial void LogMatchFailed(ILogger logger, Exception exception);
}
