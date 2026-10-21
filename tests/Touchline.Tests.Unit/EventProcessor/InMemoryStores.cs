extern alias processor;
using processor::Touchline.EventProcessor.Processing;
using Touchline.Domain.Matches;
using Touchline.Domain.Season;

namespace Touchline.Tests.Unit.EventProcessor;

/// <summary>The match store in memory: events by match and sequence, and one document per match.</summary>
internal sealed class InMemoryMatchStore : IMatchStore
{
    public Dictionary<string, SortedDictionary<long, MatchEvent>> Events { get; } = [];

    public Dictionary<string, MatchRecord> Matches { get; } = [];

    public int MatchWrites { get; private set; }

    public Action<string> OnCall { get; set; } = _ => { };

    public Task<MatchRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        OnCall("get match");
        return Task.FromResult(Matches.GetValueOrDefault(matchId));
    }

    public Task UpsertEventsAsync(string matchId, IReadOnlyList<MatchEvent> events, CancellationToken cancellationToken)
    {
        OnCall("upsert events");
        var stored = Events.TryGetValue(matchId, out var existing) ? existing : Events[matchId] = [];
        foreach (var e in events)
        {
            stored[e.Sequence] = e;
        }

        return Task.CompletedTask;
    }

    public Task UpsertMatchAsync(MatchRecord match, CancellationToken cancellationToken)
    {
        OnCall("upsert match");
        MatchWrites++;
        Matches[match.MatchId] = match;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MatchEvent>> GetEventsAsync(string matchId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MatchEvent>>(Events.TryGetValue(matchId, out var stored) ? [.. stored.Values] : []);
}

/// <summary>The season projection's containers in memory, keyed by document ID, as an upsert would be.</summary>
internal sealed class InMemorySeasonStore : ISeasonStore
{
    public Dictionary<string, PlayerMatch> PlayerMatches { get; } = [];

    public Dictionary<string, PlayerSeason> PlayerSeasons { get; } = [];

    public Task UpsertPlayerMatchesAsync(IReadOnlyList<PlayerMatch> matches, CancellationToken cancellationToken)
    {
        foreach (var match in matches)
        {
            PlayerMatches[match.Id] = match;
        }

        return Task.CompletedTask;
    }

    // The projector rebuilds seasons a few at a time, so reads and writes here can overlap.
    public Task<IReadOnlyList<PlayerMatch>> GetPlayerMatchesAsync(string season, string playerId, CancellationToken cancellationToken)
    {
        lock (PlayerMatches)
        {
            return Task.FromResult<IReadOnlyList<PlayerMatch>>([.. PlayerMatches.Values.Where(m => m.PlayerId == playerId && m.Season == season)]);
        }
    }

    public Task UpsertPlayerSeasonAsync(PlayerSeason season, CancellationToken cancellationToken)
    {
        lock (PlayerSeasons)
        {
            PlayerSeasons[season.Id] = season;
        }

        return Task.CompletedTask;
    }
}
