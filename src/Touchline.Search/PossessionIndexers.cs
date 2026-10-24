using Azure.Search.Documents;

namespace Touchline.Search;

/// <summary>
/// Writes possession documents to the search index (ADR-039). The Event Processor's side of
/// <c>Touchline.Search</c>; the Match Centre never writes.
/// </summary>
public interface IPossessionIndexer
{
    /// <summary>
    /// Whether this indexer writes anywhere. False for <see cref="NullPossessionIndexer"/>, so the
    /// caller can skip the reads and the embedding call that would be wasted.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Merges documents into the index by key, so writing the same documents again changes nothing.
    /// </summary>
    /// <param name="documents">The documents, each with its vector.</param>
    /// <param name="cancellationToken">Stops the writes.</param>
    /// <returns>A task that completes once every document is written.</returns>
    /// <exception cref="PossessionIndexingException">The index rejected one or more documents.</exception>
    Task UpsertAsync(IReadOnlyList<PossessionDocument> documents, CancellationToken cancellationToken);
}

/// <summary>The indexer used when Search isn't configured (ADR-057): it does nothing.</summary>
public sealed class NullPossessionIndexer : IPossessionIndexer
{
    /// <inheritdoc />
    public bool Enabled => false;

    /// <inheritdoc />
    public Task UpsertAsync(IReadOnlyList<PossessionDocument> documents, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Writes to Azure AI Search with <c>MergeOrUploadDocuments</c> in chunks of <see cref="ChunkSize"/>
/// (ADR-039). Access is keyless, through the credential the <see cref="SearchClient"/> was built with.
/// </summary>
/// <param name="client">The search client for the possessions index.</param>
public sealed class AzurePossessionIndexer(SearchClient client) : IPossessionIndexer
{
    /// <summary>The most documents in one request; Azure AI Search accepts up to 1,000.</summary>
    public const int ChunkSize = 1000;

    /// <inheritdoc />
    public bool Enabled => true;

    /// <inheritdoc />
    public async Task UpsertAsync(IReadOnlyList<PossessionDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        foreach (var chunk in documents.Chunk(ChunkSize))
        {
            var response = await client.MergeOrUploadDocumentsAsync(chunk, cancellationToken: cancellationToken).ConfigureAwait(false);
            var failed = response.Value.Results.Where(r => !r.Succeeded).ToList();
            if (failed.Count > 0)
            {
                // A 207 reports each document on its own, so a partial failure must not look like success.
                throw new PossessionIndexingException(
                    $"{failed.Count} of {chunk.Length} possession documents were rejected, for example '{failed[0].Key}': {failed[0].ErrorMessage}");
            }
        }
    }
}

/// <summary>The index rejected some of the documents written to it.</summary>
public sealed class PossessionIndexingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PossessionIndexingException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was rejected.</param>
    public PossessionIndexingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What was rejected.</param>
    /// <param name="innerException">The cause.</param>
    public PossessionIndexingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
