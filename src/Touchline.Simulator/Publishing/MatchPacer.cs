using Touchline.Domain.Matches;

namespace Touchline.Simulator.Publishing;

/// <summary>Events to send together, after waiting a while (ADR-023).</summary>
/// <param name="DelayBefore">How long to wait before sending them.</param>
/// <param name="Events">The events, all from the same match and the same second of the match clock.</param>
public sealed record PacedBatch(TimeSpan DelayBefore, IReadOnlyList<MatchEvent> Events);

/// <summary>
/// Turns a match's events into timed batches, so they're published at the pace the match
/// was played, sped up (ADR-023).
/// </summary>
public static class MatchPacer
{
    /// <summary>
    /// Groups events that happen at the same moment, and waits the gap on the match clock
    /// before each group, divided by the speed. The second half's clock starts at 45:00, which
    /// is earlier than the end of the first half's added time, so half-time costs no wait.
    /// </summary>
    /// <param name="events">One match's events, in sequence order. Read lazily.</param>
    /// <param name="speed">How many times faster than the match clock to publish. Must be positive.</param>
    /// <returns>The batches, in order, produced as the events are read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The speed isn't a positive number.</exception>
    public static IEnumerable<PacedBatch> Pace(IEnumerable<MatchEvent> events, double speed)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (!double.IsFinite(speed) || speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "The speed must be a positive number.");
        }

        return PaceLazily(events, speed);
    }

    // A separate iterator, so the argument checks above run when Pace is called rather than
    // when the batches are first read.
    private static IEnumerable<PacedBatch> PaceLazily(IEnumerable<MatchEvent> events, double speed)
    {
        var batch = new List<MatchEvent>();
        var batchClock = TimeSpan.Zero;
        var delay = TimeSpan.Zero;

        foreach (var matchEvent in events)
        {
            var clock = matchEvent.Time.Clock;
            if (batch.Count > 0 && clock != batchClock)
            {
                yield return new PacedBatch(delay, batch);
                var gap = clock - batchClock;
                delay = gap > TimeSpan.Zero ? gap / speed : TimeSpan.Zero;
                batch = [];
            }

            batch.Add(matchEvent);
            batchClock = clock;
        }

        if (batch.Count > 0)
        {
            yield return new PacedBatch(delay, batch);
        }
    }
}
