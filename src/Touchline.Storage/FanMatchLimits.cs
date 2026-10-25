using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;
using Touchline.Domain.Storage;

namespace Touchline.Storage;

/// <summary>
/// An <c>Idempotency-Key</c> already claimed: the match it started, and when (ADR-050).
/// </summary>
/// <param name="RequestId">The match request the key belongs to.</param>
/// <param name="ClaimedUtc">When the key was claimed.</param>
public sealed record IdempotencyClaim(string RequestId, DateTimeOffset ClaimedUtc);

/// <summary>
/// The fan-match limits' state (ADR-032, ADR-050): a counter per UTC day, and the claimed
/// <c>Idempotency-Key</c>s. Both live in the <c>match-requests</c> container beside the requests,
/// as small documents with their own time to live.
/// </summary>
public interface IFanMatchLimitStore
{
    /// <summary>Takes one of the day's slots, if any are left.</summary>
    /// <param name="day">The UTC day.</param>
    /// <param name="limit">How many matches the day allows.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>False when the day's slots are all taken.</returns>
    Task<bool> TryTakeDaySlotAsync(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Gives a slot back, never going below zero (a refund for a match that didn't start).</summary>
    /// <param name="day">The UTC day the slot was taken on.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the slot is back.</returns>
    Task ReturnDaySlotAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Claims a key for a new request, unless it's already claimed.
    /// </summary>
    /// <param name="key">The client's key, already validated.</param>
    /// <param name="requestId">The new request's ID.</param>
    /// <param name="now">The time now.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Null when this call claimed it; otherwise the existing claim, left unchanged.</returns>
    Task<IdempotencyClaim?> TryClaimKeyAsync(string key, string requestId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves a claimed key to a new request: its old one failed, so a retry with the key starts afresh.</summary>
    /// <param name="key">The client's key.</param>
    /// <param name="previous">The claim as read.</param>
    /// <param name="requestId">The new request's ID.</param>
    /// <param name="now">The time now.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>False if someone else moved it first.</returns>
    Task<bool> TryReclaimKeyAsync(string key, IdempotencyClaim previous, string requestId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Lets a key go, when the request it was claimed for was never written (a limit, or an outage).</summary>
    /// <param name="key">The client's key.</param>
    /// <param name="requestId">The request it was claimed for; a claim for another request is left alone.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the key is released.</returns>
    Task ReleaseKeyAsync(string key, string requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The limits in Cosmos DB (ADR-050). A day's counter is <c>day-yyyyMMdd</c>, changed only by an
/// ETag-guarded replace (or a create for the day's first match), retried on a <c>412</c> or
/// <c>409</c>, so two launches at once each count once. A key is <c>idem-{32 hex of its SHA-256}</c>,
/// created only if it doesn't exist; the key itself is never stored. Counters keep for three days
/// and keys for one, through per-document <c>ttl</c> (the container's default time to live is on,
/// with nothing expiring by default).
/// </summary>
/// <param name="client">The Cosmos DB client, configured by Aspire.</param>
public sealed class CosmosFanMatchLimitStore(CosmosClient client) : IFanMatchLimitStore
{
    /// <summary>How many times a conditional write is tried before giving up.</summary>
    public const int Attempts = 5;

    /// <summary>A day counter's time to live: long enough to look back on a day, short enough to clear itself.</summary>
    public static readonly TimeSpan DayTtl = TimeSpan.FromDays(3);

    /// <summary>A key's time to live: a repeat a day later is a new match.</summary>
    public static readonly TimeSpan KeyTtl = TimeSpan.FromDays(1);

    private Container Requests => client.GetContainer(CosmosNames.Database, CosmosNames.MatchRequests);

    /// <summary>A day counter's document ID.</summary>
    /// <param name="day">The UTC day.</param>
    /// <returns>For example <c>day-20261024</c>.</returns>
    public static string DayId(DateOnly day) => string.Create(CultureInfo.InvariantCulture, $"day-{day:yyyyMMdd}");

    /// <summary>A key's document ID: a hash, so the key itself is never stored and any key makes a valid ID.</summary>
    /// <param name="key">The client's key.</param>
    /// <returns>For example <c>idem-3f2a…</c>, 37 characters.</returns>
    public static string KeyId(string key) =>
        $"idem-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}";

    /// <inheritdoc />
    public async Task<bool> TryTakeDaySlotAsync(DateOnly day, int limit, CancellationToken cancellationToken)
    {
        var id = DayId(day);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await CosmosQueries.ReadOneAsync<DayCounter>(Requests, id, new PartitionKey(id), cancellationToken);
            if (current is null)
            {
                if (limit < 1)
                {
                    return false;
                }

                if (await CreateAsync(new DayCounter(id, 1, (int)DayTtl.TotalSeconds), id, cancellationToken))
                {
                    return true;
                }

                continue; // Someone created it first: read theirs and count on top.
            }

            if (current.Count >= limit)
            {
                return false;
            }

            if (await ReplaceAsync(current with { Count = current.Count + 1, ETag = null }, current.ETag, id, cancellationToken))
            {
                return true;
            }
        }

        throw new InvalidOperationException($"The day counter {id} kept changing; no slot was taken.");
    }

    /// <inheritdoc />
    public async Task ReturnDaySlotAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var id = DayId(day);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await CosmosQueries.ReadOneAsync<DayCounter>(Requests, id, new PartitionKey(id), cancellationToken);
            if (current is not { Count: > 0 })
            {
                return;
            }

            if (await ReplaceAsync(current with { Count = current.Count - 1, ETag = null }, current.ETag, id, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException($"The day counter {id} kept changing; the slot wasn't returned.");
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim?> TryClaimKeyAsync(string key, string requestId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = KeyId(key);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await CreateAsync(new KeyDocument(id, requestId, now, (int)KeyTtl.TotalSeconds), id, cancellationToken))
            {
                return null;
            }

            // Already claimed. Read it; if it went in the meantime (released, or expired), try again.
            if (await CosmosQueries.ReadOneAsync<KeyDocument>(Requests, id, new PartitionKey(id), cancellationToken) is { } existing)
            {
                return new IdempotencyClaim(existing.RequestId, existing.ClaimedUtc);
            }
        }

        throw new InvalidOperationException("An idempotency key kept changing while it was being claimed.");
    }

    /// <inheritdoc />
    public async Task<bool> TryReclaimKeyAsync(string key, IdempotencyClaim previous, string requestId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var id = KeyId(key);
        var current = await CosmosQueries.ReadOneAsync<KeyDocument>(Requests, id, new PartitionKey(id), cancellationToken);
        if (current is null)
        {
            return await CreateAsync(new KeyDocument(id, requestId, now, (int)KeyTtl.TotalSeconds), id, cancellationToken);
        }

        return current.RequestId == previous.RequestId
            && await ReplaceAsync(new KeyDocument(id, requestId, now, (int)KeyTtl.TotalSeconds), current.ETag, id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseKeyAsync(string key, string requestId, CancellationToken cancellationToken)
    {
        var id = KeyId(key);
        if (await CosmosQueries.ReadOneAsync<KeyDocument>(Requests, id, new PartitionKey(id), cancellationToken) is not { } current
            || current.RequestId != requestId)
        {
            return;
        }

        using var response = await Requests.DeleteItemStreamAsync(
            id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<bool> CreateAsync<T>(T document, string id, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, DomainJson.Options));
        using var response = await Requests.CreateItemStreamAsync(content, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    // Replaces only if the ETag is still the one read; a 412 (or a 404, gone meanwhile) means try again.
    private async Task<bool> ReplaceAsync<T>(T document, string? etag, string id, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, DomainJson.Options));
        using var response = await Requests.ReplaceItemStreamAsync(
            content, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>A UTC day's count of fan matches started.</summary>
    internal sealed record DayCounter(
        string Id,
        int Count,
        int Ttl,
        [property: JsonPropertyName("_etag")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ETag = null);

    /// <summary>A claimed key: its hash is the ID; it holds the request it started.</summary>
    internal sealed record KeyDocument(
        string Id,
        string RequestId,
        DateTimeOffset ClaimedUtc,
        int Ttl,
        [property: JsonPropertyName("_etag")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ETag = null);
}
