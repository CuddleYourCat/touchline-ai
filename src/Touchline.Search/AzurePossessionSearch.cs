using System.Diagnostics;
using System.Net;
using Azure;
using Azure.Search.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Domain.Squad;

namespace Touchline.Search;

/// <summary>
/// <see cref="IPossessionSearch"/> over Azure AI Search (ADR-040): a keyword plus vector hybrid query,
/// filtered to one match and re-ranked by the semantic ranker. It never throws to the Analyst: each
/// failure steps down a ladder (no vector, no semantic ranking, empty list), and each step is counted
/// on <c>search.fallback</c>. Caller cancellation is the one exception, and propagates.
/// </summary>
public sealed partial class AzurePossessionSearch : IPossessionSearch
{
    /// <summary>The most results one query returns, whatever the caller asks for.</summary>
    public const int MaxTop = 20;

    private const int _maxMatchIdLength = 100;

    // A semantic retry is only worth starting if at least this share of the overall budget is left.
    private const double _minRetryBudgetFraction = 0.25;

    private readonly IPossessionSearchTransport _transport;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly PossessionSearchOptions _options;
    private readonly SearchMetrics _metrics;
    private readonly ILogger<AzurePossessionSearch> _logger;

    /// <summary>Creates the search over an Azure AI Search client.</summary>
    /// <param name="client">The index client (keyless, read-only role).</param>
    /// <param name="embeddings">The query embedder, or null when no model is configured (keyword only).</param>
    /// <param name="options">The search settings.</param>
    /// <param name="metrics">The fallback counter.</param>
    /// <param name="logger">The logger.</param>
    public AzurePossessionSearch(
        SearchClient client,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        IOptions<PossessionSearchOptions> options,
        SearchMetrics metrics,
        ILogger<AzurePossessionSearch> logger)
        : this(new SearchClientTransport(client), embeddings, options, metrics, logger)
    {
    }

    internal AzurePossessionSearch(
        IPossessionSearchTransport transport,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        IOptions<PossessionSearchOptions> options,
        SearchMetrics metrics,
        ILogger<AzurePossessionSearch> logger)
    {
        _transport = transport;
        _embeddings = embeddings;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
        if (embeddings is null)
        {
            LogNoEmbedder();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PossessionHit>> SearchAsync(string matchId, string query, int top, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSafeMatchId(matchId) || string.IsNullOrWhiteSpace(query))
        {
            // A match ID outside the key alphabet can't match any document (PossessionDocument.KeyFor).
            return [];
        }

        var maxQuery = Math.Max(1, _options.MaxQueryCharacters);
        var text = query.Length <= maxQuery ? query : query[..maxQuery];
        var size = Math.Clamp(top, 1, MaxTop);

        var budget = TimeSpan.FromSeconds(Math.Max(0.01, _options.QueryTimeoutSeconds));
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        try
        {
            var vector = await EmbedAsync(text, timeout.Token).ConfigureAwait(false);
            var request = new PossessionQuery(FilterFor(matchId), text, vector, _options.SemanticConfigurationName, size);
            IReadOnlyList<ScoredDocument> found;
            try
            {
                found = await _transport.QueryAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (
                request.SemanticConfiguration is not null
                && ex.Status is (int)HttpStatusCode.TooManyRequests or (int)HttpStatusCode.ServiceUnavailable
                && clock.Elapsed < budget * (1 - _minRetryBudgetFraction))
            {
                _metrics.RecordFallback(SearchMetrics.SemanticUnavailable);
                found = await _transport.QueryAsync(request with { SemanticConfiguration = null }, timeout.Token).ConfigureAwait(false);
            }

            return Map(found, size);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _metrics.RecordFallback(SearchMetrics.Timeout);
            LogTimeout(_options.QueryTimeoutSeconds);
            return [];
        }
        catch (Exception ex)
        {
            _metrics.RecordFallback(SearchMetrics.SearchUnavailable);
            LogUnavailable(ex);
            return [];
        }
    }

    /// <summary>The OData filter that scopes a query to one match; single quotes are doubled.</summary>
    internal static string FilterFor(string matchId) =>
        $"matchId eq '{matchId.Replace("'", "''", StringComparison.Ordinal)}'";

    // The match ID comes from a route, so it's untrusted. Only the characters a document key holds
    // (PossessionDocument.KeyFor) are accepted.
    private static bool IsSafeMatchId(string? matchId) =>
        !string.IsNullOrEmpty(matchId)
        && matchId.Length <= _maxMatchIdLength
        && matchId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '=');

    private async Task<float[]?> EmbedAsync(string text, CancellationToken token)
    {
        if (_embeddings is null)
        {
            // Configured that way: logged once at construction, not counted per query.
            return null;
        }

        try
        {
            using var sub = CancellationTokenSource.CreateLinkedTokenSource(token);
            sub.CancelAfter(_options.EmbeddingTimeout);
            var result = await _embeddings.GenerateAsync([text], new EmbeddingGenerationOptions { ModelId = _options.EmbeddingDeployment }, sub.Token).ConfigureAwait(false);
            return result.Count == 0 ? throw new InvalidOperationException("The embedding call returned no vectors.") : result[0].Vector.ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.RecordFallback(SearchMetrics.EmbeddingFailed);
            LogEmbeddingFailed(ex);
            return null;
        }
    }

    /// <remarks>
    /// <see cref="PossessionHit.Score"/> is the semantic reranker score (0 to 4) when the ranker ran,
    /// else the hybrid RRF score. It is only comparable within one result list, never across queries
    /// or across the two ranking modes.
    /// </remarks>
    private List<PossessionHit> Map(IReadOnlyList<ScoredDocument> found, int size)
    {
        var hits = new List<PossessionHit>(Math.Min(found.Count, size));
        foreach (var item in found)
        {
            if (hits.Count == size)
            {
                break;
            }

            try
            {
                hits.Add(item.Document.ToHit(item.Score));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                // One unreadable document must not cost the fan the others.
                LogBadDocument(item.Document.Id, ex);
            }
        }

        return hits;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No query embedder is configured (Squad:FoundryEndpoint); possession search runs keyword only.")]
    private partial void LogNoEmbedder();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Possession search timed out after {Seconds}s; returning no results.")]
    private partial void LogTimeout(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Possession search is unavailable; returning no results.")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding the search query failed; falling back to keyword search.")]
    private partial void LogEmbeddingFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unreadable possession document {Id}.")]
    private partial void LogBadDocument(string id, Exception exception);
}
