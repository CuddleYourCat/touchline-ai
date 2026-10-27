namespace Touchline.MatchCentre.Api;

/// <summary>
/// A team as stored: what the API shows, and the hash of its edit key.
/// </summary>
/// <param name="Detail">The team.</param>
/// <param name="EditKeyHash">The SHA-256 hash of the edit key, or null for a read-only built-in team.</param>
public sealed record StoredTeam(TeamDetail Detail, string? EditKeyHash);

/// <summary>
/// Where teams live (ADR-031). The demo keeps them in memory; the team-builder lane stores them
/// in the Cosmos DB <c>teams</c> container, with the built-in profiles as seed data.
/// </summary>
public interface ITeamStore
{
    /// <summary>Reads one team.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The team, or null if there's no such team (or it's been deleted).</returns>
    Task<StoredTeam?> GetAsync(string teamId, CancellationToken cancellationToken);

    /// <summary>Lists teams, built-in first, then by name.</summary>
    /// <param name="origin">Only teams of this origin, or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The teams.</returns>
    Task<IReadOnlyList<StoredTeam>> ListAsync(TeamOrigin? origin, CancellationToken cancellationToken);

    /// <summary>Whether a player exists in any team.</summary>
    /// <param name="playerId">The player.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True if the player exists.</returns>
    Task<bool> PlayerExistsAsync(string playerId, CancellationToken cancellationToken);

    /// <summary>Adds a new team.</summary>
    /// <param name="team">The team, with its ID already assigned.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the team is stored.</returns>
    /// <exception cref="TeamAlreadyExistsException">A team with that ID is already stored.</exception>
    Task AddAsync(StoredTeam team, CancellationToken cancellationToken);

    /// <summary>Replaces a team, if it's still at the version the caller read.</summary>
    /// <param name="team">The new version, whose <see cref="TeamDetail.Version"/> is one more than <paramref name="expectedVersion"/>.</param>
    /// <param name="expectedVersion">The version being replaced.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>False if the stored team has moved on since (an optimistic-concurrency conflict).</returns>
    Task<bool> ReplaceAsync(StoredTeam team, int expectedVersion, CancellationToken cancellationToken);

    /// <summary>Deletes a team.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the team is deleted.</returns>
    Task DeleteAsync(string teamId, CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether names may be used (ADR-031). The demo applies the format rule only; the
/// team-builder lane adds Azure AI Content Safety with its blocklist of real names.
/// </summary>
public interface INameGuard
{
    /// <summary>Checks names.</summary>
    /// <param name="names">The names, which already meet the format rule.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One verdict per name, in order.</returns>
    Task<IReadOnlyList<NameVerdict>> CheckAsync(IReadOnlyList<NameToCheck> names, CancellationToken cancellationToken);
}

/// <summary>What happened to a request to start a match.</summary>
public enum LaunchOutcome
{
    /// <summary>The match is scheduled.</summary>
    Accepted,

    /// <summary>A limit was reached: a fan match is live, or the day's allowance is used.</summary>
    Busy,

    /// <summary>The pipeline that plays fan matches isn't connected.</summary>
    Unavailable
}

/// <summary>
/// The result of a request to start a match.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Match">The scheduled match, when accepted.</param>
/// <param name="RetryAfter">How long to wait before trying again, when busy.</param>
/// <param name="Reason">Why not, in plain words, when busy or unavailable.</param>
public sealed record MatchLaunch(LaunchOutcome Outcome, MatchAccepted? Match, TimeSpan? RetryAfter, string? Reason);

/// <summary>
/// Starts fan matches (ADR-032). The demo can't: the team-builder lane writes the match request
/// to Cosmos DB and starts the simulator job, within the limits.
/// </summary>
public interface IMatchLauncher
{
    /// <summary>
    /// Whether this launcher can start a match at all. False for the stand-in used when the live
    /// pipeline isn't connected (ADR-032), so <c>/api/status</c> can report that fan matches are
    /// coming soon (ADR-072). Must be cheap and do no I/O.
    /// </summary>
    bool CanLaunch { get; }

    /// <summary>Starts a match that has already been validated.</summary>
    /// <param name="request">The match.</param>
    /// <param name="idempotencyKey">The client's key for this request, so a repeat returns the same match; or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened.</returns>
    Task<MatchLaunch> LaunchAsync(MatchRequest request, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Whether a team is in a fan match that's scheduled or live, so it mustn't be deleted.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True while it's playing.</returns>
    Task<bool> IsTeamPlayingAsync(string teamId, CancellationToken cancellationToken);
}

/// <summary>
/// A team with this ID is already stored. IDs carry a random suffix, so it's a freak collision
/// that the create endpoint answers by trying a fresh ID.
/// </summary>
/// <param name="teamId">The ID that was taken.</param>
public sealed class TeamAlreadyExistsException(string teamId) : Exception($"Team '{teamId}' already exists.")
{
    /// <summary>The ID that was taken.</summary>
    public string TeamId { get; } = teamId;
}
