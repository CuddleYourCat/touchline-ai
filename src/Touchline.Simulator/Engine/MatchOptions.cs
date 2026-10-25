using Touchline.Domain.Matches;
using Touchline.Domain.Officials;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Simulator.Engine;

/// <summary>
/// Optional settings for a match. Every one has a default, so a seed and two teams are enough
/// to play a match.
/// </summary>
public sealed record MatchOptions
{
    /// <summary>
    /// The match ID stamped on every event. Defaults to <c>{home}-{away}-{seed}</c>, and for a fixture
    /// <c>{home}-{away}-{season}-r{round}-{seed}</c>, so an exhibition never shares a fixture's ID.
    /// </summary>
    public string? MatchId { get; init; }

    /// <summary>The kick-off time in the metadata. Defaults to <see cref="MatchSimulator.DefaultKickOffUtc"/>.</summary>
    public DateTimeOffset? KickOffUtc { get; init; }

    /// <summary>Who referees. Defaults to <see cref="RefereeCatalog.ForSeed"/>, which uses no random draw.</summary>
    public RefereeProfile? Referee { get; init; }

    /// <summary>Whether the match counts towards the season. Defaults to <see cref="MatchKind.Exhibition"/>, which never does.</summary>
    public MatchKind Kind { get; init; } = MatchKind.Exhibition;

    /// <summary>The season, for example "2026-27". Required for a fixture.</summary>
    public string? Season { get; init; }

    /// <summary>The round of the season, from 1. Required for a fixture.</summary>
    public int? Round { get; init; }

    /// <summary>For a replay, the match ID of the fixture it re-runs.</summary>
    public string? ReplayOf { get; init; }

    /// <summary>
    /// Players who can't play: suspended for this round (ADR-025). They're left out of the squad
    /// before the starting eleven is picked. Empty by default.
    /// </summary>
    public IReadOnlySet<string> Unavailable { get; init; } = new HashSet<string>();

    /// <summary>
    /// The home team's chosen eleven and bench (ADR-032, ADR-050), or null for the engine's
    /// default 1-4-3-3 pick. The engine plays the eleven; the bench is stored but not yet played.
    /// The sheet must pass <see cref="MatchdaySheetRules"/> against the home squad and
    /// <see cref="Unavailable"/>.
    /// </summary>
    public MatchdaySheet? HomeSheet { get; init; }

    /// <summary>The away team's chosen eleven and bench, or null for the default pick. See <see cref="HomeSheet"/>.</summary>
    public MatchdaySheet? AwaySheet { get; init; }
}
