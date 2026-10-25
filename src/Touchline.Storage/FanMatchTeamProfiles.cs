using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Storage;

/// <summary>
/// The <see cref="IMatchTeamProfiles"/> over the <c>match-requests</c> container (ADR-050). A
/// match whose ID isn't a fan match's (<see cref="FanMatchIds"/>) needs no lookup at all. A fan
/// match's request is read once, its snapshots built into profiles, and the result cached for the
/// life of the process: a fan match's roster never changes.
/// </summary>
/// <remarks>
/// Degrades rather than fails: a missing request, a snapshot that breaks the fan-team rules, or
/// Cosmos DB being unavailable answers null (the catalogue alone), logged, and isn't cached, so
/// the next call tries again. The match then shows unnamed players and the roster guardrail sends
/// any line that names one to the template, which is the safe side for the IP rules.
/// </remarks>
public sealed partial class FanMatchTeamProfiles : IMatchTeamProfiles
{
    /// <summary>How many fan matches' profiles are kept. Five a day are allowed (ADR-032), so this is days of them.</summary>
    internal const int Capacity = 64;

    private readonly IFanMatchRequestStore _requests;
    private readonly IReadOnlyDictionary<string, TeamProfile> _catalogue;
    private readonly ILogger<FanMatchTeamProfiles> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, TeamProfile>> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the lookup.</summary>
    /// <param name="requests">The match requests.</param>
    /// <param name="logger">Logs a request that can't be read or built.</param>
    /// <param name="catalogue">The built-in profiles, or null for the embedded catalogue.</param>
    public FanMatchTeamProfiles(IFanMatchRequestStore requests, ILogger<FanMatchTeamProfiles> logger, IReadOnlyDictionary<string, TeamProfile>? catalogue = null)
    {
        _requests = requests;
        _logger = logger;
        _catalogue = catalogue ?? TeamProfileCatalog.LoadEmbedded();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, TeamProfile>?> ForMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        if (!FanMatchIds.IsFanMatch(matchId))
        {
            return null;
        }

        if (_cache.TryGetValue(matchId, out var cached))
        {
            return cached;
        }

        FanMatchRequest? request;
        try
        {
            request = await _requests.GetAsync(matchId, cancellationToken);
        }
        catch (Exception exception) when (IsOutage(exception, cancellationToken))
        {
            LogUnavailable(_logger, exception, matchId);
            return null;
        }

        if (request is null)
        {
            LogMissing(_logger, matchId);
            return null;
        }

        IReadOnlyDictionary<string, TeamProfile> teams;
        try
        {
            teams = request.TeamsWith(_catalogue);
        }
        catch (ArgumentException exception)
        {
            LogUnplayable(_logger, exception, matchId);
            return null;
        }

        // A plain bound rather than an LRU: at five matches a day the cache never fills between
        // deploys, and if it ever does, starting again costs one point read per live match.
        if (_cache.Count >= Capacity)
        {
            _cache.Clear();
        }

        _cache[matchId] = teams;
        return teams;
    }

    private static bool IsOutage(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        CosmosException cosmos => cosmos.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.Forbidden or >= HttpStatusCode.InternalServerError,
        HttpRequestException or TimeoutException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The request for fan match {MatchId} couldn't be read, so its fan players are unnamed for now")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fan match {MatchId} has no request, so its fan players are unnamed")]
    private static partial void LogMissing(ILogger logger, string matchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The request for fan match {MatchId} holds a team that can't be built, so its fan players are unnamed")]
    private static partial void LogUnplayable(ILogger logger, Exception exception, string matchId);
}
