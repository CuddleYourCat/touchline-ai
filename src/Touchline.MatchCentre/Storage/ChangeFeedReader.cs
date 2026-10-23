using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Squad;
using Touchline.Domain.Storage;
using Touchline.MatchCentre.Live;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Polls the <c>events</c> and <c>commentary</c> change feeds (ADR-029, ADR-037) and hands each
/// changed document to its match's hub. It also evicts expired hubs.
/// </summary>
/// <remarks>
/// It polls about every second while things are changing, and backs off to
/// <see cref="MaxInterval"/> while both feeds answer 304 Not Modified. A failed poll is logged,
/// counted and retried after the same back-off: it never stops the host.
/// </remarks>
/// <param name="source">The change feeds.</param>
/// <param name="hubs">The live hubs to feed.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">Logs failed polls and unreadable documents.</param>
internal sealed partial class ChangeFeedReader(
    IChangeFeedSource source,
    LiveMatchHubs hubs,
    TimeProvider time,
    ILogger<ChangeFeedReader> logger) : BackgroundService
{
    /// <summary>The meter the reader's counters belong to.</summary>
    public const string MeterName = "Touchline.MatchCentre.ChangeFeed";

    /// <summary>The poll interval while changes are flowing.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between polls, while nothing changes or polls fail.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(8);

    private static readonly Meter _meter = new(MeterName);
    private static readonly Counter<long> _failures = _meter.CreateCounter<long>("touchline.changefeed.poll.failures", description: "Change feed polls that failed");

    private long _failedPolls;

    /// <summary>How many polls have failed since the reader started.</summary>
    public long FailedPolls => Interlocked.Read(ref _failedPolls);

    /// <summary>
    /// Reads both feeds once and dispatches what changed.
    /// </summary>
    /// <param name="cancellationToken">Stops the poll.</param>
    /// <returns>True when anything changed.</returns>
    internal async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var document in await source.ReadChangesAsync(CosmosNames.Events, cancellationToken))
        {
            changed = true;
            if (Read<MatchEvent>(document, CosmosNames.Events) is { } matchEvent)
            {
                hubs.Dispatch(matchEvent);
            }
        }

        foreach (var document in await source.ReadChangesAsync(CosmosNames.Commentary, cancellationToken))
        {
            changed = true;
            if (Read<CommentaryLine>(document, CosmosNames.Commentary) is { } line)
            {
                hubs.Dispatch(line);
            }
        }

        return changed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = Interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The first poll that succeeds takes the feeds' start point, and only then can a
                // hub load: an event written after it is in the feed, one before it in the load.
                var changed = await PollOnceAsync(stoppingToken);
                hubs.MarkFeedStarted();
                wait = changed ? Interval : Longer(wait);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _failedPolls);
                _failures.Add(1);
                LogPollFailed(logger, exception);
                wait = Longer(wait);
            }

            hubs.EvictExpired();
            try
            {
                await Task.Delay(wait, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static TimeSpan Longer(TimeSpan wait) => wait * 2 < MaxInterval ? wait * 2 : MaxInterval;

    // One unreadable document is skipped, not retried: the continuation has moved past it.
    private T? Read<T>(JsonElement document, string container)
        where T : class
    {
        try
        {
            return document.Deserialize<T>(DomainJson.Options);
        }
        catch (JsonException exception)
        {
            LogUnreadable(logger, exception, container);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A change feed poll failed; retrying")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A changed document in {Container} couldn't be read, so it was skipped")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string container);
}
