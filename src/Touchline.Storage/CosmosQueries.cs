using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;

namespace Touchline.Storage;

/// <summary>
/// Reads Cosmos DB documents through the stream API, so they're read by <see cref="DomainJson"/>
/// rather than the SDK's default Newtonsoft.Json serialiser. Shared by the Event Processor and
/// the Match Centre, so the domain itself stays free of the Cosmos DB SDK.
/// </summary>
public static class CosmosQueries
{
    /// <summary>
    /// Reads every page of a single-partition query's results.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="container">The container to query.</param>
    /// <param name="query">The query.</param>
    /// <param name="partition">The one partition it runs in.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Every document the query returns.</returns>
    public static Task<List<T>> ReadAllAsync<T>(Container container, QueryDefinition query, PartitionKey partition, CancellationToken cancellationToken) =>
        ReadAllAsync<T>(container, query, (PartitionKey?)partition, cancellationToken);

    /// <summary>
    /// Reads every page of a query's results, in one partition or across them all.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="container">The container to query.</param>
    /// <param name="query">The query.</param>
    /// <param name="partition">The one partition it runs in, or null to fan out across them all.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Every document the query returns.</returns>
    public static async Task<List<T>> ReadAllAsync<T>(Container container, QueryDefinition query, PartitionKey? partition, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var options = partition is { } key ? new QueryRequestOptions { PartitionKey = key } : null;
        using var pages = container.GetItemQueryStreamIterator(query, requestOptions: options);
        while (pages.HasMoreResults)
        {
            using var page = await pages.ReadNextAsync(cancellationToken);
            page.EnsureSuccessStatusCode();

            // A page is { "Documents": [ ... ], "_count": n, ... }.
            using var json = await JsonDocument.ParseAsync(page.Content, cancellationToken: cancellationToken);
            results.AddRange(json.RootElement.GetProperty("Documents").Deserialize<List<T>>(DomainJson.Options) ?? []);
        }

        return results;
    }

    /// <summary>
    /// Reads one document by its ID and partition key: the cheapest Cosmos DB operation.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="container">The container to read.</param>
    /// <param name="id">The document ID.</param>
    /// <param name="partition">The document's partition.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The document, or null when there's no such document.</returns>
    public static async Task<T?> ReadOneAsync<T>(Container container, string id, PartitionKey partition, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await container.ReadItemStreamAsync(id, partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<T>(response.Content, DomainJson.Options, cancellationToken);
    }
}
