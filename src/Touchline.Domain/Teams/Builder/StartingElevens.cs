namespace Touchline.Domain.Teams.Builder;

/// <summary>
/// The one definition of who starts (ADR-050), shared by the engine, which plays the eleven, and
/// the Match Centre, which shows a scheduled fan match's line-ups before its first event.
/// </summary>
public static class StartingElevens
{
    private static readonly (PlayerPosition Position, int Count)[] _shape =
    [
        (PlayerPosition.Goalkeeper, 1),
        (PlayerPosition.Defender, 4),
        (PlayerPosition.Midfielder, 3),
        (PlayerPosition.Forward, 3)
    ];

    /// <summary>
    /// The eleven who start, goalkeeper first. A sheet's eleven is put in line order (goalkeeper,
    /// defenders, midfielders, forwards), keeping the sheet's order within each line; without a
    /// sheet, the default 1-4-3-3 pick in squad order, with any gaps filled from whoever is left.
    /// </summary>
    /// <param name="team">The team.</param>
    /// <param name="sheet">The chosen eleven, already checked against <see cref="MatchdaySheetRules"/>, or null.</param>
    /// <returns>The starting eleven.</returns>
    /// <exception cref="ArgumentException">The sheet names a player who isn't in the squad.</exception>
    public static IReadOnlyList<Player> For(TeamProfile team, MatchdaySheet? sheet = null)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (sheet is not null)
        {
            // OrderBy is a stable sort, so players in the same line keep the sheet's order.
            return
            [
                .. sheet.StartingEleven
                    .Select(id => team.FindPlayer(id) ?? throw new ArgumentException($"Player '{id}' isn't in {team.Name}'s squad.", nameof(sheet)))
                    .OrderBy(p => p.Position)
            ];
        }

        var eleven = _shape
            .SelectMany(slot => team.Squad.Where(p => p.Position == slot.Position).Take(slot.Count))
            .ToList();

        if (eleven.Count < 11)
        {
            eleven.AddRange(team.Squad.Except(eleven).Take(11 - eleven.Count));
        }

        return eleven;
    }

    /// <summary>
    /// The bench (ADR-064): a sheet's substitutes in sheet order; without a sheet, the squad
    /// players not in the default eleven, in squad order, up to
    /// <see cref="MatchdaySheetRules.MaxSubstitutes"/>. A 16-player squad has a bench of five.
    /// </summary>
    /// <param name="team">The team, already without its unavailable players.</param>
    /// <param name="sheet">The chosen sheet, already checked against <see cref="MatchdaySheetRules"/>, or null.</param>
    /// <returns>The substitutes. Empty when the sheet names none.</returns>
    /// <exception cref="ArgumentException">The sheet names a player who isn't in the squad.</exception>
    public static IReadOnlyList<Player> Bench(TeamProfile team, MatchdaySheet? sheet = null)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (sheet is not null)
        {
            return
            [
                .. (sheet.Substitutes ?? [])
                    .Select(id => team.FindPlayer(id) ?? throw new ArgumentException($"Player '{id}' isn't in {team.Name}'s squad.", nameof(sheet)))
            ];
        }

        var eleven = For(team);
        return [.. team.Squad.Except(eleven).Take(MatchdaySheetRules.MaxSubstitutes)];
    }
}
