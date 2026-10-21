using Microsoft.Extensions.Logging;
using Touchline.Domain.Matches;
using Touchline.Domain.Season;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Projects finished fixtures into the season (ADR-025): each starter's part in the fixture,
/// then each of those players' rebuilt season, with their bans. Everything is written with
/// upserts under deterministic IDs and rebuilt from the stored fixtures, so a redelivered or
/// re-projected fixture changes nothing.
/// </summary>
/// <param name="matches">Where the fixture's events are read from.</param>
/// <param name="season">Where the projection is written.</param>
/// <param name="telemetry">The processor's counters.</param>
/// <param name="logger">What was projected.</param>
public sealed partial class SeasonProjector(
    IMatchStore matches,
    ISeasonStore season,
    PipelineTelemetry telemetry,
    ILogger<SeasonProjector> logger)
{
    // As for the store's writes: enough to overlap round trips, well inside the RU burst.
    private const int _maxConcurrentRebuilds = 4;

    /// <summary>
    /// Projects each changed match document that is a finished fixture, and ignores the rest.
    /// The change feed delivers a <c>matches</c> document on every batch, about 1,400 times a
    /// match, so almost every call ignores everything.
    /// </summary>
    /// <param name="changed">The changed match documents, latest version of each.</param>
    /// <param name="cancellationToken">Stops the projection; the change feed redelivers.</param>
    /// <returns>A task that completes once every finished fixture is projected.</returns>
    public async Task ProjectAsync(IReadOnlyList<MatchRecord> changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);

        foreach (var record in changed)
        {
            if (record is { State.FullTime: true, Metadata: { Kind: MatchKind.Fixture, Season: { } fixtureSeason } metadata })
            {
                await ProjectFixtureAsync(record.MatchId, metadata, fixtureSeason, cancellationToken);
            }
        }
    }

    private async Task ProjectFixtureAsync(string matchId, MatchMetadata metadata, string fixtureSeason, CancellationToken cancellationToken)
    {
        using var activity = PipelineTelemetry.Source.StartActivity("project fixture");
        activity?.SetTag("match.id", matchId);

        var events = await matches.GetEventsAsync(matchId, cancellationToken);
        var players = PlayerMatch.FromFixture(metadata, events);
        await season.UpsertPlayerMatchesAsync(players, cancellationToken);

        // Each player's season is rebuilt from all their fixtures, including this one. Every player
        // is independent (their own partition to read, one document to write), so a few at a time.
        var banned = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentRebuilds, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(players, parallel, async (player, token) =>
        {
            var fixtures = await season.GetPlayerMatchesAsync(fixtureSeason, player.PlayerId, token);
            var rebuilt = PlayerSeason.Rebuild(fixtures);
            await season.UpsertPlayerSeasonAsync(rebuilt, token);
            if (rebuilt.IsBannedFor(player.Round + 1))
            {
                Interlocked.Increment(ref banned);
            }
        });

        telemetry.FixtureProjected();
        LogProjected(logger, matchId, fixtureSeason, metadata.Round ?? 0, players.Count, banned);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Projected {MatchId} into {Season} round {Round}: {Players} players, {Banned} banned for the next round")]
    private static partial void LogProjected(ILogger logger, string matchId, string season, int round, int players, int banned);
}
