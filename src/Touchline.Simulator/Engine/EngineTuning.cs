namespace Touchline.Simulator.Engine;

/// <summary>
/// Every probability and duration the engine uses, in one place. Calibration (ADR-014)
/// changes these numbers, never the engine's logic. The calibration tests check that the
/// results land inside realistic ranges.
/// </summary>
internal static class EngineTuning
{
    // Pressure: how often the defending team closes down the ball before an action.
    public const double PressureBase = 0.10;
    public const double PressurePerPressingIntensity = 0.25;

    // Where pressure happens: high pressers press the opponent's build-up, and drop into a
    // deeper block near their own goal.
    public const double HighPressBase = 0.5;
    public const double HighPressPerPressingIntensity = 1.6;
    public const double DeepBlockBase = 1.1;
    public const double DeepBlockPerPressingIntensity = 0.7;

    // Shooting: chance of shooting per action, which falls off with distance to goal.
    public const double ShotMaxDistance = 35;
    public const double ShotScale = 0.42;
    public const double ShotDistanceDecay = 16;

    // Carries versus passes.
    public const double CarryShare = 0.22;

    // Passing.
    public const double LongPassBase = 0.12;
    public const double LongPassPerDirectness = 0.30;
    public const double PassSuccessShort = 0.93;
    public const double PassSuccessLongPenalty = 0.22;
    public const double PassSuccessPressurePenalty = 0.14;
    public const double PassSuccessAttackingThirdPenalty = 0.10;

    // Tackling and fouls, after a carry.
    public const double TackleBase = 0.14;
    public const double TacklePerPressingIntensity = 0.16;
    public const double TackleWonShare = 0.52;
    public const double TackleFoulShare = 0.33;
    public const double TackleUnderPressureFactor = 1.8;
    public const double FoulInOwnBoxDamping = 0.15;

    // A booked player tackles more carefully, so is far less likely to foul again.
    public const double BookedPlayerFoulFactor = 0.2;

    // Cards per foul.
    public const double YellowPerFoul = 0.19;
    public const double StraightRedPerFoul = 0.002;

    // The most players a team can have sent off. Further sending-off offences are downgraded.
    public const int MaxSentOffPerTeam = 2;

    // Direct free kick shots, from fouls within this distance of goal.
    public const double FreeKickShotDistance = 27;
    public const double FreeKickShotShare = 0.30;
    public const double FreeKickXgFactor = 0.6;

    // Shot outcomes for shots that don't go in.
    public const double SavedShareOfMisses = 0.26;
    public const double BlockedShareOfMisses = 0.25;

    // Corners, after saved or blocked shots.
    public const double CornerAfterSave = 0.30;
    public const double CornerAfterBlock = 0.40;
    public const double CornerDeliveryWon = 0.35;
    public const int MaxCornersInPossession = 2;

    // Counters: a possession won in the middle or attacking third is a counter for this
    // many actions.
    public const int CounterActions = 4;

    // Seconds each kind of moment takes, as (minimum, maximum).
    public static readonly (int Min, int Max) PassSeconds = (2, 5);
    public static readonly (int Min, int Max) CarrySeconds = (2, 4);
    public static readonly (int Min, int Max) DuelSeconds = (1, 2);
    public const int ShotSeconds = 3;
    public const int GoalStoppageSeconds = 60;
    public const int GoalKickStoppageSeconds = 22;
    public const int SaveStoppageSeconds = 10;
    public const int FoulStoppageSeconds = 22;
    public const int CardStoppageSeconds = 30;
    public const int CornerStoppageSeconds = 25;

    // Added time at the end of each half, in whole minutes, as (minimum, maximum).
    public static readonly (int Min, int Max) FirstHalfAddedMinutes = (1, 4);
    public static readonly (int Min, int Max) SecondHalfAddedMinutes = (2, 6);
}
