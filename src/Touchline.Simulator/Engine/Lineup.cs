using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;

namespace Touchline.Simulator.Engine;

/// <summary>
/// The eleven players a team starts with, and who is still on the pitch. Picks the starting
/// eleven in squad order: one goalkeeper, four defenders, three midfielders, three forwards.
/// </summary>
internal sealed class Lineup
{
    private static readonly (PlayerPosition Position, int Count)[] _shape =
    [
        (PlayerPosition.Goalkeeper, 1),
        (PlayerPosition.Defender, 4),
        (PlayerPosition.Midfielder, 3),
        (PlayerPosition.Forward, 3)
    ];

    // How likely each position is to be involved, by the third the ball is in, from that
    // team's point of view. Defenders act deep, forwards high, midfielders everywhere.
    private static readonly Dictionary<Third, Dictionary<PlayerPosition, double>> _involvement = new()
    {
        [Third.Defensive] = new() { [PlayerPosition.Goalkeeper] = 0.5, [PlayerPosition.Defender] = 4, [PlayerPosition.Midfielder] = 2, [PlayerPosition.Forward] = 0.3 },
        [Third.Middle] = new() { [PlayerPosition.Goalkeeper] = 0.05, [PlayerPosition.Defender] = 1.5, [PlayerPosition.Midfielder] = 4, [PlayerPosition.Forward] = 1 },
        [Third.Attacking] = new() { [PlayerPosition.Goalkeeper] = 0, [PlayerPosition.Defender] = 0.4, [PlayerPosition.Midfielder] = 2.5, [PlayerPosition.Forward] = 4 }
    };

    private readonly List<Player> _onPitch;
    private readonly HashSet<string> _booked = [];

    public Lineup(TeamProfile team)
    {
        Team = team;
        _onPitch = _shape
            .SelectMany(slot => team.Squad.Where(p => p.Position == slot.Position).Take(slot.Count))
            .ToList();

        if (_onPitch.Count < 11)
        {
            // A squad short in one position fills the gaps from whoever is left.
            _onPitch.AddRange(team.Squad.Except(_onPitch).Take(11 - _onPitch.Count));
        }
    }

    public TeamProfile Team { get; }

    public IReadOnlyList<Player> OnPitch => _onPitch;

    /// <summary>Picks a player for an action in the given third, never <paramref name="except"/>.</summary>
    public Player Pick(SeededRandom random, Third third, Player? except = null)
    {
        var weights = _involvement[third];
        var candidates = _onPitch.Where(p => p != except).ToList();
        var total = candidates.Sum(p => weights[p.Position]);

        if (total <= 0)
        {
            return candidates[random.Next(candidates.Count)];
        }

        var roll = random.NextDouble() * total;
        foreach (var player in candidates)
        {
            roll -= weights[player.Position];
            if (roll <= 0)
            {
                return player;
            }
        }

        return candidates[^1];
    }

    /// <summary>Picks a set-piece taker: the first midfielder on the pitch, or anyone if none are left.</summary>
    public Player SetPieceTaker() =>
        _onPitch.FirstOrDefault(p => p.Position == PlayerPosition.Midfielder) ?? _onPitch[0];

    /// <summary>Picks a penalty taker: the first forward on the pitch, or a midfielder, or anyone.</summary>
    public Player PenaltyTaker() =>
        _onPitch.FirstOrDefault(p => p.Position == PlayerPosition.Forward)
        ?? _onPitch.FirstOrDefault(p => p.Position == PlayerPosition.Midfielder)
        ?? _onPitch[0];

    /// <summary>How many of this team's players have been sent off.</summary>
    public int SentOffCount { get; private set; }

    /// <summary>Whether a player already has a yellow card.</summary>
    public bool IsBooked(Player player) => _booked.Contains(player.Id);

    /// <summary>Records a yellow card.</summary>
    public void Book(Player player) => _booked.Add(player.Id);

    /// <summary>
    /// Removes a player who has been sent off. The engine caps sendings-off per team well
    /// above the laws' 7-player minimum, so reaching it is a bug, and fails loudly rather
    /// than leaving a red-carded player on the pitch.
    /// </summary>
    /// <exception cref="InvalidOperationException">The player isn't on the pitch, or the team would fall below 7.</exception>
    public void SendOff(Player player)
    {
        if (_onPitch.Count <= 7)
        {
            throw new InvalidOperationException($"{Team.Name} can't fall below 7 players; the sending-off cap should have prevented this.");
        }

        if (!_onPitch.Remove(player))
        {
            throw new InvalidOperationException($"{player.Name} was sent off but isn't on the pitch.");
        }

        SentOffCount++;
    }
}
