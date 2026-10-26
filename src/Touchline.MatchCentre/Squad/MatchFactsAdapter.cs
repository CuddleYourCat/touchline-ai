using Touchline.Domain.Matches;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Viewing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.MatchCentre.Squad;

/// <summary>
/// The Analyst's read-only view of a match (ADR-044), over the Match Centre's read model and the
/// team store, so the demo source and the Cosmos DB read side both work. Every result is capped
/// (<see cref="MatchFactLimits"/>), and an event scan stops after <see cref="MaxEventPages"/> pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names:</b> the guardrail's roster is each team's whole squad from its profile
/// (<see cref="ITeamStore"/>), substitutes included, with the line-up as the fallback when there's
/// no profile or the store fails.
/// </para>
/// <para>
/// <b>Cache:</b> one question calls the tools several times, so the last match read (its state and
/// the player-name map) is kept for <see cref="CacheFor"/>. That's a single entry, not a growing
/// cache: a question about another match replaces it.
/// </para>
/// <para>
/// <b>Event scan:</b> the read model has no minute index, so the scan starts from an estimate: the
/// event sequence now, scaled by <c>fromMinute</c> over the clock now, less a page as a margin. If
/// the first page already starts at or after <c>fromMinute</c>, the estimate overshot, and the
/// scan steps back a page. It stops once it's past <c>toMinute</c>.
/// </para>
/// </remarks>
/// <param name="matches">The read model.</param>
/// <param name="teams">The team profiles, or null to use the line-ups alone.</param>
/// <param name="time">The clock for the cache, or null for the system clock.</param>
/// <param name="matchTeams">
/// Each match's team profiles (ADR-050). A fan match's squads come from the snapshot in its request,
/// the roster of record, before the team store, whose team may have been edited since kick-off.
/// </param>
internal sealed class MatchFactsAdapter(IMatchReadModel matches, ITeamStore? teams = null, TimeProvider? time = null, IMatchTeamProfiles? matchTeams = null) : IMatchFacts
{
    /// <summary>Events read per page while scanning for a minute range.</summary>
    internal const int EventPageSize = 500;

    /// <summary>The most pages one minute-range scan reads, about a whole match.</summary>
    internal const int MaxEventPages = 6;

    /// <summary>How long the last match read is reused, about one question's tool rounds.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private static readonly HashSet<MatchEventType> _notable =
        [MatchEventType.Shot, MatchEventType.Foul, MatchEventType.Card];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private volatile CachedMatch? _last;

    /// <inheritdoc />
    public async Task<MatchFactSheet?> GetSummaryAsync(string matchId, CancellationToken cancellationToken)
    {
        if (await ReadAsync(matchId, fresh: true, cancellationToken) is not { } cached)
        {
            return null;
        }

        var detail = cached.Detail;
        var summary = detail.Summary;
        var state = detail.State;
        return new MatchFactSheet(
            matchId,
            summary.Home.Name,
            summary.Away.Name,
            state.Score.Home,
            state.Score.Away,
            state.Clock.Display,
            state.FullTime,
            state.Home,
            state.Away,
            [.. detail.HomeLineup.Select(p => p.Name)],
            [.. detail.AwayLineup.Select(p => p.Name)],
            [summary.Home.Name, summary.Home.ShortName, summary.Away.Name, summary.Away.ShortName, detail.Venue, detail.Referee])
        {
            SquadNames = [.. cached.Names.Values.Distinct(StringComparer.Ordinal)]
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MomentFact>> GetKeyMomentsAsync(string matchId, CancellationToken cancellationToken)
    {
        // The viewer only shapes the commentary attached to each moment, which the facts leave out.
        var moments = await matches.GetMomentsAsync(matchId, Viewer.Default, cancellationToken);
        return moments is null
            ? []
            :
            [
                .. moments
                    .Where(m => m.Tier == MomentTier.Key)
                    .TakeLast(MatchFactLimits.MaxMoments)
                    .Select(m => new MomentFact(m.Id, m.Clock.Display, m.Kind, m.Team, m.Reason))
            ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventFact>> GetEventsAsync(string matchId, int fromMinute, int toMinute, CancellationToken cancellationToken)
    {
        if (toMinute < fromMinute || await ReadAsync(matchId, fresh: false, cancellationToken) is not { } cached)
        {
            return [];
        }

        var found = new List<(long Sequence, EventFact Fact)>();
        var substitutions = new List<(long Sequence, EventFact Fact)>();
        var after = EstimateStart(cached.Detail.State, fromMinute);
        var checkStart = after > 0;
        for (var page = 0; page < MaxEventPages; page++)
        {
            if (await matches.GetEventsAsync(matchId, after, EventPageSize, cancellationToken) is not { } events)
            {
                break;
            }

            if (checkStart && events.Events.Count > 0 && Minute(events.Events[0]) >= fromMinute)
            {
                // The estimate overshot: earlier events may be in range, so step back a page.
                after = Math.Max(0, after - EventPageSize);
                checkStart = after > 0;
                continue;
            }

            checkStart = false;
            foreach (var e in events.Events)
            {
                var minute = Minute(e);
                if (minute > toMinute)
                {
                    return WithSubstitutions(found, substitutions);
                }

                if (minute < fromMinute)
                {
                    continue;
                }

                if (_notable.Contains(e.Type) || e.Restart == RestartKind.Corner)
                {
                    found.Add((e.Sequence, ToFact(matchId, e, cached.Names)));
                    if (found.Count == MatchFactLimits.MaxEvents)
                    {
                        return WithSubstitutions(found, substitutions);
                    }
                }
                else if (e.Type == MatchEventType.Substitution && substitutions.Count < MatchFactLimits.MaxEvents)
                {
                    substitutions.Add((e.Sequence, ToFact(matchId, e, cached.Names)));
                }
            }

            if (events.NextAfter is not { } next)
            {
                break;
            }

            after = next;
        }

        return WithSubstitutions(found, substitutions);
    }

    // Substitutions (ADR-064) only fill the slots the shots, fouls, cards and corners leave, so a
    // busy hour of changes never crowds out the play. Back in match order.
    private static List<EventFact> WithSubstitutions(List<(long Sequence, EventFact Fact)> found, List<(long Sequence, EventFact Fact)> substitutions) =>
    [
        .. found.Concat(substitutions.Take(MatchFactLimits.MaxEvents - found.Count))
            .OrderBy(f => f.Sequence)
            .Select(f => f.Fact)
    ];

    /// <summary>
    /// Where to start scanning for <paramref name="fromMinute"/>: the sequence now, scaled by the
    /// minute over the clock now, less a page as a margin. Zero means from kick-off.
    /// </summary>
    internal static long EstimateStart(LiveState state, int fromMinute)
    {
        var minuteNow = state.Clock.Minute;
        if (fromMinute <= 0 || minuteNow <= 0 || state.LastSequence <= 0)
        {
            return 0;
        }

        var estimate = state.LastSequence * Math.Min(fromMinute, minuteNow) / (minuteNow + 1);
        return Math.Max(0, estimate - EventPageSize);
    }

    private static int Minute(MatchEvent e) => (int)e.Time.Clock.TotalMinutes;

    private async Task<CachedMatch?> ReadAsync(string matchId, bool fresh, CancellationToken cancellationToken)
    {
        var now = _time.GetTimestamp();
        if (!fresh && _last is { } last && last.MatchId == matchId && _time.GetElapsedTime(last.ReadAt, now) < CacheFor)
        {
            return last;
        }

        if (await matches.GetMatchAsync(matchId, cancellationToken) is not { } detail)
        {
            return null;
        }

        var cached = new CachedMatch(matchId, now, detail, await NamesAsync(detail, cancellationToken));
        _last = cached;
        return cached;
    }

    /// <summary>Player ID to name, for both squads, or both line-ups and benches (ADR-064) where there's no profile.</summary>
    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(MatchDetail detail, CancellationToken cancellationToken)
    {
        var profiles = matchTeams is null ? null : await matchTeams.ForMatchAsync(detail.Summary.Id, cancellationToken);
        var players = new List<PlayerSummary>(detail.HomeLineup.Count + detail.AwayLineup.Count);
        players.AddRange(await SquadAsync(detail.Summary.Home.Id, [.. detail.HomeLineup, .. detail.HomeSubstitutes], profiles, cancellationToken));
        players.AddRange(await SquadAsync(detail.Summary.Away.Id, [.. detail.AwayLineup, .. detail.AwaySubstitutes], profiles, cancellationToken));
        return players
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
    }

    private async Task<IEnumerable<PlayerSummary>> SquadAsync(
        string teamId, IReadOnlyList<PlayerSummary> lineup, IReadOnlyDictionary<string, TeamProfile>? profiles, CancellationToken cancellationToken)
    {
        // A fan team in a fan match: the snapshot is the squad, whatever the store says now.
        if (teamId.StartsWith(TeamViews.FanPrefix, StringComparison.Ordinal) && profiles?.GetValueOrDefault(teamId) is { } snapshot)
        {
            return lineup.Concat(snapshot.Squad.Select(p => PlayerSummary.From(p, teamId)));
        }

        if (teams is null)
        {
            return lineup;
        }

        try
        {
            return await teams.GetAsync(teamId, cancellationToken) is { } team
                ? lineup.Concat(team.Detail.Squad)
                : lineup;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The line-up is enough for the guardrail; a team store outage shouldn't stop an answer.
            return lineup;
        }
    }

    private static EventFact ToFact(string matchId, MatchEvent e, IReadOnlyDictionary<string, string> names) =>
        new(
            MatchFactIds.Event(matchId, e.Sequence),
            e.Time.ToString(),
            e.Type,
            e.Team,
            e.PlayerId is { } id && names.TryGetValue(id, out var name) ? name : null,
            e switch
            {
                { Shot: { } shot } => shot.Outcome.ToString(),
                { Card: { } card } => card.ToString(),
                { Restart: RestartKind.Corner } => "corner",
                { Type: MatchEventType.Substitution } => SubstitutionFacts.Outcome(e, names),
                _ => e.Successful ? null : "unsuccessful"
            });

    private sealed record CachedMatch(string MatchId, long ReadAt, MatchDetail Detail, IReadOnlyDictionary<string, string> Names);
}
