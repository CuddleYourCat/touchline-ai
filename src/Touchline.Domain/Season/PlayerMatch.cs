using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Pitch;

namespace Touchline.Domain.Season;

/// <summary>One penalty kick, as both the taker's and the goalkeeper's record of it (ADR-021).</summary>
/// <param name="MatchId">The fixture it was taken in.</param>
/// <param name="Round">That fixture's round, so a history can be ordered.</param>
/// <param name="Sequence">The shot event's sequence, which orders penalties within a match.</param>
/// <param name="TakerId">Who took it.</param>
/// <param name="KeeperId">
/// The goalkeeper facing it: the other side's first-named starter, or null when that starter had
/// already been sent off, so whoever stood in goal isn't known.
/// </param>
/// <param name="Outcome">Scored, saved or missed.</param>
/// <param name="GoalMouth">Where it crossed (or would have crossed) the goal line.</param>
/// <param name="KeeperDive">Which way the goalkeeper went.</param>
public sealed record PenaltyRecord(
    string MatchId,
    int Round,
    long Sequence,
    string TakerId,
    string? KeeperId,
    ShotOutcome Outcome,
    GoalMouthPosition? GoalMouth,
    DiveDirection? KeeperDive);

/// <summary>
/// One player's part in one fixture: the <c>player-matches</c> document (ADR-025). It's built
/// from the fixture's stored events and written with an upsert under a deterministic ID, so
/// projecting the same fixture twice changes nothing.
/// </summary>
/// <param name="Id">The document ID: <c>{matchId}:{playerId}</c>.</param>
/// <param name="PlayerId">The player, and the partition key.</param>
/// <param name="MatchId">The fixture.</param>
/// <param name="TeamId">The player's team profile ID.</param>
/// <param name="Season">The fixture's season.</param>
/// <param name="Round">The fixture's round.</param>
/// <param name="Goals">Goals scored.</param>
/// <param name="Assists">Goals the player set up.</param>
/// <param name="Shots">Shots taken, including blocked ones.</param>
/// <param name="ShotsOnTarget">Goals plus saved shots.</param>
/// <param name="ExpectedGoals">The xG of every shot taken.</param>
/// <param name="Passes">Passes attempted.</param>
/// <param name="PassesCompleted">Passes that reached a team-mate.</param>
/// <param name="Fouls">Fouls committed.</param>
/// <param name="YellowCards">Yellow cards shown.</param>
/// <param name="RedCards">Red cards shown.</param>
/// <param name="PenaltiesTaken">Penalties the player took, in order.</param>
/// <param name="PenaltiesFaced">Penalties the player faced in goal, in order.</param>
public sealed record PlayerMatch(
    string Id,
    string PlayerId,
    string MatchId,
    string TeamId,
    string Season,
    int Round,
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
    IReadOnlyList<PenaltyRecord> PenaltiesTaken,
    IReadOnlyList<PenaltyRecord> PenaltiesFaced)
{
    /// <summary>The document ID for a player's part in a match.</summary>
    /// <param name="matchId">The fixture.</param>
    /// <param name="playerId">The player.</param>
    /// <returns><c>{matchId}:{playerId}</c>.</returns>
    public static string IdFor(string matchId, string playerId) =>
        string.Create(CultureInfo.InvariantCulture, $"{matchId}:{playerId}");

    /// <summary>
    /// Builds every starter's part in a finished fixture from its events. Each starter gets a
    /// document, even one who never touched the ball: it's still an appearance.
    /// </summary>
    /// <param name="metadata">The fixture's metadata, with its team sheets, season and round.</param>
    /// <param name="events">The fixture's events, in any order.</param>
    /// <returns>One document per starter, home side first.</returns>
    /// <exception cref="ArgumentException">The match isn't a fixture, so it doesn't count towards a season (ADR-020).</exception>
    public static IReadOnlyList<PlayerMatch> FromFixture(MatchMetadata metadata, IEnumerable<MatchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(events);

        if (metadata is not { Kind: MatchKind.Fixture, Season: { } season, Round: { } round })
        {
            throw new ArgumentException($"Only a fixture counts towards a season, not a {metadata.Kind}.", nameof(metadata));
        }

        var matchId = events.FirstOrDefault()?.MatchId
            ?? throw new ArgumentException("A finished fixture has events.", nameof(events));
        var tallies = metadata.Home.StartingEleven.Select(id => new Tally(id, metadata.Home.Id))
            .Concat(metadata.Away.StartingEleven.Select(id => new Tally(id, metadata.Away.Id)))
            .ToDictionary(tally => tally.PlayerId);
        var goalkeepers = new Dictionary<TeamSide, string>
        {
            [TeamSide.Home] = metadata.Home.StartingEleven[0],
            [TeamSide.Away] = metadata.Away.StartingEleven[0]
        };

        var sentOff = new HashSet<string>(StringComparer.Ordinal);
        foreach (var matchEvent in events.OrderBy(e => e.Sequence))
        {
            if (matchEvent is { Type: MatchEventType.Card, Card: CardColour.Red, PlayerId: { } dismissed })
            {
                sentOff.Add(dismissed);
            }

            if (matchEvent.PlayerId is not { } playerId || !tallies.TryGetValue(playerId, out var player))
            {
                continue;
            }

            switch (matchEvent)
            {
                case { Type: MatchEventType.Pass }:
                    player.Passes++;
                    player.PassesCompleted += matchEvent.Successful ? 1 : 0;
                    break;

                case { Type: MatchEventType.Shot, Shot: { } shot }:
                    player.Shots++;
                    player.ShotsOnTarget += shot.OnTarget ? 1 : 0;
                    player.ExpectedGoals += shot.ExpectedGoals;
                    if (shot.Outcome == ShotOutcome.Goal)
                    {
                        player.Goals++;
                        if (matchEvent.AssistPlayerId is { } assist && tallies.TryGetValue(assist, out var assister))
                        {
                            assister.Assists++;
                        }
                    }

                    if (shot.Situation == ShotSituation.Penalty)
                    {
                        // A starting keeper sent off (often for the foul that gave the penalty) didn't face it.
                        var starter = goalkeepers[matchEvent.Team == TeamSide.Home ? TeamSide.Away : TeamSide.Home];
                        var keeperId = sentOff.Contains(starter) ? null : starter;
                        var penalty = new PenaltyRecord(matchId, round, matchEvent.Sequence, playerId, keeperId, shot.Outcome, shot.GoalMouth, shot.KeeperDive);
                        player.PenaltiesTaken.Add(penalty);
                        if (keeperId is not null && tallies.TryGetValue(keeperId, out var keeper))
                        {
                            keeper.PenaltiesFaced.Add(penalty);
                        }
                    }

                    break;

                case { Type: MatchEventType.Foul }:
                    player.Fouls++;
                    break;

                case { Type: MatchEventType.Card, Card: CardColour.Yellow }:
                    player.YellowCards++;
                    break;

                case { Type: MatchEventType.Card, Card: CardColour.Red }:
                    player.RedCards++;
                    break;
            }
        }

        return [.. tallies.Values.Select(tally => tally.ToDocument(matchId, season, round))];
    }

    // A mutable running total for one player while the events are read.
    private sealed class Tally(string playerId, string teamId)
    {
        public string PlayerId { get; } = playerId;

        public int Goals { get; set; }

        public int Assists { get; set; }

        public int Shots { get; set; }

        public int ShotsOnTarget { get; set; }

        public double ExpectedGoals { get; set; }

        public int Passes { get; set; }

        public int PassesCompleted { get; set; }

        public int Fouls { get; set; }

        public int YellowCards { get; set; }

        public int RedCards { get; set; }

        public List<PenaltyRecord> PenaltiesTaken { get; } = [];

        public List<PenaltyRecord> PenaltiesFaced { get; } = [];

        public PlayerMatch ToDocument(string matchId, string season, int round) => new(
            IdFor(matchId, PlayerId), PlayerId, matchId, teamId, season, round,
            Goals, Assists, Shots, ShotsOnTarget, ExpectedGoals, Passes, PassesCompleted, Fouls, YellowCards, RedCards,
            PenaltiesTaken, PenaltiesFaced);
    }
}
