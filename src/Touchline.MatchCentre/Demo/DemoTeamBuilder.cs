using System.Collections.Concurrent;
using Touchline.Domain.Teams;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.TeamBuilding;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// Keeps teams in memory: the built-in profiles, plus any fan teams made since the service
/// started (ADR-031). Fan teams are lost on restart, which is fine for building the UI; the
/// team-builder lane replaces this with the Cosmos DB <c>teams</c> container.
/// </summary>
internal sealed class InMemoryTeamStore : ITeamStore
{
    private readonly ConcurrentDictionary<string, StoredTeam> _teams = new(StringComparer.Ordinal);

    /// <summary>Seeds the store with the built-in profiles.</summary>
    /// <param name="builtIn">The built-in profiles, by team ID.</param>
    public InMemoryTeamStore(IReadOnlyDictionary<string, TeamProfile> builtIn)
    {
        foreach (var team in builtIn.Values)
        {
            _teams[team.Id] = new StoredTeam(TeamViews.FromProfile(team), null);
        }
    }

    /// <inheritdoc />
    public Task<StoredTeam?> GetAsync(string teamId, CancellationToken cancellationToken) =>
        Task.FromResult(_teams.TryGetValue(teamId, out var team) ? team : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredTeam>> ListAsync(TeamOrigin? origin, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredTeam> teams =
        [
            .. _teams.Values
                .Where(t => origin is null || t.Detail.Team.Origin == origin)
                .OrderBy(t => t.Detail.Team.Origin)
                .ThenBy(t => t.Detail.Team.Name, StringComparer.Ordinal)
        ];
        return Task.FromResult(teams);
    }

    /// <inheritdoc />
    public Task<bool> PlayerExistsAsync(string playerId, CancellationToken cancellationToken) =>
        Task.FromResult(_teams.Values.Any(t => t.Detail.Squad.Any(p => p.Id == playerId)));

    /// <inheritdoc />
    public Task AddAsync(StoredTeam team, CancellationToken cancellationToken)
    {
        if (!_teams.TryAdd(team.Detail.Team.Id, team))
        {
            throw new InvalidOperationException($"Team '{team.Detail.Team.Id}' already exists.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ReplaceAsync(StoredTeam team, int expectedVersion, CancellationToken cancellationToken)
    {
        var id = team.Detail.Team.Id;
        return Task.FromResult(_teams.TryGetValue(id, out var current)
            && current.Detail.Version == expectedVersion
            && _teams.TryUpdate(id, team, current));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string teamId, CancellationToken cancellationToken)
    {
        _teams.TryRemove(teamId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Allows every name that meets the format rule. The team-builder lane replaces it with Azure AI
/// Content Safety and its blocklist of real names (ADR-031).
/// </summary>
internal sealed class FormatOnlyNameGuard : INameGuard
{
    /// <inheritdoc />
    public Task<IReadOnlyList<NameVerdict>> CheckAsync(IReadOnlyList<NameToCheck> names, CancellationToken cancellationToken)
    {
        IReadOnlyList<NameVerdict> verdicts = [.. names.Select(n => new NameVerdict(n.Kind, n.Name, true, null))];
        return Task.FromResult(verdicts);
    }
}

/// <summary>
/// Stands in for the launcher until the live pipeline plays fan matches (ADR-032): every request
/// is valid but unavailable.
/// </summary>
internal sealed class UnconnectedMatchLauncher : IMatchLauncher
{
    /// <summary>What a fan sees in the demo build.</summary>
    public const string Reason = "Fan matches need the live match pipeline, which isn't connected in this demo build.";

    /// <inheritdoc />
    public Task<MatchLaunch> LaunchAsync(MatchRequest request, string? idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(new MatchLaunch(LaunchOutcome.Unavailable, null, null, Reason));

    /// <inheritdoc />
    public Task<bool> IsTeamPlayingAsync(string teamId, CancellationToken cancellationToken) => Task.FromResult(false);
}
