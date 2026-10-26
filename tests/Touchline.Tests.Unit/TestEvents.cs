using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Tests.Unit;

internal static class TestEvents
{
    public static readonly RefereeSheet Referee = new("ref-calloway", "Ruth Calloway");

    public static readonly MatchMetadata Metadata = new(
        Sheet("north-bank-fc", "North Bank FC", "NBK", "nbfc"),
        Sheet("thames-valley-united", "Thames Valley United", "TVU", "tvu"),
        "Northgate Stadium",
        new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero),
        seed: 42,
        Referee,
        MatchKind.Exhibition);

    public static TeamSheet Sheet(string id, string name, string shortName, string playerPrefix) =>
        new(id, name, shortName, Enumerable.Range(1, 11).Select(n => $"{playerPrefix}-{n:00}").ToList());

    /// <summary>
    /// Shot detail whose goal mouth agrees with its outcome (ADR-020): a goal or save inside
    /// the frame, a miss wide, a block without one, and a keeper's dive on a penalty.
    /// </summary>
    public static ShotDetail Shot(
        double xg,
        ShotOutcome outcome,
        ShotSituation situation = ShotSituation.OpenPlay,
        BodyPart bodyPart = BodyPart.RightFoot,
        bool weakFoot = false,
        double speedKmh = 90)
    {
        GoalMouthPosition? goalMouth = outcome switch
        {
            ShotOutcome.Blocked => null,
            ShotOutcome.OffTarget => new GoalMouthPosition(5, 0.5),
            _ => new GoalMouthPosition(1.5, 0.4)
        };
        DiveDirection? dive = situation == ShotSituation.Penalty ? DiveDirection.Left : null;

        return new ShotDetail(xg, outcome, situation, bodyPart, weakFoot, speedKmh, goalMouth, hitWoodwork: false, dive);
    }

    /// <summary>For a goal, an end in the back of the net (ADR-022 requires one), otherwise null.</summary>
    public static PitchPosition? EndFor(ShotOutcome outcome, TeamSide shooter) =>
        outcome != ShotOutcome.Goal ? null : shooter == TeamSide.Home ? new PitchPosition(106, 34) : new PitchPosition(-1, 34);

    /// <summary>An opposing defender for a blocked shot (ADR-022 requires one), otherwise null.</summary>
    public static string? Blocker(ShotOutcome outcome, TeamSide shooter) =>
        outcome != ShotOutcome.Blocked ? null : shooter == TeamSide.Home ? "tvu-05" : "nbfc-04";

    // Adds the detail an event type must carry (ADR-019, ADR-020), so a test event is valid
    // unless a test deliberately breaks it. Shots and card colours still come from the test,
    // and a card names the event before it as its foul.
    public static MatchEvent WithRequiredDetail(this MatchEvent matchEvent) => matchEvent.Type switch
    {
        MatchEventType.Pass => matchEvent with { Pass = new PassDetail(0.85, 45, weakFoot: false, PassHeight.Ground) },
        MatchEventType.Carry => matchEvent with { Carry = new CarryDetail(28) },
        MatchEventType.MatchMetadata => matchEvent with { Metadata = Metadata, PlayerId = null },
        MatchEventType.Card when matchEvent.Sequence > 1 => matchEvent with { FoulSequence = matchEvent.Sequence - 1 },
        MatchEventType.Substitution => matchEvent with { ReplacementPlayerId = matchEvent.Team == TeamSide.Home ? "nbfc-14" : "tvu-18" },
        _ => matchEvent
    };
}
