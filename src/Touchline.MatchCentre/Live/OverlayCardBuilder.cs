using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Live;

/// <summary>
/// Builds the neutral overlay card for a moment (ADR-047). Pure, so the demo replay and the
/// Cosmos live path produce the same cards. Viewer shaping (a plain title, a metric subtitle,
/// minor cards for the Analyst) is applied later, per subscriber, by <see cref="OverlayShaping"/>.
/// </summary>
internal static class OverlayCardBuilder
{
    /// <summary>How long a key moment's card shows for.</summary>
    public const int KeyDurationMs = 6000;

    /// <summary>How long a notable moment's card shows for.</summary>
    public const int NotableDurationMs = 3000;

    /// <summary>How long a minor moment's card shows for.</summary>
    public const int MinorDurationMs = 2000;

    /// <summary>The priority of a key moment's card.</summary>
    public const string HighPriority = "high";

    /// <summary>The priority of a notable moment's card.</summary>
    public const string NormalPriority = "normal";

    /// <summary>The priority of a minor moment's card.</summary>
    public const string LowPriority = "low";

    /// <summary>The card for a key moment.</summary>
    /// <param name="moment">The moment.</param>
    /// <param name="matchId">The match, which may differ from the trigger's (a demo lap).</param>
    /// <returns>The card.</returns>
    public static OverlayCard For(KeyMoment moment, string matchId) =>
        new(IdFor(matchId, moment.Trigger.Sequence),
            ClockReading.From(moment.Trigger.Time),
            moment.Kind.ToString().ToLowerInvariant(),
            KeyDurationMs,
            new OverlayPayload(Title(moment.Kind), null, moment.Side, moment.FeaturedPlayerId),
            HighPriority);

    /// <summary>The card for a notable moment.</summary>
    /// <param name="moment">The moment.</param>
    /// <param name="matchId">The match, which may differ from the trigger's (a demo lap).</param>
    /// <returns>The card.</returns>
    public static OverlayCard For(NotableMoment moment, string matchId) =>
        new(IdFor(matchId, moment.Trigger.Sequence),
            ClockReading.From(moment.Trigger.Time),
            moment.Kind.ToString().ToLowerInvariant(),
            moment.IsMinor ? MinorDurationMs : NotableDurationMs,
            new OverlayPayload(Title(moment.Kind), null, moment.Team, moment.FeaturedPlayerId),
            moment.IsMinor ? LowPriority : NormalPriority);

    // Moment-based, so a card replayed after a reconnect has the ID the client already saw.
    private static string IdFor(string matchId, long sequence) => $"{MomentIds.For(matchId, sequence)}:overlay";

    private static string Title(KeyMomentKind kind) => kind switch
    {
        KeyMomentKind.Goal => "GOAL",
        KeyMomentKind.BigChance => "BIG CHANCE",
        KeyMomentKind.PenaltyAwarded => "PENALTY",
        KeyMomentKind.RedCard => "RED CARD",
        KeyMomentKind.PenaltySaved => "PENALTY SAVED",
        KeyMomentKind.PenaltyMissed => "PENALTY MISSED",
        KeyMomentKind.ClearedOffLine => "OFF THE LINE",
        _ => kind.ToString().ToUpperInvariant()
    };

    private static string Title(NotableMomentKind kind) => kind switch
    {
        NotableMomentKind.YellowCard => "YELLOW CARD",
        NotableMomentKind.Corner => "CORNER",
        NotableMomentKind.Woodwork => "WOODWORK",
        NotableMomentKind.ThrowIn => "THROW-IN",
        NotableMomentKind.GoalKick => "GOAL KICK",
        NotableMomentKind.Substitution => "SUBSTITUTION",
        _ => kind.ToString().ToUpperInvariant()
    };
}
