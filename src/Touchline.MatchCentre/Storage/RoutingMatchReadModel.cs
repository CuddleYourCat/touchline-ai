using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Touchline.Domain.Analytics;
using Touchline.Domain.Season;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// The read model behind the API (ADR-036): <c>demo-*</c> IDs from the recording, every other ID
/// from Cosmos DB. The match list merges both, live matches first and then the newest, and is the
/// demo's alone while Cosmos DB is down.
/// </summary>
/// <param name="demo">The recorded demo match.</param>
/// <param name="cosmos">The Cosmos DB read side, or null when it isn't configured.</param>
/// <param name="logger">Logs a Cosmos DB failure.</param>
internal sealed partial class RoutingMatchReadModel(
    IMatchReadModel demo,
    IMatchReadModel? cosmos,
    ILogger<RoutingMatchReadModel> logger) : IMatchReadModel
{
    /// <summary>The counter of responses served without Cosmos DB, while it's down.</summary>
    public const string DegradedCounter = "touchline.matchcentre.source.degraded";

    // The Match Centre's existing meter, already exported through the Touchline.* registration.
    private static readonly Meter _meter = new(ChangeFeedReader.MeterName);
    private static readonly Counter<long> _degraded = _meter.CreateCounter<long>(DegradedCounter, description: "Responses served without Cosmos DB, while it was down");

    /// <inheritdoc />
    public async Task<MatchList> ListMatchesAsync(MatchStatus? status, string? season, CancellationToken cancellationToken)
    {
        var demoMatches = await demo.ListMatchesAsync(status, season, cancellationToken);
        if (cosmos is null)
        {
            return demoMatches;
        }

        MatchList stored;
        try
        {
            stored = await cosmos.ListMatchesAsync(status, season, cancellationToken);
        }
        catch (Exception exception) when (MatchSources.IsOutage(exception, cancellationToken))
        {
            // The list is still useful without the stored matches: the demo carries on. The
            // counter makes the degraded list visible to operators, and the Degraded flag
            // becomes the Touchline-Degraded header for clients.
            LogListDegraded(logger, exception);
            _degraded.Add(1, new KeyValuePair<string, object?>("operation", "list"));
            return demoMatches with { Degraded = true };
        }

        return new MatchList(
            [.. demoMatches.Matches.Concat(stored.Matches)
                .OrderByDescending(match => match.Status == MatchStatus.Live)
                .ThenByDescending(match => match.KickOffUtc)],
            demoMatches.Degraded || stored.Degraded);
    }

    /// <inheritdoc />
    public Task<MatchDetail?> GetMatchAsync(string matchId, CancellationToken cancellationToken) =>
        Route(matchId, source => source.GetMatchAsync(matchId, cancellationToken), $"match '{matchId}'", cancellationToken);

    /// <inheritdoc />
    public Task<EventPage?> GetEventsAsync(string matchId, long after, int limit, CancellationToken cancellationToken) =>
        Route(matchId, source => source.GetEventsAsync(matchId, after, limit, cancellationToken), $"the events of '{matchId}'", cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MomentView>?> GetMomentsAsync(string matchId, Viewer viewer, CancellationToken cancellationToken) =>
        Route(matchId, source => source.GetMomentsAsync(matchId, viewer, cancellationToken), $"the moments of '{matchId}'", cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MomentumReading>?> GetMomentumAsync(string matchId, CancellationToken cancellationToken) =>
        Route(matchId, source => source.GetMomentumAsync(matchId, cancellationToken), $"the momentum of '{matchId}'", cancellationToken);

    /// <inheritdoc />
    /// <remarks>The demo has no season cards, so they come from Cosmos DB whenever it's configured.</remarks>
    public Task<PlayerSeason?> GetPlayerSeasonAsync(string season, string playerId, CancellationToken cancellationToken) =>
        cosmos is null
            ? demo.GetPlayerSeasonAsync(season, playerId, cancellationToken)
            : MatchSources.GuardAsync(() => cosmos.GetPlayerSeasonAsync(season, playerId, cancellationToken), $"the season card of '{playerId}'", logger, cancellationToken);

    private Task<T> Route<T>(string matchId, Func<IMatchReadModel, Task<T>> read, string what, CancellationToken cancellationToken) =>
        MatchSources.ServesFromCosmos(matchId, cosmos is not null)
            ? MatchSources.GuardAsync(() => read(cosmos!), what, logger, cancellationToken)
            : read(demo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cosmos DB couldn't list matches, so the list is the demo's alone")]
    private static partial void LogListDegraded(ILogger logger, Exception exception);
}
