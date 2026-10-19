namespace Touchline.Domain.Matches;

/// <summary>How a shot ended.</summary>
public enum ShotOutcome
{
    /// <summary>The ball went in.</summary>
    Goal,

    /// <summary>On target, stopped by the goalkeeper.</summary>
    Saved,

    /// <summary>Missed the target.</summary>
    OffTarget,

    /// <summary>Blocked by an outfield player.</summary>
    Blocked
}

/// <summary>The kind of play a shot came from. Shot quality depends on it (ADR-014).</summary>
public enum ShotSituation
{
    /// <summary>Settled possession.</summary>
    OpenPlay,

    /// <summary>A fast break after winning the ball.</summary>
    Counter,

    /// <summary>A corner, free kick or throw-in routine.</summary>
    SetPiece,

    /// <summary>A penalty kick.</summary>
    Penalty
}

/// <summary>The colour of a card shown by the referee.</summary>
public enum CardColour
{
    /// <summary>A caution.</summary>
    Yellow,

    /// <summary>A sending-off.</summary>
    Red
}

/// <summary>What a shot was struck with (ADR-019).</summary>
public enum BodyPart
{
    /// <summary>The left foot.</summary>
    LeftFoot,

    /// <summary>The right foot.</summary>
    RightFoot,

    /// <summary>A header.</summary>
    Head
}

/// <summary>
/// The detail of a shot.
/// </summary>
public sealed record ShotDetail
{
    /// <summary>
    /// Creates shot detail.
    /// </summary>
    /// <param name="expectedGoals">The chance quality: the probability an average player scores from here, from 0 to 1.</param>
    /// <param name="outcome">How the shot ended.</param>
    /// <param name="situation">The kind of play it came from.</param>
    /// <param name="bodyPart">What the shot was struck with.</param>
    /// <param name="weakFoot">True when the shooter used their weaker foot. Never true for a header.</param>
    /// <param name="speedKmh">The ball's speed, in km/h.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// xG isn't between 0 and 1, the speed isn't a plausible ball speed, or an enum value isn't defined.
    /// </exception>
    /// <exception cref="ArgumentException">A header is marked as a weak-foot shot.</exception>
    public ShotDetail(double expectedGoals, ShotOutcome outcome, ShotSituation situation, BodyPart bodyPart, bool weakFoot, double speedKmh)
    {
        if (weakFoot && bodyPart == BodyPart.Head)
        {
            throw new ArgumentException("A header can't be struck with the weaker foot.", nameof(weakFoot));
        }

        ExpectedGoals = DetailRules.Probability(expectedGoals, nameof(expectedGoals), "xG");
        Outcome = DetailRules.Defined(outcome, nameof(outcome));
        Situation = DetailRules.Defined(situation, nameof(situation));
        BodyPart = DetailRules.Defined(bodyPart, nameof(bodyPart));
        WeakFoot = weakFoot;
        SpeedKmh = DetailRules.SpeedKmh(speedKmh, nameof(speedKmh));
    }

    /// <summary>
    /// The probability an average player scores this chance (xG), from 0 to 1. It includes
    /// the body part, as public xG models do, but not the shooter's foot or finishing quality.
    /// </summary>
    public double ExpectedGoals { get; }

    /// <summary>What the shot was struck with.</summary>
    public BodyPart BodyPart { get; }

    /// <summary>True when the shooter used their weaker foot.</summary>
    public bool WeakFoot { get; }

    /// <summary>The ball's speed, in km/h.</summary>
    public double SpeedKmh { get; }

    /// <summary>How the shot ended.</summary>
    public ShotOutcome Outcome { get; }

    /// <summary>The kind of play it came from.</summary>
    public ShotSituation Situation { get; }

    /// <summary>True when the shot hit the target: a goal or a save.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OnTarget => Outcome is ShotOutcome.Goal or ShotOutcome.Saved;
}
