using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Storage;

namespace Touchline.Simulator.Publishing;

/// <summary>One player's line in the season, as the simulator needs it before a fixture.</summary>
/// <param name="PlayerId">The player.</param>
/// <param name="Rounds">The rounds they started.</param>
/// <param name="BannedRounds">The rounds they're suspended for.</param>
public sealed record SeasonStanding(string PlayerId, IReadOnlyList<int> Rounds, IReadOnlyList<int> BannedRounds);

/// <summary>
/// The season as the projection has it so far (ADR-025): which rounds are done, and who is banned
/// when. Read once before a fixture.
/// </summary>
/// <param name="players">Every player with a season document.</param>
public sealed class SeasonStatus(IReadOnlyList<SeasonStanding> players)
{
    /// <summary>A season nothing has been projected into yet.</summary>
    public static SeasonStatus Empty { get; } = new([]);

    /// <summary>The latest round any player has started, or 0 before the first.</summary>
    public int LatestRound => players.SelectMany(p => p.Rounds).DefaultIfEmpty(0).Max();

    /// <summary>Whether a round has already been played and projected.</summary>
    /// <param name="round">The round.</param>
    /// <returns>True when any player started it.</returns>
    public bool HasPlayed(int round) => players.Any(p => p.Rounds.Contains(round));

    /// <summary>The players suspended for a round.</summary>
    /// <param name="round">The round.</param>
    /// <returns>Their IDs, empty when nobody is banned.</returns>
    public IReadOnlySet<string> BannedFor(int round) =>
        players.Where(p => p.BannedRounds.Contains(round)).Select(p => p.PlayerId).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Reads the season projection before a fixture (ADR-025).</summary>
public interface ISeasonReader
{
    /// <summary>
    /// Reads where the season stands: every player's rounds and bans.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The season so far, empty before its first fixture is projected.</returns>
    Task<SeasonStatus> ReadAsync(string season, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the <c>player-seasons</c> container's partition for one season: a single-partition query,
/// of about 34 small rows. The simulator's identity can only read this container.
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosSeasonReader(CosmosClient client) : ISeasonReader
{
    /// <summary>The database name, as the Event Processor writes it.</summary>
    public const string DatabaseName = CosmosNames.Database;

    /// <summary>The container holding each player's rebuilt season.</summary>
    public const string PlayerSeasonsContainerName = CosmosNames.PlayerSeasons;

    /// <inheritdoc />
    public async Task<SeasonStatus> ReadAsync(string season, CancellationToken cancellationToken)
    {
        // Only the three fields needed. Read through the stream API with DomainJson, as everything
        // stored in Cosmos DB is, rather than the SDK's default Newtonsoft.Json serialiser.
        var query = new QueryDefinition("SELECT c.playerId, c.rounds, c.bannedRounds FROM c");
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(season) };

        var players = new List<SeasonStanding>();
        using var pages = client.GetContainer(DatabaseName, PlayerSeasonsContainerName).GetItemQueryStreamIterator(query, requestOptions: options);
        while (pages.HasMoreResults)
        {
            using var page = await pages.ReadNextAsync(cancellationToken);
            page.EnsureSuccessStatusCode();

            // A page is { "Documents": [ ... ], "_count": n, ... }.
            using var json = await JsonDocument.ParseAsync(page.Content, cancellationToken: cancellationToken);
            players.AddRange(json.RootElement.GetProperty("Documents").Deserialize<List<SeasonStanding>>(DomainJson.Options) ?? []);
        }

        return new SeasonStatus(players);
    }
}
