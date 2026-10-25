using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Touchline.Agents;
using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Teams;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// Builds the <see cref="MatchContext"/> the commentary workflow needs from the stored match
/// document: the names the roster guardrail allows (ADR-044) and the score after the moment.
/// </summary>
public static partial class MatchContexts
{
    private static readonly Lazy<IReadOnlyDictionary<string, TeamProfile>> _catalog = new(TeamProfileCatalog.LoadEmbedded);

    // Team IDs already reported as unknown, so each is logged once per process, not per moment.
    private static readonly ConcurrentDictionary<string, bool> _reportedUnknownTeams = new(StringComparer.Ordinal);

    /// <summary>
    /// The context for one key moment. A roster is the team profile's whole squad, so a
    /// substitute who comes on is a name the guardrail allows: the team sheet carries only the
    /// starting eleven, and the squad is the smallest list that is always right. A fan team's
    /// squad comes from its match request's snapshot (ADR-050), passed in as <paramref name="teams"/>.
    /// A team none of the profiles know (a fan match whose request can't be read) gets an empty
    /// roster, logged once, so a line naming any of its players falls back to the template rather
    /// than handing the model raw player IDs.
    /// </summary>
    /// <param name="moment">The key moment, which carries the score after it when the processor published it.</param>
    /// <param name="match">The stored match document, or null if it hasn't been written yet.</param>
    /// <param name="teams">The team profiles to name players from; the embedded catalogue if null.</param>
    /// <param name="logger">Where an unknown team is reported; nothing is logged if null.</param>
    /// <returns>
    /// The context. Without metadata the rosters are empty, so any named player fails the guardrail
    /// and the template is used. Without a score on the moment the score is null (unknown): the
    /// stored state may be later than the moment, so it is never used instead.
    /// </returns>
    public static MatchContext For(
        KeyMoment moment,
        MatchRecord? match,
        IReadOnlyDictionary<string, TeamProfile>? teams = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(moment);
        teams ??= _catalog.Value;

        var matchId = moment.Trigger.MatchId;
        if (match?.Metadata is not { } metadata)
        {
            return new MatchContext(matchId, [], [], [], moment.HomeScore, moment.AwayScore);
        }

        string[] otherNames =
        [
            metadata.Home.Name, metadata.Home.ShortName,
            metadata.Away.Name, metadata.Away.ShortName,
            metadata.Venue, metadata.Referee.Name
        ];

        return new MatchContext(
            matchId,
            Names(metadata.Home, teams, logger),
            Names(metadata.Away, teams, logger),
            [.. otherNames.Distinct(StringComparer.Ordinal)],
            moment.HomeScore,
            moment.AwayScore);
    }

    private static string[] Names(TeamSheet sheet, IReadOnlyDictionary<string, TeamProfile> teams, ILogger? logger)
    {
        if (teams.GetValueOrDefault(sheet.Id) is { } profile)
        {
            return [.. profile.Squad.Select(player => player.Name)];
        }

        if (logger is not null && _reportedUnknownTeams.TryAdd(sheet.Id, true))
        {
            LogUnknownTeam(logger, sheet.Id);
        }

        return [];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Team {TeamId} is not in the team catalogue, so its roster is empty and lines naming its players fall back to the template")]
    private static partial void LogUnknownTeam(ILogger logger, string teamId);
}
