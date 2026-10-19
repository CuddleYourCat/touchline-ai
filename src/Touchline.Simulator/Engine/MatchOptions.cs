using Touchline.Domain.Matches;
using Touchline.Domain.Officials;

namespace Touchline.Simulator.Engine;

/// <summary>
/// Optional settings for a match. Every one has a default, so a seed and two teams are enough
/// to play a match.
/// </summary>
public sealed record MatchOptions
{
    /// <summary>The match ID stamped on every event. Defaults to <c>{home}-{away}-{seed}</c>.</summary>
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
}
