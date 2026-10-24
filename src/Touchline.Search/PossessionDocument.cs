using System.Globalization;
using System.Text.Json.Serialization;
using Touchline.Domain.Matches;
using Touchline.Domain.Squad;

namespace Touchline.Search;

/// <summary>
/// One possession sequence as stored in the Azure AI Search <c>possessions</c> index (ADR-008,
/// ADR-040). The JSON names match <c>infra/search/possessions-index.json</c> exactly; a unit test
/// compares the two so the schema and this class cannot drift.
/// </summary>
public sealed record PossessionDocument
{
    private const int _secondHalfOffsetSeconds = 3600;

    /// <summary>The document key, from <see cref="KeyFor"/>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The match, used as the filter on every query.</summary>
    [JsonPropertyName("matchId")]
    public required string MatchId { get; init; }

    /// <summary>The simulator's possession ID within the match, as text (the index field is a string).</summary>
    [JsonPropertyName("possessionId")]
    public required string PossessionId { get; init; }

    /// <summary>The team in possession: <c>Home</c> or <c>Away</c>.</summary>
    [JsonPropertyName("team")]
    public required string Team { get; init; }

    /// <summary>When the possession started, as on-screen clock text such as <c>63:12</c>.</summary>
    [JsonPropertyName("fromClock")]
    public required string FromClock { get; init; }

    /// <summary>When the possession ended, as on-screen clock text.</summary>
    [JsonPropertyName("toClock")]
    public required string ToClock { get; init; }

    /// <summary>
    /// A sortable start time that tells the halves apart: the clock in seconds for the first half,
    /// plus 3,600 for the second (see <see cref="SecondsFor"/>).
    /// </summary>
    [JsonPropertyName("fromSeconds")]
    public required int FromSeconds { get; init; }

    /// <summary>The distinct roster names involved.</summary>
    [JsonPropertyName("players")]
    public required IReadOnlyList<string> Players { get; init; }

    /// <summary>How the possession ended, such as <c>goal</c>, <c>turnover</c> or <c>out</c>.</summary>
    [JsonPropertyName("endedIn")]
    public required string EndedIn { get; init; }

    /// <summary>The text summary, for full-text search and for embedding.</summary>
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>
    /// The 1,536-dimension <c>text-embedding-3-small</c> vector of <see cref="Summary"/>. A
    /// <c>float[]</c> because the Search SDK serialises it as a plain JSON array; null until embedded.
    /// The field isn't retrievable, so documents read back have it null.
    /// </summary>
    [JsonPropertyName("summaryVector")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? SummaryVector { get; init; }

    /// <summary>
    /// The document key for a possession: <c>{matchId}_{possessionId}</c>. Azure AI Search keys allow
    /// only letters, digits, <c>_</c>, <c>-</c> and <c>=</c>, so ADR-039's <c>{matchId}:{possessionId}</c>
    /// is written with an underscore (ADR-056). The possession ID is a number, so the last underscore
    /// always splits the key unambiguously.
    /// </summary>
    /// <param name="matchId">The match; letters, digits, <c>-</c>, <c>_</c> or <c>=</c> only.</param>
    /// <param name="possessionId">The possession within the match; not negative.</param>
    /// <returns>For example <c>demo-3_17</c>.</returns>
    /// <exception cref="ArgumentException">The match ID is empty or has a character a key can't hold.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The possession ID is negative.</exception>
    public static string KeyFor(string matchId, int possessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(matchId);
        ArgumentOutOfRangeException.ThrowIfNegative(possessionId);
        foreach (var c in matchId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '='))
            {
                throw new ArgumentException($"Match ID '{matchId}' has a character an Azure AI Search key can't hold.", nameof(matchId));
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{matchId}_{possessionId}");
    }

    /// <summary>The sortable <see cref="FromSeconds"/> value for a match time.</summary>
    /// <param name="time">The match time.</param>
    /// <returns>Whole seconds of the clock, plus 3,600 in the second half.</returns>
    public static int SecondsFor(MatchTime time) =>
        (int)time.Clock.TotalSeconds + (time.Period == MatchPeriod.SecondHalf ? _secondHalfOffsetSeconds : 0);

    /// <summary>Maps a stored document to the search port's result type.</summary>
    /// <param name="score">The search relevance score for this document.</param>
    /// <returns>The hit.</returns>
    /// <exception cref="FormatException">A stored clock or possession ID can't be read.</exception>
    public PossessionHit ToHit(double score)
    {
        var period = FromSeconds >= _secondHalfOffsetSeconds ? MatchPeriod.SecondHalf : MatchPeriod.FirstHalf;
        return new PossessionHit(
            Id,
            MatchId,
            int.Parse(PossessionId, CultureInfo.InvariantCulture),
            Enum.Parse<TeamSide>(Team),
            ParseClock(FromClock, period),
            ParseClock(ToClock, period),
            Summary,
            score);
    }

    private static MatchTime ParseClock(string text, MatchPeriod period)
    {
        var parts = text.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new FormatException($"'{text}' isn't a mm:ss match clock.");
        }

        return MatchTime.At(period, minutes, seconds);
    }
}
