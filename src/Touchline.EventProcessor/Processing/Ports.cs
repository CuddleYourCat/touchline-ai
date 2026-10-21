using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Season;

namespace Touchline.EventProcessor.Processing;

/// <summary>A message from the event stream: its properties (the envelope) and its body.</summary>
/// <param name="Properties">The message's application properties.</param>
/// <param name="Body">The message body: one match event as JSON.</param>
public sealed record IncomingEvent(IReadOnlyDictionary<string, object> Properties, BinaryData Body);

/// <summary>
/// A match's stored document in the <c>matches</c> container (ADR-023): its metadata, once
/// the first event has arrived, and its live state. The ID is the match ID.
/// </summary>
/// <param name="Id">The document ID: the match ID.</param>
/// <param name="MatchId">The match ID, which is also the partition key.</param>
/// <param name="Metadata">The match's metadata, or null before its first event has been processed.</param>
/// <param name="State">The match state after the last event processed.</param>
public sealed record MatchRecord(string Id, string MatchId, MatchMetadata? Metadata, MatchState State)
{
    /// <summary>A match no event has been processed for yet.</summary>
    /// <param name="matchId">The match.</param>
    /// <returns>An empty record at kick-off.</returns>
    public static MatchRecord New(string matchId) => new(matchId, matchId, null, MatchState.KickOff(matchId));
}

/// <summary>Where events and match state are stored (Cosmos DB, ADR-023).</summary>
public interface IMatchStore
{
    /// <summary>
    /// Reads a match's stored document.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The document, or null when nothing has been stored for the match yet.</returns>
    Task<MatchRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores events, replacing any already stored with the same sequence, so storing a
    /// redelivered batch again changes nothing.
    /// </summary>
    /// <param name="matchId">The match the events belong to.</param>
    /// <param name="events">The events, in sequence order.</param>
    /// <param name="cancellationToken">Stops the writes.</param>
    /// <returns>A task that completes once every event is stored.</returns>
    Task UpsertEventsAsync(string matchId, IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a match's document, replacing the previous one.
    /// </summary>
    /// <param name="match">The document.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task that completes once it's stored.</returns>
    Task UpsertMatchAsync(MatchRecord match, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every stored event of a match, for the season projection (ADR-025).
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The events, in sequence order.</returns>
    Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string matchId, CancellationToken cancellationToken);
}

/// <summary>
/// Where the season projection's documents live (ADR-025): <c>player-matches</c>, partitioned by
/// player, and <c>player-seasons</c>, partitioned by season.
/// </summary>
public interface ISeasonStore
{
    /// <summary>
    /// Stores each player's part in a fixture, replacing any earlier projection of it.
    /// </summary>
    /// <param name="matches">The fixture's player documents.</param>
    /// <param name="cancellationToken">Stops the writes.</param>
    /// <returns>A task that completes once they're all stored.</returns>
    Task UpsertPlayerMatchesAsync(IReadOnlyList<PlayerMatch> matches, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every fixture a player has played in a season: one partition.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="playerId">The player.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The player's fixtures, in any order.</returns>
    Task<IReadOnlyList<PlayerMatch>> GetPlayerMatchesAsync(string season, string playerId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a player's rebuilt season, replacing the previous one.
    /// </summary>
    /// <param name="season">The season document.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task that completes once it's stored.</returns>
    Task UpsertPlayerSeasonAsync(PlayerSeason season, CancellationToken cancellationToken);
}

/// <summary>Where key moments go: the Service Bus key-moments topic (ADR-004, ADR-023).</summary>
public interface IKeyMomentPublisher
{
    /// <summary>
    /// Publishes key moments, in order.
    /// </summary>
    /// <param name="moments">The moments.</param>
    /// <param name="cancellationToken">Stops the publish.</param>
    /// <returns>A task that completes once they're all accepted.</returns>
    Task PublishAsync(IReadOnlyList<KeyMoment> moments, CancellationToken cancellationToken);
}
