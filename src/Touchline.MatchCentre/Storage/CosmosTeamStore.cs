using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Touchline.Domain;
using Touchline.Domain.Storage;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;
using Touchline.Storage;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// Settings for the Cosmos DB team store. Bound from <c>MatchCentre:Teams</c>.
/// </summary>
public sealed class TeamStoreOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:Teams";

    /// <summary>
    /// How long a deleted team's document stays before Cosmos DB's time to live removes it
    /// (ADR-048). The delete is a flag first, because the change feed doesn't see hard deletes.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan DeletedRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// A fan team as Cosmos DB holds it (ADR-048): the API's view of the team, the hash of its edit
/// key, and the soft-delete flag with its time to live. The container is partitioned by <c>/id</c>.
/// </summary>
/// <param name="Id">The team's ID, which is also its partition key.</param>
/// <param name="Detail">The team, whose <see cref="TeamDetail.Version"/> is the optimistic-concurrency version.</param>
/// <param name="EditKeyHash">The SHA-256 hash of the edit key.</param>
/// <param name="Deleted">True once the team has been deleted; it's hidden from every read.</param>
/// <param name="Ttl">Seconds until Cosmos DB removes a deleted document; absent while the team is live.</param>
/// <param name="ETag">Cosmos DB's <c>_etag</c>, read back for a conditional replace and never written.</param>
internal sealed record TeamDocument(
    string Id,
    TeamDetail Detail,
    string? EditKeyHash = null,
    bool Deleted = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ttl = null,
    [property: JsonPropertyName("_etag")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ETag = null);

/// <summary>
/// Keeps fan teams in the Cosmos DB <c>teams</c> container (ADR-048): one document per team,
/// partitioned by <c>/id</c>, so every get, add, replace and delete is a point operation. The
/// built-in profiles are served from the embedded catalogue and never written. Listing and the
/// player lookup are cross-partition queries, fine at demo scale on a serverless account.
/// </summary>
/// <remarks>
/// A delete sets <c>deleted</c> and a per-document <c>ttl</c> instead of removing the document,
/// because the change feed doesn't see hard deletes. Cosmos DB's time to live then removes it.
/// Replaces are conditional on the document's <c>_etag</c>, so a concurrent save loses cleanly.
/// </remarks>
internal sealed class CosmosTeamStore : ITeamStore
{
    private const string _liveFilter = "(NOT IS_DEFINED(c.deleted) OR c.deleted = false)";
    private const string _listQuery = $"SELECT * FROM c WHERE {_liveFilter} AND c.detail.team.origin = @origin";
    private const string _playerQuery = $"SELECT TOP 1 c.id FROM c JOIN p IN c.detail.squad WHERE p.id = @playerId AND {_liveFilter}";
    private const int _deleteAttempts = 3;
    private const string _teamsUnavailable = "The teams aren't available right now. Try again shortly.";

    private readonly CosmosClient _client;
    private readonly IReadOnlyDictionary<string, StoredTeam> _builtIn;
    private readonly TeamStoreOptions _options;
    private readonly ILogger<CosmosTeamStore> _logger;

    /// <summary>Creates the store.</summary>
    /// <param name="client">The Cosmos DB client, configured by Aspire.</param>
    /// <param name="builtIn">The built-in profiles, by team ID.</param>
    /// <param name="options">The store's settings.</param>
    /// <param name="logger">Logs outages.</param>
    public CosmosTeamStore(
        CosmosClient client,
        IReadOnlyDictionary<string, TeamProfile> builtIn,
        IOptions<TeamStoreOptions> options,
        ILogger<CosmosTeamStore> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        _builtIn = builtIn.Values.ToDictionary(t => t.Id, t => new StoredTeam(TeamViews.FromProfile(t), null), StringComparer.Ordinal);
    }

    private Container Teams => _client.GetContainer(CosmosNames.Database, CosmosNames.Teams);

    /// <inheritdoc />
    public async Task<StoredTeam?> GetAsync(string teamId, CancellationToken cancellationToken)
    {
        if (_builtIn.TryGetValue(teamId, out var builtIn))
        {
            return builtIn;
        }

        var document = await ReadLiveAsync(teamId, cancellationToken);
        return document is null ? null : ToStored(document);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredTeam>> ListAsync(TeamOrigin? origin, CancellationToken cancellationToken)
    {
        var teams = new List<StoredTeam>();
        if (origin is null or TeamOrigin.BuiltIn)
        {
            teams.AddRange(_builtIn.Values);
        }

        if (origin is null or TeamOrigin.Fan)
        {
            var query = new QueryDefinition(_listQuery).WithParameter("@origin", TeamOrigin.Fan.ToString());
            var documents = await MatchSources.GuardAsync(
                () => CosmosQueries.ReadAllAsync<TeamDocument>(Teams, query, partition: null, cancellationToken),
                "the fan teams", _teamsUnavailable, _logger, cancellationToken);
            teams.AddRange(documents.Select(ToStored));
        }

        return
        [
            .. teams
                .OrderBy(t => t.Detail.Team.Origin)
                .ThenBy(t => t.Detail.Team.Name, StringComparer.Ordinal)
        ];
    }

    /// <inheritdoc />
    public async Task<bool> PlayerExistsAsync(string playerId, CancellationToken cancellationToken)
    {
        if (_builtIn.Values.Any(t => t.Detail.Squad.Any(p => p.Id == playerId)))
        {
            return true;
        }

        var query = new QueryDefinition(_playerQuery).WithParameter("@playerId", playerId);
        var found = await MatchSources.GuardAsync(
            () => CosmosQueries.ReadAllAsync<TeamIdOnly>(Teams, query, partition: null, cancellationToken),
            "a player lookup", _teamsUnavailable, _logger, cancellationToken);
        return found.Count > 0;
    }

    /// <inheritdoc />
    public Task AddAsync(StoredTeam team, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(team);
        var id = team.Detail.Team.Id;
        if (!id.StartsWith(TeamViews.FanPrefix, StringComparison.Ordinal) || !CosmosIds.IsValid(id))
        {
            throw new ArgumentException($"'{id}' isn't a valid fan team ID.", nameof(team));
        }

        return MatchSources.GuardAsync(async () =>
        {
            using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new TeamDocument(id, team.Detail, team.EditKeyHash), DomainJson.Options));
            using var response = await Teams.CreateItemStreamAsync(content, new PartitionKey(id), cancellationToken: cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new TeamAlreadyExistsException(id);
            }

            response.EnsureSuccessStatusCode();
            return true;
        }, "a new team", _teamsUnavailable, _logger, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ReplaceAsync(StoredTeam team, int expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(team);
        var id = team.Detail.Team.Id;
        return MatchSources.GuardAsync(async () =>
        {
            if (await ReadLiveAsync(id, cancellationToken) is not { } current || current.Detail.Version != expectedVersion)
            {
                return false;
            }

            return await WriteIfUnchangedAsync(current, current with { Detail = team.Detail, EditKeyHash = team.EditKeyHash }, cancellationToken);
        }, "a team update", _teamsUnavailable, _logger, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string teamId, CancellationToken cancellationToken) =>
        MatchSources.GuardAsync(async () =>
        {
            // A concurrent edit moves the ETag on, so read again and retry rather than lose the delete.
            for (var attempt = 0; attempt < _deleteAttempts; attempt++)
            {
                if (await ReadLiveAsync(teamId, cancellationToken) is not { } current)
                {
                    return true;
                }

                var ttl = (int)Math.Min(int.MaxValue, _options.DeletedRetention.TotalSeconds);
                if (await WriteIfUnchangedAsync(current, current with { Deleted = true, Ttl = ttl }, cancellationToken))
                {
                    return true;
                }
            }

            throw new InvalidOperationException($"Team '{teamId}' kept changing while it was being deleted.");
        }, "a team delete", _teamsUnavailable, _logger, cancellationToken);

    private static StoredTeam ToStored(TeamDocument document) => new(document.Detail, document.EditKeyHash);

    // A point read of a live fan team. Anything that can't be a fan team's ID is "no such team"
    // without a round trip, so an odd ID from a URL can't turn into a Cosmos DB error.
    private async Task<TeamDocument?> ReadLiveAsync(string teamId, CancellationToken cancellationToken)
    {
        if (!teamId.StartsWith(TeamViews.FanPrefix, StringComparison.Ordinal) || !CosmosIds.IsValid(teamId))
        {
            return null;
        }

        var document = await MatchSources.GuardAsync(
            () => CosmosQueries.ReadOneAsync<TeamDocument>(Teams, teamId, new PartitionKey(teamId), cancellationToken),
            "a team", _teamsUnavailable, _logger, cancellationToken);
        return document is { Deleted: false } ? document : null;
    }

    // Replaces the document only if its ETag is still the one that was read: a 412 means someone
    // else saved first, and the caller reports the conflict.
    private async Task<bool> WriteIfUnchangedAsync(TeamDocument read, TeamDocument next, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(next with { ETag = null }, DomainJson.Options));
        using var response = await Teams.ReplaceItemStreamAsync(
            content, read.Id, new PartitionKey(read.Id), new ItemRequestOptions { IfMatchEtag = read.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private sealed record TeamIdOnly(string Id);
}
