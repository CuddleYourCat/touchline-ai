using System.ComponentModel.DataAnnotations;


using Azure;
using Azure.AI.ContentSafety;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Settings for the Content Safety name guard. Bound from <c>MatchCentre:ContentSafety</c>.
/// </summary>
public sealed class ContentSafetyOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:ContentSafety";

    /// <summary>
    /// The endpoint of the Foundry (AI Services) account, such as
    /// <c>https://ais-touchline-x.cognitiveservices.azure.com/</c>. Empty means the guard isn't
    /// configured and the demo's format-only guard is used.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>The custom blocklist of real clubs, nicknames, grounds, abbreviations and players.</summary>
    [Required]
    [RegularExpression("^[0-9A-Za-z._~-]{1,64}$")]
    public string BlocklistName { get; set; } = "touchline-real-names";

    /// <summary>How long one call may take before the guard counts as unavailable.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a definite verdict is remembered for the same kind and name.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan VerdictCacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a replica keeps a verdict in its own memory (ADR-061). Short, because an entry
    /// refilled from Redis starts a fresh local lifetime: the worst case is
    /// <see cref="VerdictCacheTtl"/> plus this. Never more than <see cref="VerdictCacheTtl"/>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan VerdictLocalCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most calls in flight at once for one request. A name check carries up to 30 names.</summary>
    [Range(1, 16)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>The lowest harm severity (0, 2, 4 or 6) that refuses a name. Four is "medium".</summary>
    [Range(0, 6)]
    public int MinBlockedSeverity { get; set; } = 4;
}

/// <summary>
/// The guard couldn't reach a verdict because Content Safety was unavailable: unreachable,
/// throttled, timed out or misconfigured. The API answers with a <c>503</c> and stores nothing
/// (ADR-049).
/// </summary>
/// <param name="message">What went wrong.</param>
/// <param name="innerException">The underlying failure, or null.</param>
public sealed class NameGuardUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Checks names with Azure AI Content Safety text analysis (ADR-049): a custom blocklist of real
/// names, then the harm categories. It fails closed: if Content Safety can't answer, it throws
/// <see cref="NameGuardUnavailableException"/> rather than let a name through unchecked.
/// </summary>
/// <remarks>
/// Names are normalised first (see <see cref="NameNormaliser"/>), and verdicts are cached per normalised text, so the same text typed twice, or as a team and a player, costs one call.
/// Verdicts go through <see cref="HybridCache"/>: this replica's memory first, then Redis when it's configured (ADR-061), so every replica
/// sees a verdict at once. Only definite verdicts are cached; a failure throws, and HybridCache never stores a factory that threw. A request's names are checked with
/// bounded concurrency, one call per distinct name.
/// </remarks>
internal sealed partial class ContentSafetyNameGuard(
    ContentSafetyClient client,
    HybridCache cache,
    IOptions<ContentSafetyOptions> options,
    ILogger<ContentSafetyNameGuard> logger) : INameGuard
{
    /// <summary>What a fan sees when a name matched the blocklist. Says why, without quoting the list.</summary>
    public const string RealNameReason = "That looks like a real club, ground or person. Choose a fictional name.";

    /// <summary>What a fan sees when a name is flagged as abusive. Deliberately says no more.</summary>
    public const string NotAllowedReason = "That name isn't allowed. Try a different one.";

    private const string _cachePrefix = "name-guard|";

    private readonly ContentSafetyOptions _options = options.Value;

    /// <inheritdoc />
    /// <exception cref="NameGuardUnavailableException">Content Safety couldn't give a verdict.</exception>
    public async Task<IReadOnlyList<NameVerdict>> CheckAsync(IReadOnlyList<NameToCheck> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var distinct = names.Select(n => NameNormaliser.Normalise(n.Name)).Distinct().ToList();
        var decisions = new Dictionary<string, Decision>(StringComparer.Ordinal);
        var gate = new Lock();

        await Parallel.ForEachAsync(
            distinct,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = cancellationToken },
            async (key, token) =>
            {
                var decision = await DecideAsync(key, token);
                lock (gate)
                {
                    decisions[key] = decision;
                }
            });

        return
        [
            .. names.Select(n =>
            {
                var decision = decisions[NameNormaliser.Normalise(n.Name)];
                return new NameVerdict(n.Kind, n.Name, decision.Allowed, decision.Reason);
            })
        ];
    }

    // The normalised text is both the cache key and what is sent. The analysis doesn't depend on
    // the kind of name, so a team and a player of the same text share one call and one verdict.
    private async Task<Decision> DecideAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return new Decision(false, NotAllowedReason);
        }

        if (_options.VerdictCacheTtl <= TimeSpan.Zero)
        {
            return await AnalyseAsync(text, cancellationToken);
        }

        // Concurrent checks of the same text share one call. A call that throws stores nothing,
        // so an outage is never remembered and a recovered service is used at once (ADR-049).
        var lifetime = new HybridCacheEntryOptions
        {
            Expiration = _options.VerdictCacheTtl,
            LocalCacheExpiration = _options.VerdictLocalCacheTtl < _options.VerdictCacheTtl ? _options.VerdictLocalCacheTtl : _options.VerdictCacheTtl,
        };
        return await cache.GetOrCreateAsync(
            _cachePrefix + text,
            (Guard: this, Text: text),
            static (state, token) => new ValueTask<Decision>(state.Guard.AnalyseAsync(state.Text, token)),
            lifetime,
            cancellationToken: cancellationToken);
    }

    private async Task<Decision> AnalyseAsync(string text, CancellationToken cancellationToken)
    {
        var request = new AnalyzeTextOptions(text)
        {
            BlocklistNames = { _options.BlocklistName },
            HaltOnBlocklistHit = true,
            OutputType = AnalyzeTextOutputType.FourSeverityLevels,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            var result = (await client.AnalyzeTextAsync(request, timeout.Token)).Value;
            if (result.BlocklistsMatch is { Count: > 0 })
            {
                return new Decision(false, RealNameReason);
            }

            return result.CategoriesAnalysis.Any(c => (c.Severity ?? 0) >= _options.MinBlockedSeverity)
                ? new Decision(false, NotAllowedReason)
                : new Decision(true, null);
        }
        catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnavailable(logger, exception);
            throw new NameGuardUnavailableException("Content Safety couldn't check the name.", exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Content Safety couldn't check a name, so the request is refused")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);

    /// <summary>A verdict as cached: serialised to JSON when Redis holds it.</summary>
    /// <param name="Allowed">Whether the name may be used.</param>
    /// <param name="Reason">Why not, for the fan.</param>
    internal sealed record Decision(bool Allowed, string? Reason);
}
