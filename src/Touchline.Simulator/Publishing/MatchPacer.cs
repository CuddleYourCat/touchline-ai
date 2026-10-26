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
    /// is earlier than the end of the first half's added time, so the clock gap at half time is
    /// zero or negative. The group after the first half's <c>PeriodEnd</c> waits the half-time
    /// pause instead, from <see cref="HalfTimeBreak.For"/> (ADR-066).
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
        var batchPeriod = MatchPeriod.FirstHalf;
        var delay = TimeSpan.Zero;
        var halfTimeNext = false;

        foreach (var matchEvent in events)
        {
            var clock = matchEvent.Time.Clock;
            // A new period starts a new batch even at the same clock reading: with no first-half
            // added time, the first half ends at 45:00 and the second half starts at 45:00.
            if (batch.Count > 0 && (clock != batchClock || matchEvent.Time.Period != batchPeriod))
            {
                yield return new PacedBatch(delay, batch);
                var gap = clock - batchClock;
                delay = halfTimeNext
                    ? HalfTimeBreak.For(speed)
                    : gap > TimeSpan.Zero ? gap / speed : TimeSpan.Zero;
                halfTimeNext = false;
                batch = [];
            }

            halfTimeNext |= matchEvent is { Type: MatchEventType.PeriodEnd, Time.Period: MatchPeriod.FirstHalf };
            batch.Add(matchEvent);
            batchClock = clock;
            batchPeriod = matchEvent.Time.Period;
        }

        if (batch.Count > 0)
        {
            yield return new PacedBatch(delay, batch);
        }
    }
}
