using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Simulator.Engine;

/// <summary>
/// Turns a shot's position and situation into an xG value: the probability that an average
/// player scores it (ADR-014).
/// </summary>
/// <remarks>
/// Distance follows a logistic curve fitted to three reference points: 6 m gives about
/// 0.50, 11 m about 0.30 and 20 m about 0.08. Shots from wide angles see less of the goal,
/// so the value is scaled by the cosine of the angle from the goal's centre line. Counters
/// are slightly better chances (an unsettled defence), set pieces slightly worse (a
/// crowded box), and headers are 0.6 of the footed value (ADR-019). The shooter's foot and
/// finishing never enter it: they change the result, not the expectation.
/// </remarks>
public static class ExpectedGoalsModel
{
    /// <summary>The xG of a penalty kick.</summary>
    public const double PenaltyXg = 0.76;

    /// <summary>A header's xG as a share of the same chance on the foot, as in public xG models.</summary>
    public const double HeaderFactor = 0.6;

    private const double _slope = 0.174;
    private const double _offset = 1.05;
    private const double _counterFactor = 1.25;
    private const double _setPieceFactor = 0.75;
    private const double _minimumAngleFactor = 0.15;

    /// <summary>
    /// Calculates the xG of a shot.
    /// </summary>
    /// <param name="shotAt">Where the shot was taken.</param>
    /// <param name="shooter">The shooting team, which decides which goal it's aimed at.</param>
    /// <param name="situation">The kind of play it came from.</param>
    /// <param name="bodyPart">What it was struck with. Which foot doesn't matter: xG is for an average player (ADR-019).</param>
    /// <returns>xG between 0.01 and 0.95, or <see cref="PenaltyXg"/> for a penalty.</returns>
    public static double Calculate(PitchPosition shotAt, TeamSide shooter, ShotSituation situation, BodyPart bodyPart)
    {
        if (situation == ShotSituation.Penalty)
        {
            return PenaltyXg;
        }

        var goal = PitchDimensions.AttackingGoal(shooter);
        var depth = Math.Abs(goal.X - shotAt.X);
        var distance = shotAt.DistanceTo(goal);

        var byDistance = 1 / (1 + Math.Exp(_slope * distance - _offset));
        var angleFactor = distance == 0 ? 1 : Math.Max(_minimumAngleFactor, depth / distance);
        var situationFactor = situation switch
        {
            ShotSituation.Counter => _counterFactor,
            ShotSituation.SetPiece => _setPieceFactor,
            _ => 1.0
        };

        var bodyPartFactor = bodyPart == BodyPart.Head ? HeaderFactor : 1.0;

        return Math.Clamp(byDistance * angleFactor * situationFactor * bodyPartFactor, 0.01, 0.95);
    }
}
