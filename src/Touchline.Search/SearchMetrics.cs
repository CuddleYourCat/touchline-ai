using System.Diagnostics.Metrics;

namespace Touchline.Search;

/// <summary>
/// The search library's OpenTelemetry instruments (ADR-040, ADR-056). Hosts export them by adding
/// the <see cref="MeterName"/> meter, which the service defaults do.
/// </summary>
public sealed class SearchMetrics
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "Touchline.Search";

    /// <summary>The fallback counter's name.</summary>
    public const string FallbackCounterName = "search.fallback";

    /// <summary>The indexed-documents counter's name.</summary>
    public const string IndexedCounterName = "search.indexed";

    /// <summary>The tag saying why a fallback happened.</summary>
    public const string ReasonTag = "reason";

    /// <summary>The query embedding failed, so the query ran keyword only.</summary>
    public const string EmbeddingFailed = "embedding_failed";

    /// <summary>The semantic ranker returned 429 or 503, so the query ran hybrid without it.</summary>
    public const string SemanticUnavailable = "semantic_unavailable";

    /// <summary>Search was down, so the query returned an empty list.</summary>
    public const string SearchUnavailable = "search_unavailable";

    /// <summary>The query ran past its timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The summary model call failed, so the template text was indexed.</summary>
    public const string SummaryModelFailed = "summary_model_failed";

    /// <summary>The roster guardrail rejected the model summary, so the template text was indexed.</summary>
    public const string SummaryGuardrail = "summary_guardrail";

    /// <summary>The token budget was blown, so the template text was indexed.</summary>
    public const string SummaryBudget = "summary_budget";

    /// <summary>The per-match model summary cap was reached, so the template text was indexed.</summary>
    public const string SummaryCap = "summary_cap";

    /// <summary>The message was a redelivery, so no model summaries were written (ADR-052, ADR-056) and the template text was indexed.</summary>
    public const string SummaryRedelivery = "summary_redelivery";

    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _indexed;

    /// <summary>Creates the search meter from the host's meter factory.</summary>
    /// <param name="meterFactory">The host's meter factory, which owns the meter's lifetime.</param>
    public SearchMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _fallbacks = meter.CreateCounter<long>(
            FallbackCounterName,
            unit: "{fallback}",
            description: "Times search or indexing degraded to a simpler path, by reason.");
        _indexed = meter.CreateCounter<long>(
            IndexedCounterName,
            unit: "{document}",
            description: "Possession documents written to the index.");
    }

    /// <summary>Records one fallback.</summary>
    /// <param name="reason">One of the reason constants on this class.</param>
    public void RecordFallback(string reason) =>
        _fallbacks.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));

    /// <summary>Records several fallbacks of one reason in one measurement, such as every possession kept off the model by the per-match cap.</summary>
    /// <param name="reason">One of the reason constants on this class.</param>
    /// <param name="count">How many possessions fell back; nothing is recorded for zero.</param>
    public void RecordFallback(string reason, int count)
    {
        if (count > 0)
        {
            _fallbacks.Add(count, new KeyValuePair<string, object?>(ReasonTag, reason));
        }
    }

    /// <summary>Records documents written to the index.</summary>
    /// <param name="count">How many.</param>
    public void RecordIndexed(int count) => _indexed.Add(count);
}
