using Touchline.Domain.Matches;

namespace Touchline.Domain.Moments;

/// <summary>The kinds of moment worth waking the agents for (ADR-004, ADR-022).</summary>
public enum KeyMomentKind
{
    /// <summary>A shot that went in.</summary>
    Goal,

    /// <summary>A shot that didn't go in, but was a good chance (high xG).</summary>
    BigChance,

    /// <summary>A foul by a defending team inside its own penalty area.</summary>
    PenaltyAwarded,

    /// <summary>A player sent off.</summary>
    RedCard,

    /// <summary>A penalty the goalkeeper stopped.</summary>
    PenaltySaved,

    /// <summary>A penalty the taker missed: wide, over, or off the woodwork.</summary>
    PenaltyMissed,

    /// <summary>A goal-bound shot stopped by a defender on or near the goal line.</summary>
    ClearedOffLine
}

/// <summary>
/// A moment that goes onto the Service Bus key-moments topic, where it wakes the agents.
/// </summary>
/// <param name="Kind">What kind of moment it is.</param>
/// <param name="Trigger">The event that caused it.</param>
/// <param name="Reason">One plain sentence saying why it matters, for logs and agent context.</param>
/// <param name="GoalLineMarginCm">
/// For a close goal-line call (ADR-022), how far the whole ball was over the line, in
/// centimetres: positive for a goal, negative for a clearance that hadn't fully crossed.
/// Null unless the call was within <see cref="KeyMomentDetector.CloseCallCm"/>.
/// </param>
public sealed record KeyMoment(KeyMomentKind Kind, MatchEvent Trigger, string Reason, int? GoalLineMarginCm = null);
