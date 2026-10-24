namespace Touchline.Search;

/// <summary>
/// Settings for possession search (ADR-040, ADR-041, ADR-056), bound from the <c>Search</c>
/// configuration section. Nothing here is a secret: access is keyless, through managed identity.
/// </summary>
public sealed class PossessionSearchOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Search";

    /// <summary>The Azure AI Search endpoint. Unset means search is switched off.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The index name.</summary>
    public string IndexName { get; set; } = "possessions";

    /// <summary>The semantic configuration in <c>possessions-index.json</c>.</summary>
    public string SemanticConfigurationName { get; set; } = "possessions-semantic";

    /// <summary>The embedding model deployment. Never <c>ada-002</c>.</summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>The whole-query timeout in seconds, so a slow Search never stalls the Analyst (ADR-040).</summary>
    public double QueryTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// The sub-budget for embedding the query. When it runs out the query runs keyword only inside
    /// the remaining <see cref="QueryTimeoutSeconds"/> instead of timing out whole (ADR-040).
    /// </summary>
    public TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest query text sent to Search and the embedder, in characters. A cost and abuse cap on the fan's words.</summary>
    public int MaxQueryCharacters { get; set; } = 500;

    /// <summary>The most possession summaries per match the small chat model writes (ADR-056); the rest use the template.</summary>
    public int MaxModelSummariesPerMatch { get; set; } = 20;

    /// <summary>How many model summary calls run at once; the per-match cap is unaffected. Default 4, minimum 1.</summary>
    public int MaxSummaryConcurrency { get; set; } = 4;

    /// <summary>
    /// The most characters of one text sent to the embedding model (the budget on that call path):
    /// longer text is cut at a word boundary first. A summary is capped at 600 characters already.
    /// </summary>
    public int MaxEmbeddingInputChars { get; set; } = 2000;

    /// <summary>The most documents embedded in one match's single batch call; the embeddings API accepts up to 2,048 inputs.</summary>
    public int MaxEmbeddingDocumentsPerMatch { get; set; } = 2000;

    /// <summary>Whether search is configured: true when <see cref="Endpoint"/> is set.</summary>
    public bool IsConfigured => Endpoint is not null;
}
