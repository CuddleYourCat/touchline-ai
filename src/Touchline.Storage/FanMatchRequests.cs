using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Matches;
using Touchline.Domain.Storage;

namespace Touchline.Storage;

/// <summary>
/// Where fan match requests live (ADR-050): the Match Centre writes them, the simulator job reads
/// one and moves its status on, and the readers of a match take its rosters from it.
/// </summary>
/// <remarks>
/// Shaped for the limits slice (H4): a create that reports an existing ID lets an
/// <c>Idempotency-Key</c> map to a deterministic request ID; a status change that's conditional on
/// the current status lets a refund and a finish never race; and the list of active requests is
/// what the one-live check reads.
/// </remarks>
public interface IFanMatchRequestStore
{
    /// <summary>Writes a new request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True if written; false if a request with that ID already exists, which is left unchanged.</returns>
    Task<bool> CreateAsync(FanMatchRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one request.</summary>
    /// <param name="requestId">The request (and match) ID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The request, or null if there's no such request.</returns>
    Task<FanMatchRequest?> GetAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a request to a new status, only if it's in one of the expected statuses: a single
    /// conditional patch, so two writers can't both win.
    /// </summary>
    /// <param name="requestId">The request.</param>
    /// <param name="status">The new status.</param>
    /// <param name="reason">Why, for a failure; trimmed to <see cref="FanMatchRequest.MaxReasonLength"/>.</param>
    /// <param name="expected">The statuses it may move from.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>False if there's no such request, or it's no longer in an expected status.</returns>
    Task<bool> SetStatusAsync(string requestId, FanMatchStatus status, string? reason, IReadOnlyCollection<FanMatchStatus> expected, CancellationToken cancellationToken);

    /// <summary>The requests that are scheduled or running and were created since a given time.</summary>
    /// <param name="createdSince">Older requests are left out, so one whose job died without a word stops counting.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The active requests.</returns>
    Task<IReadOnlyList<FanMatchRequest>> ListActiveAsync(DateTimeOffset createdSince, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps fan match requests in the Cosmos DB <c>match-requests</c> container (ADR-050): one small
/// document per request, partitioned by <c>/id</c>, read and written through the stream API with
/// <see cref="DomainJson"/>, like every other document.
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosFanMatchRequestStore(CosmosClient client) : IFanMatchRequestStore
{
    /// <summary>The active-requests query: scheduled or running, created since <c>@since</c>.</summary>
    internal const string ActiveQuery =
        "SELECT * FROM c WHERE (c.status = 'Scheduled' OR c.status = 'Running') AND c.createdUtc >= @since";

    private Container Requests => client.GetContainer(CosmosNames.Database, CosmosNames.MatchRequests);

    /// <inheritdoc />
    public async Task<bool> CreateAsync(FanMatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CosmosIds.IsValid(request.Id))
        {
            throw new ArgumentException($"'{request.Id}' isn't a valid request ID.", nameof(request));
        }

        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, DomainJson.Options));
        using var response = await Requests.CreateItemStreamAsync(content, new PartitionKey(request.Id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <inheritdoc />
    public Task<FanMatchRequest?> GetAsync(string requestId, CancellationToken cancellationToken) =>
        CosmosIds.IsValid(requestId)
            ? CosmosQueries.ReadOneAsync<FanMatchRequest>(Requests, requestId, new PartitionKey(requestId), cancellationToken)
            : Task.FromResult<FanMatchRequest?>(null);

    /// <inheritdoc />
    public async Task<bool> SetStatusAsync(
        string requestId,
        FanMatchStatus status,
        string? reason,
        IReadOnlyCollection<FanMatchStatus> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!CosmosIds.IsValid(requestId) || expected.Count == 0)
        {
            return false;
        }

        // Plain strings for every value, so the patch reads back through DomainJson whatever
        // serialiser the client was configured with. The statuses are enum names, never user text.
        PatchOperation[] operations =
        [
            PatchOperation.Set("/status", status.ToString()),
            PatchOperation.Set("/reason", FanMatchRequest.Shorten(reason)),
            PatchOperation.Set("/updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
        ];
        var options = new PatchItemRequestOptions { FilterPredicate = StatusFilter(expected) };

        using var response = await Requests.PatchItemStreamAsync(requestId, new PartitionKey(requestId), operations, options, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FanMatchRequest>> ListActiveAsync(DateTimeOffset createdSince, CancellationToken cancellationToken)
    {
        // The same text DomainJson writes for createdUtc, so the comparison is between like strings.
        var since = JsonSerializer.Serialize(createdSince.ToUniversalTime(), DomainJson.Options).Trim('"');
        var query = new QueryDefinition(ActiveQuery).WithParameter("@since", since);
        return await CosmosQueries.ReadAllAsync<FanMatchRequest>(Requests, query, partition: null, cancellationToken);
    }

    /// <summary>The patch condition for a set of expected statuses, for example <c>FROM c WHERE c.status IN ('Scheduled')</c>.</summary>
    /// <param name="expected">The statuses.</param>
    /// <returns>The predicate.</returns>
    internal static string StatusFilter(IEnumerable<FanMatchStatus> expected) =>
        $"FROM c WHERE c.status IN ({string.Join(", ", expected.Distinct().Select(s => $"'{s}'"))})";
}
