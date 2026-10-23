using Touchline.Domain.Teams;

namespace Touchline.Domain.Matches.Shape;

/// <summary>A player's place in the formation.</summary>
/// <param name="PlayerId">The player.</param>
/// <param name="Line">The line the player plays in.</param>
/// <param name="Index">Where in the line, from 0, across the pitch from the team's right.</param>
/// <param name="Count">How many players the line has.</param>
internal sealed record Slot(string PlayerId, PlayerPosition Line, int Index, int Count);

/// <summary>
/// Derives a formation, such as a 4-3-3, from a team sheet: the first player on the sheet
/// whose position is goalkeeper keeps goal (or the first player, if none is), and everyone
/// else plays in the line of their position, across it in team-sheet order.
/// </summary>
internal static class Formation
{
    /// <summary>The slots of a team's starting eleven.</summary>
    /// <param name="team">The team.</param>
    /// <returns>One slot per player, in team-sheet order.</returns>
    public static IReadOnlyList<Slot> Slots(ShapeTeam team)
    {
        var eleven = team.Sheet.StartingEleven;
        var keeper = Math.Max(0, eleven.ToList().FindIndex(id => PositionOf(team, id) == PlayerPosition.Goalkeeper));
        var lines = eleven
            .Select((id, i) => (Id: id, Line: i == keeper ? PlayerPosition.Goalkeeper : LineOf(PositionOf(team, id))))
            .ToList();

        return
        [
            .. lines.Select(player =>
            {
                var line = lines.Where(p => p.Line == player.Line).ToList();
                return new Slot(player.Id, player.Line, line.FindIndex(p => p.Id == player.Id), line.Count);
            })
        ];
    }

    private static PlayerPosition? PositionOf(ShapeTeam team, string id) =>
        team.Positions.TryGetValue(id, out var position) ? position : null;

    // A second goalkeeper plays in defence, and a player missing from the profile in midfield.
    private static PlayerPosition LineOf(PlayerPosition? position) => position switch
    {
        PlayerPosition.Goalkeeper => PlayerPosition.Defender,
        { } known => known,
        null => PlayerPosition.Midfielder
    };
}
