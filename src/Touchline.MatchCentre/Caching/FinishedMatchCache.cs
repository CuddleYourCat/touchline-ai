using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Storage;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// How long finished-match reads are cached (ADR-061). Bound from <c>MatchCentre:Caching</c>.
/// </summary>
public sealed class FinishedMatchCacheOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:Caching";

    /// <summary>
    /// How long a finished match, its moments or its recap is cached. A finished match's events
    /// never change, and only lines the model wrote in the viewer's own language and mode are
    /// cached, so this mostly bounds memory and a late correction.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan FinishedMatchTtl { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>The reads the finished-match cache covers.</summary>
internal enum FinishedMatchRead
{
    /// <summary><c>GET /api/matches/{matchId}</c>.</summary>
    Detail,

    /// <summary><c>GET /api/matches/{matchId}/moments</c>.</summary>
    Moments,

    /// <summary><c>GET /api/matches/{matchId}/recap</c>.</summary>
    Recap,
}

/// <summary>
/// The output cache for finished matches (ADR-061). A response is stored only when the endpoint
/// filter has proved it's for a finished match and final for this viewer (<see cref="Allow"/>);
/// everything else, live matches above all, is served fresh every time. The list, the SSE stream
/// and every write are never cached.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A match: when its state is full time.</item>
/// <item>Its moments: when the match was at full time <i>before</i> the moments were read, so a
/// match finishing mid-request can't store a half-played list, and every key moment's line is the
/// model's, in exactly the viewer's language and mode. A template or fallback-language line may
/// still be replaced, so it is served fresh.</item>
/// <item>Its recap: when it's the model's, in exactly the viewer's language and mode, never the
/// template or another language's recap served while the right one is still being written.</item>
/// <item>Never a demo match: each replica replays the recording on its own clock, so a demo lap
/// that has finished on one replica may still be live on another.</item>
/// </list>
/// The viewer's query parameters (<c>mode</c>, <c>lang</c>, <c>team</c>, <c>player</c>,
/// <c>metric</c>) vary the moments and the recap.
/// </remarks>
internal static class FinishedMatchCache
{
    /// <summary>The viewer settings that change the words on the moments and the recap (<see cref="ViewerQuery"/>).</summary>
    public static readonly string[] ViewerKeys = ["mode", "lang", "team", "player", "metric"];

    private static readonly object _allowed = new();

    /// <summary>Caches this endpoint's responses for finished matches only.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="read">Which read it is.</param>
    /// <returns>The endpoint, for chaining.</returns>
    public static RouteHandlerBuilder CacheWhenFinished(this RouteHandlerBuilder builder, FinishedMatchRead read) =>
        builder.CacheOutput(new FinishedMatchCachePolicy(read)).AddEndpointFilter(new FinishedMatchCacheFilter(read));

    /// <summary>Marks the response as one for a finished match, so it may be stored.</summary>
    /// <param name="context">The request.</param>
    public static void Allow(HttpContext context) => context.Items[_allowed] = true;

    /// <summary>Whether the response was marked as one for a finished match.</summary>
    /// <param name="context">The request.</param>
    /// <returns>True when it may be stored.</returns>
    public static bool IsAllowed(HttpContext context) => context.Items.ContainsKey(_allowed);

    /// <summary>The mode and language a request asked for, as <see cref="ViewerQuery"/> resolves them.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The mode (casual by default) and the language tag (<c>en</c> by default).</returns>
    public static (ViewingMode Mode, string Language) Requested(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lang = request.Query["lang"].ToString();
        return (EnumNames.Parse<ViewingMode>(request.Query["mode"].ToString()) ?? ViewingMode.Casual,
            string.IsNullOrWhiteSpace(lang) ? Viewer.Default.Language : lang);
    }

    /// <summary>
    /// Whether a handler's result is for a finished match, and final for the viewer who asked. A
    /// pure rule, so it's tested alone.
    /// </summary>
    /// <param name="read">Which read it was.</param>
    /// <param name="result">What the handler returned.</param>
    /// <param name="finishedBefore">For the moments, whether the match was finished before they were read.</param>
    /// <param name="mode">The mode the viewer asked for.</param>
    /// <param name="language">The language tag the viewer asked for.</param>
    /// <returns>True when it may be cached.</returns>
    public static bool IsFinished(FinishedMatchRead read, object? result, bool finishedBefore, ViewingMode mode, string language) =>
        (read, Unwrap(result)) switch
        {
            (FinishedMatchRead.Detail, Ok<MatchDetail> match) =>
                match.Value is { State.FullTime: true } detail && !MatchSources.IsDemo(detail.Summary.Id),
            (FinishedMatchRead.Moments, Ok<IReadOnlyList<MomentView>> moments) =>
                finishedBefore && moments.Value is { } views
                && views.All(m => m.Tier != MomentTier.Key || IsFinal(m.Commentary, mode, language)),
            (FinishedMatchRead.Recap, Ok<MatchRecap> recap) =>
                recap.Value is { Source: CommentarySource.Model } written && !MatchSources.IsDemo(written.MatchId)
                && written.Mode == mode && SameTag(written.Language, language),
            _ => false,
        };

    // A line nothing will replace for this viewer: the model's, in their exact language and mode.
    private static bool IsFinal(CommentaryLine? line, ViewingMode mode, string language) =>
        line is { Source: CommentarySource.Model } && line.Mode == mode && SameTag(line.Language, language);

    // The exact tag, ignoring only case (the store keeps one casing): es-MX is not es.
    private static bool SameTag(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static object? Unwrap(object? result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result;
    }
}

/// <summary>
/// Decides, after the handler has run, whether its response is for a finished match, and marks it
/// so the policy lets the output cache store it.
/// </summary>
/// <param name="read">Which read this endpoint is.</param>
internal sealed class FinishedMatchCacheFilter(FinishedMatchRead read) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;

        // The moments don't say whether the match is over, so that's read first: a match that
        // finishes after this check simply isn't cached this time.
        var finishedBefore = read == FinishedMatchRead.Moments && await IsFinishedAsync(http);

        var result = await next(context);
        var (mode, language) = FinishedMatchCache.Requested(http.Request);
        if (FinishedMatchCache.IsFinished(read, result, finishedBefore, mode, language))
        {
            FinishedMatchCache.Allow(http);
        }

        return result;
    }

    private static async Task<bool> IsFinishedAsync(HttpContext http)
    {
        if (http.GetRouteValue("matchId") is not string matchId || MatchSources.IsDemo(matchId))
        {
            return false;
        }

        try
        {
            var matches = http.RequestServices.GetRequiredService<IMatchReadModel>();
            return await matches.GetMatchAsync(matchId, http.RequestAborted) is { State.FullTime: true };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The handler makes the same read and reports the failure; here it just means "don't cache".
            return false;
        }
    }
}

/// <summary>
/// The output cache policy for one finished-match read: GET only, keyed by path and the viewer's
/// query parameters, for <see cref="FinishedMatchCacheOptions.FinishedMatchTtl"/>. Storage is
/// allowed only for a <c>200</c> that <see cref="FinishedMatchCacheFilter"/> marked as finished.
/// </summary>
/// <param name="read">Which read this endpoint is.</param>
internal sealed class FinishedMatchCachePolicy(FinishedMatchRead read) : IOutputCachePolicy
{
    /// <inheritdoc />
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.HttpContext.Request;
        var attempt = HttpMethods.IsGet(request.Method) && !request.Headers.ContainsKey("Authorization");

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = attempt;
        context.AllowCacheStorage = attempt;
        context.AllowLocking = true;
        context.CacheVaryByRules.QueryKeys = read == FinishedMatchRead.Detail ? default : FinishedMatchCache.ViewerKeys;
        context.ResponseExpirationTimeSpan = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<FinishedMatchCacheOptions>>().Value.FinishedMatchTtl;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK
            || response.Headers.SetCookie.Count > 0
            || !FinishedMatchCache.IsAllowed(context.HttpContext))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
