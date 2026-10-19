using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using static Touchline.Simulator.Engine.EngineTuning;

namespace Touchline.Simulator.Engine;

public sealed partial class MatchSimulator
{
    // How a player's traits (ADR-019) turn into outcomes: feet, finishing, passing and speed.
    private sealed partial class Run
    {
        // A one-footed player strikes with their stronger foot, or sometimes the weaker one when
        // it's allowed. A two-footed player has no weaker foot, so either foot will do.
        private (BodyPart Foot, bool WeakFoot) Foot(Player player, bool allowWeakFoot)
        {
            if (player.PreferredFoot == PreferredFoot.Both)
            {
                return (_random.Next(2) == 0 ? BodyPart.LeftFoot : BodyPart.RightFoot, false);
            }

            var (strong, weak) = player.PreferredFoot == PreferredFoot.Left
                ? (BodyPart.LeftFoot, BodyPart.RightFoot)
                : (BodyPart.RightFoot, BodyPart.LeftFoot);

            return UsesWeakFoot(player, allowWeakFoot) ? (weak, true) : (strong, false);
        }

        // Only the weak-foot question, for actions that don't record which foot (passes).
        // A two-footed player costs no random draw.
        private bool UsesWeakFoot(Player player, bool allowWeakFoot) =>
            allowWeakFoot && player.PreferredFoot != PreferredFoot.Both && _random.NextDouble() < WeakFootShare;

        // When a one-footed player could have used either foot, the feet are scaled so their
        // usual mix averages 1, and xG and xPass stay true for an average player (ADR-019).
        private static double FootFactor(Player player, bool weakFootPossible, bool weakFoot, double weakFactor, double strongFactor) =>
            !weakFootPossible || player.PreferredFoot == PreferredFoot.Both ? 1
            : weakFoot ? weakFactor * strongFactor
            : strongFactor;

        private static double FinishingFactor(Player player) => FinishingFactorMin + FinishingFactorRange * player.Finishing;

        private static double PassCompletion(Player passer, double expectedCompletion, double footFactor) =>
            Math.Min(MaxPassCompletion, expectedCompletion * (PassingFactorMin + PassingFactorRange * passer.Passing) * footFactor);

        private double ShotSpeed(ShotSituation situation, BodyPart bodyPart, bool weakFoot)
        {
            var speed = bodyPart == BodyPart.Head ? Uniform(HeaderSpeedKmh)
                : situation == ShotSituation.Penalty ? Uniform(PenaltySpeedKmh)
                : Math.Clamp(Normal(FootedShotSpeedMeanKmh, FootedShotSpeedSpreadKmh), FootedShotSpeedLimitsKmh.Min, FootedShotSpeedLimitsKmh.Max);

            return Math.Round(speed * (weakFoot ? WeakFootSpeedFactor : 1), 1);
        }

        // Below the player's recorded best, except for the rare sprint that beats it. Whether
        // that's a new personal best is for the milestones detector to say (ADR-019).
        private double CarrySpeed(Player player)
        {
            var share = Uniform(CarrySpeedShareOfTop);
            var speed = share >= SprintShareOfTop && _random.NextDouble() < PersonalBestChancePerSprint
                ? player.TopSpeedKmh + Uniform(PersonalBestGainKmh)
                : player.TopSpeedKmh * share;

            return Math.Round(speed, 1);
        }
    }
}
