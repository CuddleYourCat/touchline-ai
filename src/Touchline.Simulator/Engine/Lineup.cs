using Touchline.Domain.Pitch;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Simulator.Engine;

/// <summary>
/// The eleven players a team starts with, its bench, and who is still on the pitch. Plays the
/// team sheet when one is given (ADR-050); otherwise picks the starting eleven in squad order:
/// one goalkeeper, four defenders, three midfielders, three forwards, with the rest on the bench.
/// </summary>
internal sealed class Lineup
{
    // How likely each position is to be involved, by the third the ball is in, from that
    // team's point of view. Defenders act deep, forwards high, midfielders everywhere.
    private static readonly Dictionary<Third, Dictionary<PlayerPosition, double>> _involvement = new()
    {
        [Third.Defensive] = new() { [PlayerPosition.Goalkeeper] = 0.5, [PlayerPosition.Defender] = 4, [PlayerPosition.Midfielder] = 2, [PlayerPosition.Forward] = 0.3 },
        [Third.Middle] = new() { [PlayerPosition.Goalkeeper] = 0.05, [PlayerPosition.Defender] = 1.5, [PlayerPosition.Midfielder] = 4, [PlayerPosition.Forward] = 1 },
        [Third.Attacking] = new() { [PlayerPosition.Goalkeeper] = 0, [PlayerPosition.Defender] = 0.4, [PlayerPosition.Midfielder] = 2.5, [PlayerPosition.Forward] = 4 }
    };

    private readonly List<Player> _onPitch;
    private readonly List<Player> _bench;
    private readonly HashSet<string> _booked = [];
    private readonly HashSet<string> _cameOn = [];

    public Lineup(TeamProfile team, MatchdaySheet? sheet = null)
    {
        Team = team;
        _onPitch = [.. StartingEleven(team, sheet)];
        _bench = [.. Bench(team, sheet)];
    }

    /// <summary>
    /// The substitutes (ADR-064): a sheet's bench, or without one the squad players not in the
    /// eleven. Like <see cref="StartingEleven"/>, the one definition for play and the metadata.
    /// </summary>
    /// <exception cref="ArgumentException">The sheet names a player who isn't in the squad.</exception>
    public static IReadOnlyList<Player> Bench(TeamProfile team, MatchdaySheet? sheet = null) =>
        StartingElevens.Bench(team, sheet);

    /// <summary>The substitutes still on the bench, in sheet order.</summary>
    public IReadOnlyList<Player> OnBench => _bench;

    /// <summary>How many substitutes this team has brought on.</summary>
    public int SubstitutesUsed => _cameOn.Count;

    /// <summary>Whether a player came on from the bench in this match.</summary>
    public bool CameOn(Player player) => _cameOn.Contains(player.Id);

    /// <summary>
    /// Takes a player off and brings a substitute on in his place (ADR-064). The substitute takes
    /// the same place in the line-up, so he inherits the departing player's turn as set-piece or
    /// penalty taker when his position allows, and plays with his own traits. The player taken
    /// off can't come back: he leaves the pitch, and the bench only ever loses players.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The player going off isn't on the pitch or is the goalkeeper, or the one coming on isn't on the bench.
    /// </exception>
    public void Substitute(Player off, Player on)
    {
        var index = _onPitch.IndexOf(off);
        if (index < 0)
        {
            throw new InvalidOperationException($"{off.Name} can't be substituted: he isn't on the pitch.");
        }

        if (off.Position == PlayerPosition.Goalkeeper)
        {
            throw new InvalidOperationException($"{off.Name} is the goalkeeper, and the goalkeeper is never substituted.");
        }

        if (!_bench.Remove(on))
        {
            throw new InvalidOperationException($"{on.Name} can't come on: he isn't on the bench.");
        }

        _onPitch[index] = on;
        _cameOn.Add(on.Id);
    }

    /// <summary>
    /// The eleven who start, goalkeeper first. It's the one definition used both for play and
    /// for the metadata's team sheet, so the two can't disagree.
    /// </summary>
    /// <remarks>
    /// A sheet's eleven is put in line order (goalkeeper, defenders, midfielders, forwards),
    /// keeping the sheet's order within each line. That order picks the set-piece and penalty
    /// takers, as it does for the default pick, and keeps the goalkeeper first, which readers
    /// of the metadata rely on. The caller has already checked the sheet against
    /// <see cref="MatchdaySheetRules"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">The sheet names a player who isn't in the squad.</exception>
    public static IReadOnlyList<Player> StartingEleven(TeamProfile team, MatchdaySheet? sheet = null) =>
        StartingElevens.For(team, sheet);

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

    /// <summary>The goalkeeper on the pitch, or null if none is left.</summary>
    public Player? Goalkeeper => _onPitch.FirstOrDefault(p => p.Position == PlayerPosition.Goalkeeper);

    /// <summary>Whether a player is still on the pitch (not sent off or substituted).</summary>
    public bool IsOnPitch(Player player) => _onPitch.Contains(player);

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
