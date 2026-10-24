namespace Touchline.Search;

/// <summary>
/// One query to the possession index, as plain data: what the request looks like, with no Azure types.
/// </summary>
/// <param name="Filter">The OData filter, always scoped to one match.</param>
/// <param name="Text">The keyword search text (the fan's words; never part of the filter).</param>
/// <param name="Vector">The query embedding for the vector half, or null for keyword only.</param>
/// <param name="SemanticConfiguration">The semantic configuration to re-rank with, or null for no semantic ranking.</param>
/// <param name="Top">The most documents to return, also the nearest-neighbour count.</param>
internal sealed record PossessionQuery(string Filter, string Text, float[]? Vector, string? SemanticConfiguration, int Top);

/// <summary>A document the index returned and the score to rank it by (reranker score when present).</summary>
/// <param name="Document">The stored document.</param>
/// <param name="Score">The score, higher is better.</param>
internal sealed record ScoredDocument(PossessionDocument Document, double Score);

/// <summary>
/// A thin seam over <c>SearchClient</c> (ADR-040), so tests can assert the request shape without HTTP.
/// </summary>
internal interface IPossessionSearchTransport
{
    /// <summary>Runs one query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The documents, best first.</returns>
    Task<IReadOnlyList<ScoredDocument>> QueryAsync(PossessionQuery query, CancellationToken cancellationToken);
}
