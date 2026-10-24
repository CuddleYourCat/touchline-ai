using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace Touchline.Search;

/// <summary>The real transport: <see cref="SearchClient"/> over HTTPS with the caller's managed identity.</summary>
internal sealed class SearchClientTransport(SearchClient client) : IPossessionSearchTransport
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ScoredDocument>> QueryAsync(PossessionQuery query, CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            Filter = query.Filter,
            Size = query.Top
        };

        if (query.Vector is { } vector)
        {
            options.VectorSearch = new VectorSearchOptions();
            options.VectorSearch.Queries.Add(new VectorizedQuery(vector)
            {
                KNearestNeighborsCount = query.Top,
                Fields = { "summaryVector" }
            });
        }

        if (query.SemanticConfiguration is { } semantic)
        {
            options.QueryType = SearchQueryType.Semantic;
            options.SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = semantic };
        }

        var response = await client.SearchAsync<PossessionDocument>(query.Text, options, cancellationToken).ConfigureAwait(false);
        var results = new List<ScoredDocument>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ScoredDocument(result.Document, result.SemanticSearch?.RerankerScore ?? result.Score ?? 0));
        }

        return results;
    }
}
