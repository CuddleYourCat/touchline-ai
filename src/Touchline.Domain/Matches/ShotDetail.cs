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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedGoals"/> isn't a number between 0 and 1.</exception>
    public ShotDetail(double expectedGoals, ShotOutcome outcome, ShotSituation situation)
    {
        // Comparisons with NaN are always false, so range checks alone would let it through.
        if (!double.IsFinite(expectedGoals) || expectedGoals < 0 || expectedGoals > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGoals), expectedGoals, "xG must be a number between 0 and 1.");
        }

        ExpectedGoals = expectedGoals;
        Outcome = outcome;
        Situation = situation;
    }

    /// <summary>The probability an average player scores this chance (xG), from 0 to 1.</summary>
    public double ExpectedGoals { get; }

    /// <summary>How the shot ended.</summary>
    public ShotOutcome Outcome { get; }

    /// <summary>The kind of play it came from.</summary>
    public ShotSituation Situation { get; }

    /// <summary>True when the shot hit the target: a goal or a save.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OnTarget => Outcome is ShotOutcome.Goal or ShotOutcome.Saved;
}
