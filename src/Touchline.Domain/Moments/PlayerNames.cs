using Touchline.Domain.Teams;

namespace Touchline.Domain.Moments;

/// <summary>
/// Looks players up in the squads, so moments can name them from the roster and never from
/// anywhere else.
/// </summary>
internal sealed class PlayerNames
{
    private readonly IReadOnlyDictionary<string, (Player Player, TeamProfile Team)> _players;

    /// <summary>Indexes the players of the given squads.</summary>
    /// <param name="teams">The squads, or null for the embedded team profiles.</param>
    public PlayerNames(IEnumerable<TeamProfile>? teams)
    {
        _players = (teams ?? TeamProfileCatalog.LoadEmbedded().Values)
            .SelectMany(team => team.Squad.Select(player => (Player: player, Team: team)))
            .ToDictionary(entry => entry.Player.Id);
    }

    /// <summary>A player as <c>Name (SHORT)</c>; an unknown ID comes back as the ID itself.</summary>
    /// <param name="playerId">The player, or null.</param>
    /// <returns>The description.</returns>
    public string Describe(string? playerId) =>
        Find(playerId) is { } found ? $"{found.Name} ({found.Team})" : playerId ?? "unknown player";

    /// <summary>A player's roster name and club short name, or null when the ID isn't in any squad.</summary>
    /// <param name="playerId">The player, or null.</param>
    /// <returns>The names, or null.</returns>
    public (string Name, string Team)? Find(string? playerId) =>
        playerId is not null && _players.TryGetValue(playerId, out var entry)
            ? (entry.Player.Name, entry.Team.ShortName)
            : null;
}
