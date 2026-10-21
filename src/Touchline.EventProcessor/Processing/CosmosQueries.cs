using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Touchline.Domain;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Runs a single-partition query through the stream API, so the results are read by
/// <see cref="DomainJson"/> rather than the SDK's default Newtonsoft.Json serialiser.
/// </summary>
internal static class CosmosQueries
{
    /// <summary>
    /// Reads every page of a query's results.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="container">The container to query.</param>
    /// <param name="query">The query.</param>
    /// <param name="partition">The one partition it runs in.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Every document the query returns.</returns>
    public static async Task<List<T>> ReadAllAsync<T>(Container container, QueryDefinition query, PartitionKey partition, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        using var pages = container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = partition });
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
}
