using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;
using Touchline.Search;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Indexes a finished match's possessions once, at full time (ADR-039, ADR-056): reads the stored
/// match and events, cuts and summarises the possessions, lets the small model rewrite the key-moment
/// ones, embeds every summary in one batch, and merges the documents into the index.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The document keys are <c>{matchId}_{possessionId}</c>, so a redelivered message rewrites the
/// same documents.</item>
/// <item>Documents are never indexed without vectors: if the embedding call fails, or returns the
/// wrong number of vectors, nothing is written and the run reports
/// <see cref="PossessionIndexStatus.EmbeddingFailed"/>, so the caller abandons the message.</item>
/// <item>A failed index write throws (a partial batch included), so the message isn't completed.</item>
/// <item>With a <see cref="NullPossessionIndexer"/>, nothing is read or embedded.</item>
/// </list>
/// </remarks>
public sealed partial class PossessionIndexWriter
{
    /// <summary>The length of a <c>text-embedding-3-small</c> vector, which the index schema fixes.</summary>
    public const int VectorDimensions = 1536;

    private readonly IMatchStore _matches;
    private readonly KeyMomentDetector _detector;
    private readonly KeyMomentSummaryWriter _summaries;
    private readonly IPossessionIndexer _indexer;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly IMatchTeamProfiles _teams;
    private readonly SearchMetrics _metrics;
    private readonly PossessionSearchOptions _options;
    private readonly ILogger<PossessionIndexWriter> _logger;

    /// <summary>Creates the writer.</summary>
    /// <param name="matches">The stored matches and events.</param>
    /// <param name="detector">Finds the key moments again from the stored events.</param>
    /// <param name="summaries">Writes the key-moment summaries with the small model.</param>
    /// <param name="indexer">Writes to the index; a <see cref="NullPossessionIndexer"/> switches indexing off.</param>
    /// <param name="teams">The team profiles players are named from, per match: the catalogue, plus a fan match's snapshots (ADR-050).</param>
    /// <param name="metrics">Where indexed documents are counted.</param>
    /// <param name="options">The search settings, including the embedding call's input caps.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="embeddings">The embedding generator; null only when indexing is off.</param>
    public PossessionIndexWriter(
        IMatchStore matches,
        KeyMomentDetector detector,
        KeyMomentSummaryWriter summaries,
        IPossessionIndexer indexer,
        IMatchTeamProfiles teams,
        SearchMetrics metrics,
        IOptions<PossessionSearchOptions> options,
        ILogger<PossessionIndexWriter> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _matches = matches;
        _detector = detector;
        _summaries = summaries;
        _indexer = indexer;
        _embeddings = embeddings;
        _teams = teams;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Indexes a finished match's possessions.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="deliveryCount">The Service Bus delivery count, starting at 1. After the first delivery no model summaries are written (ADR-052, ADR-056).</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>How the run ended.</returns>
    /// <exception cref="PossessionIndexingException">The index rejected some documents.</exception>
    public async Task<PossessionIndexRun> WriteAsync(string matchId, int deliveryCount, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        if (!_indexer.Enabled || _embeddings is null)
        {
            return new PossessionIndexRun(PossessionIndexStatus.Disabled, 0);
        }

        var match = await _matches.GetMatchAsync(matchId, cancellationToken);
        if (match is not { State.FullTime: true })
        {
            return new PossessionIndexRun(PossessionIndexStatus.NotFinished, 0);
        }

        var events = await _matches.GetEventsAsync(matchId, cancellationToken);
        var profiles = await _teams.ForMatchAsync(matchId, cancellationToken);
        var roster = PossessionRoster.From(match.Metadata, profiles);
        var detector = profiles is null ? _detector : _detector.ForTeams(profiles);
        var possessions = PossessionSummarizer.Summarize(matchId, events, detector, roster);
        if (possessions.Count == 0)
        {
            LogNothing(_logger, matchId);
            return new PossessionIndexRun(PossessionIndexStatus.Done, 0);
        }

        if (possessions.Count > _options.MaxEmbeddingDocumentsPerMatch)
        {
            LogTooMany(_logger, matchId, possessions.Count, _options.MaxEmbeddingDocumentsPerMatch);
            possessions = [.. possessions.Take(_options.MaxEmbeddingDocumentsPerMatch)];
        }

        var documents = await _summaries.WriteAsync(possessions, roster, deliveryCount, cancellationToken);

        GeneratedEmbeddings<Embedding<float>> vectors;
        try
        {
            vectors = await _embeddings.GenerateAsync(documents.Select(d => Capped(d.Summary)).ToList(), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogEmbeddingFailed(_logger, ex, matchId);
            return new PossessionIndexRun(PossessionIndexStatus.EmbeddingFailed, 0);
        }

        if (vectors.Count != documents.Count || vectors.Any(v => v.Vector.Length != VectorDimensions))
        {
            LogBadVectors(_logger, matchId, vectors.Count, documents.Count);
            return new PossessionIndexRun(PossessionIndexStatus.EmbeddingFailed, 0);
        }

        var withVectors = documents.Select((d, i) => d with { SummaryVector = vectors[i].Vector.ToArray() }).ToList();
        await _indexer.UpsertAsync(withVectors, cancellationToken);
        _metrics.RecordIndexed(withVectors.Count);
        LogIndexed(_logger, matchId, withVectors.Count);
        return new PossessionIndexRun(PossessionIndexStatus.Done, withVectors.Count);
    }

    // The budget on the embedding call: no text longer than the configured cap is sent.
    private string Capped(string text) => text.Length <= _options.MaxEmbeddingInputChars ? text : text[.._options.MaxEmbeddingInputChars];

    [LoggerMessage(Level = LogLevel.Information, Message = "Match {MatchId} has no possessions with on-ball play, so nothing was indexed")]
    private static partial void LogNothing(ILogger logger, string matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match {MatchId} has {Count} possessions; only the first {Max} are indexed")]
    private static partial void LogTooMany(ILogger logger, string matchId, int count, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding call for match {MatchId} failed; nothing is indexed and the message will be redelivered")]
    private static partial void LogEmbeddingFailed(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding call for match {MatchId} returned {Returned} usable vector(s) for {Expected} document(s); nothing is indexed and the message will be redelivered")]
    private static partial void LogBadVectors(ILogger logger, string matchId, int returned, int expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Indexed {Count} possession document(s) for match {MatchId}")]
    private static partial void LogIndexed(ILogger logger, string matchId, int count);
}

/// <summary>How a possession-indexing run ended.</summary>
public enum PossessionIndexStatus
{
    /// <summary>The documents were written, or the match had none.</summary>
    Done,

    /// <summary>The match isn't stored, or isn't at full time.</summary>
    NotFinished,

    /// <summary>Indexing is off (Search isn't configured), so nothing was read or written.</summary>
    Disabled,

    /// <summary>The embedding call failed, so nothing was written.</summary>
    EmbeddingFailed
}

/// <summary>What one possession-indexing run did.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Indexed">Documents written to the index.</param>
public sealed record PossessionIndexRun(PossessionIndexStatus Status, int Indexed);
