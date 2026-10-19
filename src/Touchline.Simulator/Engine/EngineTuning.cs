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
    public const double PassSuccessShort = 0.94;
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

    // Body part (ADR-019): headers come from corners, and in open play only after a cross,
    // a completed high pass into the box (ADR-020).
    public const double CornerHeaderShare = 0.85;
    public const double CrossHeaderShare = 0.55;

    // Pass height (ADR-020), as (ground, low) shares, with the rest high. A pass into the box
    // from a wide attacking position is a cross, and mostly high. xPass includes the height,
    // because an average player completes a lofted ball less often.
    public static readonly (double Ground, double Low) ShortPassHeights = (0.85, 0.10);
    public static readonly (double Ground, double Low) LongPassHeights = (0.15, 0.30);
    public const double CrossHighShare = 0.80;
    public const double PassSuccessLowPenalty = 0.03;
    public const double PassSuccessHighPenalty = 0.08;

    // Referees (ADR-020). Each trait of 0.5 is a factor of 1. Whistle: fouls given, 0.80–1.20.
    // Strictness: cards per foul, 0.75–1.25. Advantage: of the fouls that can be played on
    // (outside the box, and outside the fouled team's own third), the share where play goes
    // on, 15–45%. That's roughly 10–30% of all fouls.
    public const double WhistleFactorMin = 0.80;
    public const double WhistleFactorRange = 0.40;
    public const double StrictnessFactorMin = 0.75;
    public const double StrictnessFactorRange = 0.5;
    public const double AdvantageShareMin = 0.15;
    public const double AdvantageShareRange = 0.30;

    // Temperament (ADR-020): the factor on both fouls and cards per foul is 1.41 at a
    // discipline of 0 and 0.59 at 1, so a hot-head collects about twice the cards.
    public const double DisciplineFactorSlope = 0.82;
    public const double MaxFoulShare = 0.9;

    // Where shots go (ADR-020). Footed shots on target are low a little over half the time.
    // A miss goes wide or over, and one in ten of those over is skied like a rugby kick.
    public const double LowShotShare = 0.55;
    public const double WideShareOfMisses = 0.55;
    public const double SkiedShareOfOverMisses = 0.10;

    // Woodwork (ADR-020): a share of misses meet the frame (mostly the posts) and rebound into
    // play, and a few goals go in off the post or the bar.
    public const double WoodworkShareOfMisses = 0.08;
    public const double PostShareOfWoodwork = 0.65;
    public const double PostAndInShareOfGoals = 0.05;
    public const double ReboundToAttackers = 0.40;

    // Penalties (ADR-020): the taker misses (more often aiming high, less often for a good
    // finisher), or the keeper saves only if they guessed the right column.
    public const double PenaltyMissLow = 0.03;
    public const double PenaltyMissHigh = 0.12;
    public const double PenaltySaveSideLow = 0.55;
    public const double PenaltySaveSideHigh = 0.20;
    public const double PenaltySaveCentreLow = 0.75;
    public const double PenaltySaveCentreHigh = 0.30;
    public const double PenaltyWoodworkShareOfMisses = 0.30;

    // How often a one-footed player uses their weaker foot in open play. Set pieces and
    // penalties are always taken on the stronger foot.
    public const double WeakFootShare = 0.22;

    // Player traits change outcomes, never xG or xPass (ADR-019). A rating of 0.5 is a
    // factor of 1: finishing runs from 0.75 to 1.25, passing from 0.92 to 1.08.
    public const double FinishingFactorMin = 0.75;
    public const double FinishingFactorRange = 0.5;
    public const double PassingFactorMin = 0.92;
    public const double PassingFactorRange = 0.16;

    // The weaker foot as a share of the stronger one. An average player's own mix of feet is
    // part of "average", so when the weaker foot is possible both feet are scaled to keep the
    // mix at 1: the stronger foot is slightly above average and the weaker one below it.
    // Without that, xG and xPass would overstate every one-footed player by about 2–4%.
    public const double WeakFootShotFactor = 0.8;
    public const double WeakFootPassFactor = 0.9;
    public const double StrongFootShotFactor = 1 / (1 - WeakFootShare * (1 - WeakFootShotFactor));
    public const double StrongFootPassFactor = 1 / (1 - WeakFootShare * (1 - WeakFootPassFactor));

    public const double MaxScoringChance = 0.97;
    public const double MaxPassCompletion = 0.99;

    // Speeds, in km/h (ADR-019).
    public static readonly (double Min, double Max) ShortPassSpeedKmh = (30, 60);
    public static readonly (double Min, double Max) LongPassSpeedKmh = (55, 90);
    public static readonly (double Min, double Max) HeaderSpeedKmh = (40, 70);
    public static readonly (double Min, double Max) PenaltySpeedKmh = (80, 110);
    public const double FootedShotSpeedMeanKmh = 92;
    public const double FootedShotSpeedSpreadKmh = 13;
    public static readonly (double Min, double Max) FootedShotSpeedLimitsKmh = (60, 125);
    public const double WeakFootSpeedFactor = 0.9;

    // A carry is run at 70–98% of the player's recorded top speed. A sprint (90% or more)
    // very rarely beats that record, by 0.1 to 0.5 km/h. The gain is at least the 0.1 km/h
    // speeds are rounded to, so a record-breaking carry never rounds back down to the record.
    public static readonly (double Min, double Max) CarrySpeedShareOfTop = (0.70, 0.98);
    public const double SprintShareOfTop = 0.90;
    public const double PersonalBestChancePerSprint = 1 / 400.0;
    public static readonly (double Min, double Max) PersonalBestGainKmh = (0.1, 0.5);

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
