using Touchline.Domain.Analytics;
using Touchline.Domain.Season;
using Touchline.Domain.Viewing;

namespace Touchline.MatchCentre.Api;

/// <summary>
/// Everything the read endpoints need about matches (ADR-027). The demo source implements it
/// over one recorded match; the live read side implements it over Cosmos DB. A method returns
/// null when the match (or player) doesn't exist.
/// </summary>
public interface IMatchReadModel
{
    /// <summary>Lists matches, newest first.</summary>
    /// <param name="status">Only matches in this status, or null for all.</param>
    /// <param name="season">Only fixtures in this season, or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matches, and whether a source was missing from them.</returns>
    Task<MatchList> ListMatchesAsync(MatchStatus? status, string? season, CancellationToken cancellationToken);

    /// <summary>Reads one match in full.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The match, or null if there's no such match.</returns>
    Task<MatchDetail?> GetMatchAsync(string matchId, CancellationToken cancellationToken);

    /// <summary>Reads a page of a match's events.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="after">Only events after this sequence.</param>
    /// <param name="limit">The most events to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, or null if there's no such match.</returns>
    Task<EventPage?> GetEventsAsync(string matchId, long after, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a match's moments so far, with commentary for the viewer.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="viewer">Whose commentary to pick.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The moments in sequence order, or null if there's no such match.</returns>
    Task<IReadOnlyList<MomentView>?> GetMomentsAsync(string matchId, Viewer viewer, CancellationToken cancellationToken);

    /// <summary>Reads a match's momentum so far.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The readings in time order, or null if there's no such match.</returns>
    Task<IReadOnlyList<MomentumReading>?> GetMomentumAsync(string matchId, CancellationToken cancellationToken);

    /// <summary>Reads a player's season card (ADR-025).</summary>
    /// <param name="season">The season.</param>
    /// <param name="playerId">The player.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The card, or null if the player hasn't played that season.</returns>
    Task<PlayerSeason?> GetPlayerSeasonAsync(string season, string playerId, CancellationToken cancellationToken);
}

/// <summary>
/// The live stream's source (ADR-029). The demo source replays the recorded match on a clock;
/// the live read side follows the Cosmos DB change feed. Heartbeats are added by the endpoint,
/// not here.
/// </summary>
public interface ILiveFeed
{
    /// <summary>
    /// Follows a match from a point until full time, or until cancelled.
    /// </summary>
    /// <param name="matchId">The match, which the caller has already checked exists.</param>
    /// <param name="after">
    /// The last event sequence the client has seen. Null means "from now": the feed starts
    /// with a <see cref="StreamEvents.State"/> snapshot, then carries on live.
    /// </param>
    /// <param name="viewer">Whose commentary to send.</param>
    /// <param name="cancellationToken">Cancels the stream, when the client disconnects.</param>
    /// <returns>The messages, as they happen.</returns>
    IAsyncEnumerable<LiveMessage> FollowAsync(string matchId, long? after, Viewer viewer, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the feed can serve the match, before the stream's response starts, so a source
    /// that's down is refused with a status code rather than a stream that hangs. A source with
    /// nothing to check leaves this as it is.
    /// </summary>
    /// <param name="matchId">The match, which the caller has already checked exists.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Completes when the feed is ready.</returns>
    Task PrepareAsync(string matchId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A match list with how complete it is. It stays out of the HTTP contract: the endpoint returns
/// <see cref="Matches"/> as the body, and turns <see cref="Degraded"/> into a response header.
/// </summary>
/// <param name="Matches">The matches.</param>
/// <param name="Degraded">True when Cosmos DB couldn't be read, so the list holds the demo matches alone.</param>
public sealed record MatchList(IReadOnlyList<MatchSummary> Matches, bool Degraded = false);
