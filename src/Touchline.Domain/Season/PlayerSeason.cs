using System.Globalization;

namespace Touchline.Domain.Season;

/// <summary>The fictional league's disciplinary rules (ADR-025).</summary>
public static class LeagueRules
{
    /// <summary>Every fifth yellow card of a season earns a one-match ban.</summary>
    public const int YellowCardsPerBan = 5;

    /// <summary>How many penalties a season document keeps for a taker or a goalkeeper, newest first.</summary>
    public const int RecentPenalties = 5;
}

/// <summary>
/// One player's season: the <c>player-seasons</c> document (ADR-025). It's rebuilt from all of
/// the player's <see cref="PlayerMatch"/> documents for the season, never incremented, so a
/// redelivered fixture can't count twice.
/// </summary>
/// <param name="Id">The document ID: <c>{season}:{playerId}</c>.</param>
/// <param name="Season">The season, and the partition key.</param>
/// <param name="PlayerId">The player.</param>
/// <param name="TeamId">The player's team, as of their latest fixture.</param>
/// <param name="Appearances">Fixtures played, starting or coming on from the bench (ADR-064).</param>
/// <param name="Rounds">The rounds the player played in, in order. The simulator reads these to know which rounds are done.</param>
/// <param name="Goals">Goals scored.</param>
/// <param name="Assists">Goals set up.</param>
/// <param name="Shots">Shots taken.</param>
/// <param name="ShotsOnTarget">Goals plus saved shots.</param>
/// <param name="ExpectedGoals">The xG of every shot taken.</param>
/// <param name="Passes">Passes attempted.</param>
/// <param name="PassesCompleted">Passes completed.</param>
/// <param name="Fouls">Fouls committed.</param>
/// <param name="YellowCards">Yellow cards shown.</param>
/// <param name="RedCards">Red cards shown.</param>
/// <param name="BannedRounds">The rounds the player is suspended for, in order.</param>
/// <param name="RecentPenaltiesTaken">The player's last five penalties, newest first.</param>
/// <param name="RecentPenaltiesFaced">The last five penalties the player faced in goal, newest first.</param>
/// <param name="SubstituteAppearances">How many of the appearances were from the bench. A document stored before substitutions reads as 0.</param>
public sealed record PlayerSeason(
    string Id,
    string Season,
    string PlayerId,
    string TeamId,
    int Appearances,
    IReadOnlyList<int> Rounds,
    int Goals,
    int Assists,
    int Shots,
    int ShotsOnTarget,
    double ExpectedGoals,
    int Passes,
    int PassesCompleted,
    int Fouls,
    int YellowCards,
    int RedCards,
    IReadOnlyList<int> BannedRounds,
    IReadOnlyList<PenaltyRecord> RecentPenaltiesTaken,
    IReadOnlyList<PenaltyRecord> RecentPenaltiesFaced,
    int SubstituteAppearances = 0)
{
    /// <summary>The document ID for a player's season.</summary>
    /// <param name="season">The season.</param>
    /// <param name="playerId">The player.</param>
    /// <returns><c>{season}:{playerId}</c>.</returns>
    public static string IdFor(string season, string playerId) =>
        string.Create(CultureInfo.InvariantCulture, $"{season}:{playerId}");

    /// <summary>Whether the player is suspended for a round.</summary>
    /// <param name="round">The round.</param>
    /// <returns>True when a ban covers that round.</returns>
    public bool IsBannedFor(int round) => BannedRounds.Contains(round);

    /// <summary>
    /// Rebuilds a player's season from every fixture they played in it.
    /// </summary>
    /// <remarks>
    /// Bans count by round number: a ban earned in round R is served in round R + 1, or the next
    /// round not already covered by a ban. A red card earns one ban, and so does every fifth
    /// yellow. The result depends only on the fixtures, never on the order they were projected in.
    /// A round counts once: if two fixtures exist for the same round, only the first by match ID
    /// counts. The simulator refuses to play a round twice, so this is only a defence; re-running
    /// a round is what a replay is for (ADR-020).
    /// </remarks>
    /// <param name="matches">The player's fixtures for one season, in any order.</param>
    /// <returns>The season document.</returns>
    /// <exception cref="ArgumentException">There are no fixtures, or they don't all belong to one player and one season.</exception>
    public static PlayerSeason Rebuild(IEnumerable<PlayerMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var played = matches
            .OrderBy(m => m.Round).ThenBy(m => m.MatchId, StringComparer.Ordinal)
            .DistinctBy(m => m.Round)
            .ToList();
        if (played is not [var first, ..])
        {
            throw new ArgumentException("A season needs at least one fixture.", nameof(matches));
        }

        if (played.Any(m => m.PlayerId != first.PlayerId || m.Season != first.Season))
        {
            throw new ArgumentException($"Every fixture must be {first.PlayerId}'s in {first.Season}.", nameof(matches));
        }

        var banned = new SortedSet<int>();
        var yellows = 0;
        foreach (var match in played)
        {
            var before = yellows;
            yellows += match.YellowCards;
            var bans = (yellows / LeagueRules.YellowCardsPerBan) - (before / LeagueRules.YellowCardsPerBan)
                + (match.RedCards > 0 ? 1 : 0);

            for (var round = match.Round + 1; bans > 0; round++)
            {
                if (banned.Add(round))
                {
                    bans--;
                }
            }
        }

        return new PlayerSeason(
            IdFor(first.Season, first.PlayerId),
            first.Season,
            first.PlayerId,
            played[^1].TeamId,
            played.Count,
            [.. played.Select(m => m.Round)],
            played.Sum(m => m.Goals),
            played.Sum(m => m.Assists),
            played.Sum(m => m.Shots),
            played.Sum(m => m.ShotsOnTarget),
            played.Sum(m => m.ExpectedGoals),
            played.Sum(m => m.Passes),
            played.Sum(m => m.PassesCompleted),
            played.Sum(m => m.Fouls),
            yellows,
            played.Sum(m => m.RedCards),
            [.. banned],
            Newest(played.SelectMany(m => m.PenaltiesTaken)),
            Newest(played.SelectMany(m => m.PenaltiesFaced)),
            played.Count(m => m.Substitute));
    }

    private static List<PenaltyRecord> Newest(IEnumerable<PenaltyRecord> penalties) =>
        [.. penalties.OrderByDescending(p => p.Round).ThenByDescending(p => p.Sequence).Take(LeagueRules.RecentPenalties)];
}
