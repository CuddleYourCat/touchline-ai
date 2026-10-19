namespace Touchline.Domain.Matches;

/// <summary>
/// The detail of a carry (ADR-019). The distance isn't stored: it's the gap between the
/// event's start and end.
/// </summary>
public sealed record CarryDetail
{
    /// <summary>
    /// Creates carry detail.
    /// </summary>
    /// <param name="speedKmh">The player's speed, in km/h.</param>
    /// <exception cref="ArgumentOutOfRangeException">The speed isn't plausible.</exception>
    public CarryDetail(double speedKmh) => SpeedKmh = DetailRules.SpeedKmh(speedKmh, nameof(speedKmh));

    /// <summary>
    /// The player's speed, in km/h. Comparing it with their recorded top speed (and spotting a
    /// new personal best) is the milestones detector's job, not the event's.
    /// </summary>
    public double SpeedKmh { get; }
}
