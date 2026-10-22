using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Domain.Analytics;

/// <summary>
/// How one window of a match tilted: which side was more dangerous (skill shot 2, match dynamics).
/// </summary>
/// <param name="Period">The half the window is in.</param>
/// <param name="FromMinute">The window's first minute on the match clock, inclusive (for example 45).</param>
/// <param name="ToMinute">The window's last minute, exclusive (for example 50).</param>
/// <param name="Value">
/// From -1 (only the away side threatened) through 0 (even, or no threat at all) to +1 (only the
/// home side threatened).
/// </param>
public sealed record MomentumReading(MatchPeriod Period, int FromMinute, int ToMinute, double Value);

/// <summary>
/// The momentum meter: reads a match's events as a series of five-minute windows, each scored by
/// the threat both sides created in it.
/// </summary>
public static class Momentum
{
    /// <summary>How long each window is, in match-clock minutes.</summary>
    public const int WindowMinutes = 5;

    // The three threat values are tuning, so they're static readonly rather than const: a const's
    // value is copied into every assembly that uses it, and retuning would need them all rebuilt.

    /// <summary>The threat every shot carries before its quality (its xG) is counted.</summary>
    public static readonly double ShotBaseThreat = 1;

    /// <summary>The threat a shot adds for each 1.0 of its xG, so a 0.2 xG chance adds 0.6.</summary>
    public static readonly double ThreatPerExpectedGoal = 3;

    /// <summary>The threat of a completed pass or carry that ends in the team's own attacking third.</summary>
    public static readonly double AttackingThirdThreat = 0.25;

    /// <summary>
    /// How much danger one event carries for the team that made it.
    /// </summary>
    /// <remarks>
    /// A shot is worth <see cref="ShotBaseThreat"/> plus <see cref="ThreatPerExpectedGoal"/> times
    /// its xG. A completed pass or carry that ends in the team's own attacking third is worth
    /// <see cref="AttackingThirdThreat"/>. Everything else is worth nothing.
    /// </remarks>
    /// <param name="matchEvent">The event.</param>
    /// <returns>The threat, 0 or more.</returns>
    public static double ThreatOf(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        var inAttackingThird = matchEvent.End is { } end && PitchZone.Locate(end, matchEvent.Team).Third == Third.Attacking;

        return matchEvent switch
        {
            { Type: MatchEventType.Shot, Shot: { } shot } => ShotBaseThreat + (ThreatPerExpectedGoal * shot.ExpectedGoals),
            { Type: MatchEventType.Pass or MatchEventType.Carry, Successful: true } when inAttackingThird => AttackingThirdThreat,
            _ => 0
        };
    }

    /// <summary>
    /// Reads a match's momentum, window by window.
    /// </summary>
    /// <remarks>
    /// Windows follow the match clock in each half: 0–5, 5–10 and so on. Added time is its own
    /// window (a first half that runs to 47:30 gets a 45–50 window). A window appears only if at
    /// least one event happened in it, and the readings come in match order: the first half, then
    /// the second.
    /// </remarks>
    /// <param name="events">The match's events, in any order.</param>
    /// <returns>One reading per window that had events.</returns>
    public static IReadOnlyList<MomentumReading> Read(IEnumerable<MatchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return
        [
            .. events
                .GroupBy(matchEvent =>
                {
                    var minute = (int)matchEvent.Time.Clock.TotalMinutes;
                    var fromMinute = minute / WindowMinutes * WindowMinutes;
                    return (matchEvent.Time.Period, FromMinute: fromMinute);
                })
                .OrderBy(window => window.Key.Period)
                .ThenBy(window => window.Key.FromMinute)
                .Select(window =>
                {
                    var homeThreat = 0d;
                    var awayThreat = 0d;

                    foreach (var matchEvent in window)
                    {
                        var threat = ThreatOf(matchEvent);
                        if (matchEvent.Team == TeamSide.Home)
                        {
                            homeThreat += threat;
                        }
                        else
                        {
                            awayThreat += threat;
                        }
                    }

                    var totalThreat = homeThreat + awayThreat;
                    var value = totalThreat == 0 ? 0 : (homeThreat - awayThreat) / totalThreat;
                    return new MomentumReading(window.Key.Period, window.Key.FromMinute,
                        window.Key.FromMinute + WindowMinutes, value);
                })
        ];
    }
}
