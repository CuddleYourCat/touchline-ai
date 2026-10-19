namespace Touchline.Domain.Matches;

/// <summary>The detail of a pass (ADR-019).</summary>
public sealed record PassDetail
{
    /// <summary>
    /// Creates pass detail.
    /// </summary>
    /// <param name="expectedCompletion">xPass: the probability an average player completes this pass, from 0 to 1.</param>
    /// <param name="speedKmh">The ball's speed, in km/h.</param>
    /// <param name="weakFoot">True when the passer used their weaker foot.</param>
    /// <exception cref="ArgumentOutOfRangeException">xPass isn't between 0 and 1, or the speed isn't a plausible ball speed.</exception>
    public PassDetail(double expectedCompletion, double speedKmh, bool weakFoot)
    {
        ExpectedCompletion = DetailRules.Probability(expectedCompletion, nameof(expectedCompletion), "xPass");
        SpeedKmh = DetailRules.SpeedKmh(speedKmh, nameof(speedKmh));
        WeakFoot = weakFoot;
    }

    /// <summary>
    /// xPass: the probability an average player completes this pass, from distance, pressure,
    /// the target's third and whether it's a long ball. The passer's own quality changes the
    /// result, not this number.
    /// </summary>
    public double ExpectedCompletion { get; }

    /// <summary>The ball's speed, in km/h.</summary>
    public double SpeedKmh { get; }

    /// <summary>True when the passer used their weaker foot.</summary>
    public bool WeakFoot { get; }
}
