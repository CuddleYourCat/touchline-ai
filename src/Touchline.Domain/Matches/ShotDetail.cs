using Touchline.Domain.Pitch;

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
    /// <param name="goalMouth">Where the shot crossed (or would have crossed) the goal line, or null for a blocked shot (ADR-020).</param>
    /// <param name="hitWoodwork">True when the shot hit a post or the bar, whether it went in or stayed out.</param>
    /// <param name="keeperDive">For a penalty, which way the goalkeeper went. Penalties only.</param>
    /// <param name="clearedOffLine">
    /// True when a defender stopped it on or near the goal line (ADR-022). Blocked shots only, and
    /// then the goal mouth is where it was stopped. It defaults to false, the right reading when
    /// it's absent, unlike a speed or a position.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// xG isn't between 0 and 1, the speed isn't a plausible ball speed, or an enum value isn't defined.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A header is marked as a weak-foot shot, or the goal mouth, woodwork or dive contradicts the outcome or situation.
    /// </exception>
    public ShotDetail(
        double expectedGoals,
        ShotOutcome outcome,
        ShotSituation situation,
        BodyPart bodyPart,
        bool weakFoot,
        double speedKmh,
        GoalMouthPosition? goalMouth,
        bool hitWoodwork,
        DiveDirection? keeperDive,
        bool clearedOffLine = false)
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

        // Each problem names the parameter at fault, so a caller logging ParamName finds the right field.
        if ((GoalMouthProblem(outcome, goalMouth, hitWoodwork, clearedOffLine) ?? DiveProblem(situation, keeperDive)) is var (problem, parameter))
        {
            throw new ArgumentException($"The shot is invalid: {problem}.", parameter);
        }

        GoalMouth = goalMouth;
        HitWoodwork = hitWoodwork;
        KeeperDive = keeperDive;
        ClearedOffLine = clearedOffLine;
    }

    /// <summary>
    /// True when a defender stopped the shot on or near the goal line (ADR-022). It's still a
    /// blocked shot, off target in data-feed terms, and the event names the defender.
    /// </summary>
    public bool ClearedOffLine { get; }

    /// <summary>Where the shot crossed (or would have crossed) the goal line, from the shooter's point of view. Null when blocked.</summary>
    public GoalMouthPosition? GoalMouth { get; }

    /// <summary>
    /// True when the shot hit a post or the bar. On a goal it went in off the woodwork, and on a
    /// miss it came back into play. Data feeds count woodwork-and-out as off target.
    /// </summary>
    public bool HitWoodwork { get; }

    /// <summary>For a penalty, which way the goalkeeper went, from the taker's point of view.</summary>
    public DiveDirection? KeeperDive { get; }

    // The goal mouth must agree with the outcome: a block never reaches the line unless it's
    // cleared off it, a goal, a save or a clearance off the line is inside the frame, and a miss
    // is off the frame unless it hit the woodwork.
    private static (string Problem, string Parameter)? GoalMouthProblem(ShotOutcome outcome, GoalMouthPosition? goalMouth, bool hitWoodwork, bool clearedOffLine) => (outcome, goalMouth, hitWoodwork) switch
    {
        (not ShotOutcome.Blocked, _, _) when clearedOffLine => ($"a {outcome} shot can't have been cleared off the line", nameof(clearedOffLine)),
        (ShotOutcome.Blocked, null, false) when clearedOffLine => ("a clearance off the line needs the goal-mouth position where it was stopped", nameof(goalMouth)),
        (ShotOutcome.Blocked, { } at, false) when clearedOffLine && !GoalFrame.IsInside(at) => ("a clearance off the line must be inside the frame", nameof(goalMouth)),
        (ShotOutcome.Blocked, not null, false) when clearedOffLine => null,
        (ShotOutcome.Blocked, not null, _) => ("a blocked shot never reached the goal line, so it has no goal-mouth position", nameof(goalMouth)),
        (ShotOutcome.Blocked, null, true) or (ShotOutcome.Saved, _, true) => ($"a {outcome} shot can't have hit the woodwork", nameof(hitWoodwork)),
        (not ShotOutcome.Blocked, null, _) => ($"a {outcome} shot needs a goal-mouth position", nameof(goalMouth)),
        (ShotOutcome.Goal or ShotOutcome.Saved, { } at, _) when !GoalFrame.IsInside(at) => ($"a {outcome} shot must be inside the frame", nameof(goalMouth)),
        (ShotOutcome.OffTarget, { } at, true) when !GoalFrame.IsOnWoodwork(at) => ("a shot that hit the woodwork must meet the frame", nameof(goalMouth)),
        (ShotOutcome.OffTarget, { } at, false) when GoalFrame.IsInside(at) || GoalFrame.IsOnWoodwork(at) => ("a miss must be outside the frame", nameof(goalMouth)),
        _ => null
    };

    // Only a penalty records the keeper's dive, and every penalty must.
    private static (string Problem, string Parameter)? DiveProblem(ShotSituation situation, DiveDirection? keeperDive) => (situation == ShotSituation.Penalty, keeperDive) switch
    {
        (true, null) => ("a penalty must record the keeper's dive", nameof(keeperDive)),
        (true, { } dive) when !Enum.IsDefined(dive) => ($"the dive {dive} isn't defined", nameof(keeperDive)),
        (false, not null) => ("only a penalty records the keeper's dive", nameof(keeperDive)),
        _ => null
    };

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
