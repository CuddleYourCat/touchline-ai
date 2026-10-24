using Touchline.Agents;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Storage;
using Touchline.Storage;

namespace Touchline.MatchCentre.Squad;

/// <summary>
/// Serves a finished match's recap without writing anything (ADR-030, amended 23 Oct). The Event
/// Processor stores the recaps at full time; the Match Centre only reads them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A demo match, or a host with no recap store, gets the squad's recap: the demo's template.</item>
/// <item>Any other match gets the stored recap for the viewer's mode and language, falling back
/// from a regional tag to its base language, then to English.</item>
/// <item>With nothing stored yet, or the store unavailable, it gets the template recap, built from
/// the match's facts, so a fan never sees an error for a missing recap.</item>
/// </list>
/// </remarks>
/// <param name="squad">The squad, whose recap serves the demo matches.</param>
/// <param name="facts">The match facts the template is built from.</param>
/// <param name="logger">Logs a store failure.</param>
/// <param name="store">The stored recaps, or null when Cosmos DB isn't configured.</param>
internal sealed partial class RecapReader(ISquad squad, IMatchFacts facts, ILogger<RecapReader> logger, IRecapStore? store = null)
{
    /// <summary>Reads the recap for a match at full time.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="viewer">Who's reading.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored recap, or the template.</returns>
    /// <exception cref="KeyNotFoundException">There's no such match.</exception>
    public async Task<MatchRecap> GetAsync(string matchId, Viewer viewer, CancellationToken cancellationToken)
    {
        if (store is null || MatchSources.IsDemo(matchId))
        {
            return await squad.RecapAsync(matchId, viewer, cancellationToken);
        }

        try
        {
            foreach (var language in Languages(viewer.Language))
            {
                if (await store.GetAsync(matchId, viewer.Mode, language, cancellationToken) is { } stored)
                {
                    // Only the model's recaps are stored, and ones stored before Source existed read it as the
                    // enum's default (Template), so a stored recap is always marked as the model's.
                    return stored with { Source = CommentarySource.Model };
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogStoreFailed(logger, ex, matchId);
        }

        var sheet = await facts.GetSummaryAsync(matchId, cancellationToken)
            ?? throw new KeyNotFoundException($"There's no match '{matchId}'.");
        var moments = await facts.GetKeyMomentsAsync(matchId, cancellationToken);
        return ProducerRecap.Template(sheet, moments, viewer);
    }

    /// <summary>The languages to look for, best first: the tag, its base language, then English.</summary>
    /// <param name="language">The viewer's tag, such as <c>es-MX</c>.</param>
    /// <returns>For example <c>es-MX</c>, <c>es</c>, <c>en</c>.</returns>
    /// <remarks>
    /// Each candidate is normalised as the store normalises its IDs, before the duplicates are
    /// dropped, so no two candidates read the same document.
    /// </remarks>
    internal static IEnumerable<string> Languages(string language) =>
        new[] { language, language.Split('-')[0], "en" }.Select(CosmosCommentaryStore.NormaliseLanguage).Distinct(StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recap store couldn't serve match {MatchId}; serving the template recap")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception, string matchId);
}
