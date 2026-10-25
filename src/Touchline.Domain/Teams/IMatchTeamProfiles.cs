namespace Touchline.Domain.Teams;

/// <summary>
/// The one place that knows which team profiles name a given match's players (ADR-050). Built-in
/// teams come from the embedded catalogue; a fan match's fan teams come from the snapshot in its
/// request, so a team edited or deleted mid-match can't change the names a match uses.
/// </summary>
/// <remarks>
/// Every reader that turns a player ID into a name, position or tendency asks this per match: the
/// key and notable moment detectors, the possession indexer's roster, the agents' roster guardrail
/// and match facts, and the Match Centre's line-ups and shape model.
/// </remarks>
public interface IMatchTeamProfiles
{
    /// <summary>
    /// The profiles for one match.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The embedded catalogue plus the match's fan teams, by team ID; or null when the match needs
    /// nothing beyond the catalogue the caller already has (a built-in match, or a fan match whose
    /// request can't be read right now, which then degrades to unnamed players and template lines).
    /// </returns>
    Task<IReadOnlyDictionary<string, TeamProfile>?> ForMatchAsync(string matchId, CancellationToken cancellationToken);
}

/// <summary>
/// The <see cref="IMatchTeamProfiles"/> for a host with no match requests (tests, the demo, a
/// local run without Cosmos DB): every match uses the catalogue alone.
/// </summary>
public sealed class CatalogueOnlyMatchTeams : IMatchTeamProfiles
{
    /// <summary>The shared instance.</summary>
    public static CatalogueOnlyMatchTeams Instance { get; } = new();

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, TeamProfile>?> ForMatchAsync(string matchId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, TeamProfile>?>(null);
}
