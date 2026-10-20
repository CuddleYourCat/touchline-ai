using Microsoft.Extensions.Options;

namespace Touchline.Simulator.Publishing;

/// <summary>
/// The simulator worker: plays each configured match and publishes its events at match pace
/// (ADR-023), then idles until the host stops, or stops it when <see cref="SimulatorOptions.ExitWhenDone"/> is set (ADR-024).
/// </summary>
/// <param name="options">The matches to play.</param>
/// <param name="sender">Where events go.</param>
/// <param name="time">The clock used for the waits between batches.</param>
/// <param name="lifetime">Stopped once every match is played, when the worker runs as a job.</param>
/// <param name="logger">Progress and failures.</param>
public sealed partial class MatchPublisher(
    IOptions<SimulatorOptions> options,
    IMatchEventSender sender,
    TimeProvider time,
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
    public async Task PlayAsync(MatchPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var simulator = plan.CreateSimulator();
        LogKickOff(logger, simulator.MatchId, plan.Seed, plan.Speed);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Kick-off: publishing {MatchId} (seed {Seed}) at {Speed}x")]
    private static partial void LogKickOff(ILogger logger, string matchId, int seed, double speed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Full time: published {Count} events for {MatchId}")]
    private static partial void LogFullTime(ILogger logger, string matchId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "All {Count} configured matches played. {Next}")]
    private static partial void LogAllPlayed(ILogger logger, int count, string next);

    [LoggerMessage(Level = LogLevel.Error, Message = "A match failed to publish. The run is marked as failed.")]
    private static partial void LogMatchFailed(ILogger logger, Exception exception);
}
