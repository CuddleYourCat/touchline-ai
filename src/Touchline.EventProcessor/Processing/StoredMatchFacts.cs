using Touchline.Domain.Matches;
using Touchline.Domain.Moments;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;

namespace Touchline.EventProcessor.Processing;

/// <summary>
/// The Producer's read-only view of one finished match in the processor (ADR-030, amended 23 Oct):
/// a snapshot built once from the stored match document and its events, so writing six recaps
/// reads Cosmos DB twice, not twelve times. Key moments are found again by replaying the events
/// through the detector. Every result is capped (<see cref="MatchFactLimits"/>).
/// </summary>
public sealed class StoredMatchFacts : IMatchFacts
{
    private readonly MatchFactSheet _sheet;
    private readonly IReadOnlyList<MomentFact> _moments;
    private readonly IReadOnlyList<(int Minute, EventFact Fact)> _events;

    private StoredMatchFacts(MatchFactSheet sheet, IReadOnlyList<MomentFact> moments, IReadOnlyList<(int Minute, EventFact Fact)> events)
    {
        _sheet = sheet;
        _moments = moments;
        _events = events;
    }

    /// <summary>Builds the snapshot of a stored match.</summary>
    /// <param name="match">The stored match document.</param>
    /// <param name="events">Its stored events, in sequence order.</param>
    /// <param name="detector">Finds the key moments again.</param>
    /// <param name="teams">The team profiles to name players from; the embedded catalogue if null.</param>
    /// <returns>The facts.</returns>
    public static StoredMatchFacts From(
        MatchRecord match,
        IReadOnlyList<MatchEvent> events,
        KeyMomentDetector detector,
        IReadOnlyDictionary<string, TeamProfile>? teams = null)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(detector);
        teams ??= TeamProfileCatalog.LoadEmbedded();

        var metadata = match.Metadata;
        var homeProfile = metadata is null ? null : teams.GetValueOrDefault(metadata.Home.Id);
        var awayProfile = metadata is null ? null : teams.GetValueOrDefault(metadata.Away.Id);
        var names = new[] { homeProfile, awayProfile }
            .Where(p => p is not null)
            .SelectMany(p => p!.Squad)
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        string homeName = metadata?.Home.Name ?? "Home";
        string awayName = metadata?.Away.Name ?? "Away";
        string[] other = metadata is null
            ? [homeName, awayName]
            : [metadata.Home.Name, metadata.Home.ShortName, metadata.Away.Name, metadata.Away.ShortName, metadata.Venue, metadata.Referee.Name];
        var state = match.State;
        var sheet = new MatchFactSheet(
            match.MatchId,
            homeName,
            awayName,
            state.Home.Goals,
            state.Away.Goals,
            state.Time.ToString(),
            state.FullTime,
            state.Home,
            state.Away,
            Lineup(metadata?.Home, names),
            Lineup(metadata?.Away, names),
            [.. other.Distinct(StringComparer.Ordinal)])
        {
            SquadNames = [.. names.Values.Distinct(StringComparer.Ordinal)]
        };

        return new StoredMatchFacts(sheet, Moments(match.MatchId, events, detector), Notable(match.MatchId, events, names));
    }

    /// <inheritdoc />
    public Task<MatchFactSheet?> GetSummaryAsync(string matchId, CancellationToken cancellationToken) =>
        Task.FromResult(matchId == _sheet.MatchId ? _sheet : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<MomentFact>> GetKeyMomentsAsync(string matchId, CancellationToken cancellationToken) =>
        Task.FromResult(matchId == _sheet.MatchId ? _moments : []);

    /// <inheritdoc />
    public Task<IReadOnlyList<EventFact>> GetEventsAsync(string matchId, int fromMinute, int toMinute, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EventFact>>(matchId != _sheet.MatchId
            ? []
            : [.. _events.Where(e => e.Minute >= fromMinute && e.Minute <= toMinute).Select(e => e.Fact).Take(MatchFactLimits.MaxEvents)]);

    private static string[] Lineup(TeamSheet? sheet, IReadOnlyDictionary<string, string> names) =>
        sheet is null ? [] : [.. sheet.StartingEleven.Select(id => names.GetValueOrDefault(id)).OfType<string>()];

    private static List<MomentFact> Moments(string matchId, IReadOnlyList<MatchEvent> events, KeyMomentDetector detector)
    {
        var state = MatchState.KickOff(matchId);
        var moments = new List<MomentFact>();
        foreach (var e in events)
        {
            if (detector.Detect(e, state) is { } moment)
            {
                moments.Add(new MomentFact(
                    MomentIds.For(matchId, e.Sequence), e.Time.ToString(), moment.Kind.ToString(), e.Team, moment.Reason));
            }

            state = state.Apply(e);
        }

        return [.. moments.TakeLast(MatchFactLimits.MaxMoments)];
    }

    // Goals and cards first, then the other shots, capped, back in match order.
    private static List<(int Minute, EventFact Fact)> Notable(string matchId, IReadOnlyList<MatchEvent> events, IReadOnlyDictionary<string, string> names) =>
    [
        .. events
            .Where(e => e.Type is MatchEventType.Shot or MatchEventType.Card)
            .OrderBy(e => e.Shot?.Outcome == ShotOutcome.Goal || e.Card is not null ? 0 : 1)
            .ThenBy(e => e.Sequence)
            .Take(MatchFactLimits.MaxEvents)
            .OrderBy(e => e.Sequence)
            .Select(e => ((int)e.Time.Clock.TotalMinutes, new EventFact(
                MatchFactIds.Event(matchId, e.Sequence),
                e.Time.ToString(),
                e.Type,
                e.Team,
                e.PlayerId is { } id ? names.GetValueOrDefault(id) : null,
                e.Shot is { } shot ? shot.Outcome.ToString() : e.Card?.ToString())))
    ];

}
