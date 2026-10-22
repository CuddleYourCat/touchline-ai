using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Season;
using Touchline.Domain.Storage;
using Touchline.Storage;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Stores the season projection in Cosmos DB (ADR-025): <c>player-matches</c>, partitioned by
/// <c>/playerId</c>, and <c>player-seasons</c>, partitioned by <c>/season</c>. Through the stream
/// APIs and <see cref="DomainJson"/>, like <see cref="CosmosMatchStore"/>.
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosSeasonStore(CosmosClient client) : ISeasonStore
{
    /// <summary>The container holding each player's part in each fixture.</summary>
    public const string PlayerMatchesContainerName = CosmosNames.PlayerMatches;

    /// <summary>The container holding each player's rebuilt season.</summary>
    public const string PlayerSeasonsContainerName = CosmosNames.PlayerSeasons;

    private Container PlayerMatches => client.GetContainer(CosmosMatchStore.DatabaseName, PlayerMatchesContainerName);

    private Container PlayerSeasons => client.GetContainer(CosmosMatchStore.DatabaseName, PlayerSeasonsContainerName);

    /// <inheritdoc />
    public async Task UpsertPlayerMatchesAsync(IReadOnlyList<PlayerMatch> matches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);

        // Every player is their own partition, so there's no batch to share. Twenty-two writes
        // a fixture, a few at a time.
        await Parallel.ForEachAsync(matches, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (match, token) =>
        {
            using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(match, DomainJson.Options));
            using var response = await PlayerMatches.UpsertItemStreamAsync(content, new PartitionKey(match.PlayerId), cancellationToken: token);
            response.EnsureSuccessStatusCode();
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerMatch>> GetPlayerMatchesAsync(string season, string playerId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.season = @season").WithParameter("@season", season);
        return await CosmosQueries.ReadAllAsync<PlayerMatch>(PlayerMatches, query, new PartitionKey(playerId), cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertPlayerSeasonAsync(PlayerSeason season, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(season);

        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(season, DomainJson.Options));
        using var response = await PlayerSeasons.UpsertItemStreamAsync(content, new PartitionKey(season.Season), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
