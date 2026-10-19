using Touchline.Domain.Matches;

namespace Touchline.Tests.Unit;

internal static class TestEvents
{
    public static readonly MatchMetadata Metadata = new(
        Sheet("north-bank-fc", "North Bank FC", "NBK", "nbfc"),
        Sheet("thames-valley-united", "Thames Valley United", "TVU", "tvu"),
        "Northgate Stadium",
        new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero),
        seed: 42);

    public static TeamSheet Sheet(string id, string name, string shortName, string playerPrefix) =>
        new(id, name, shortName, Enumerable.Range(1, 11).Select(n => $"{playerPrefix}-{n:00}").ToList());

    // Adds the detail an event type must carry (ADR-019), so a test event is valid unless a
    // test deliberately breaks it. Shots and cards still get their detail from the test.
    public static MatchEvent WithRequiredDetail(this MatchEvent matchEvent) => matchEvent.Type switch
    {
        MatchEventType.Pass => matchEvent with { Pass = new PassDetail(0.85, 45, weakFoot: false) },
        MatchEventType.Carry => matchEvent with { Carry = new CarryDetail(28) },
        MatchEventType.MatchMetadata => matchEvent with { Metadata = Metadata, PlayerId = null },
        _ => matchEvent
    };
}
