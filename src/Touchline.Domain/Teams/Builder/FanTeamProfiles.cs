namespace Touchline.Domain.Teams.Builder;

/// <summary>
/// The hidden ratings of one archetype (ADR-050): what a <see cref="Player"/> needs beyond the
/// fan's choices. Never shown in the API.
/// </summary>
/// <param name="Role">The playing style in plain words, as a built-in player's role reads.</param>
/// <param name="TopSpeedKmh">The recorded previous best top speed, in km/h.</param>
/// <param name="Finishing">Shooting quality, from 0 to 1.</param>
/// <param name="Passing">Passing quality, from 0 to 1.</param>
/// <param name="Discipline">Temperament, from 0 (a hot-head) to 1.</param>
internal sealed record ArchetypeRatings(string Role, double TopSpeedKmh, double Finishing, double Passing, double Discipline);

/// <summary>
/// Turns a fan-built team into a <see cref="TeamProfile"/> the simulator can play (ADR-050).
/// Pure and deterministic: the same spec always builds the same profile.
/// </summary>
/// <remarks>
/// Every rating and tendency comes from a fixed table, and every value in the tables lies
/// inside the range the built-in profiles span (for players, per position), so a fan team
/// can be unusual but never outside the calibrated game. A unit test recomputes those
/// ranges from the catalog, so it follows any edit to the built-in profiles.
/// </remarks>
public static class FanTeamProfiles
{
    // The parameter at fault when a player entry is empty: the list on Build's spec.
    private const string _playersParameter = "spec." + nameof(FanTeamSpec.Players);

    /// <summary>
    /// Builds a playable profile from a fan team.
    /// </summary>
    /// <param name="spec">The fan team.</param>
    /// <returns>The profile, which passes the same checks as the embedded catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An archetype or the manager's style isn't defined.</exception>
    /// <exception cref="ArgumentException">
    /// The spec has no players or manager, breaks the fan-team rules (an ID starting
    /// <see cref="SquadRules.FanIdPrefix"/>, and <see cref="SquadRules"/>), or the built profile
    /// fails the catalog's checks. The message lists every problem.
    /// </exception>
    public static TeamProfile Build(FanTeamSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var players = spec.Players ?? throw new ArgumentException("A fan team needs a list of players.", nameof(spec));
        var manager = spec.Manager ?? throw new ArgumentException("A fan team needs a manager.", nameof(spec));

        var profile = new TeamProfile
        {
            Id = spec.Id,
            Name = spec.Name,
            ShortName = spec.ShortName,
            Venue = spec.Venue,
            Colours = spec.Colours,
            Tendencies = TendenciesOf(manager.Style),
            Squad = [.. players.Select(ToPlayer)]
        };

        // The Match Centre checks a draft before it's stored, but the builder holds the same line,
        // so a team that reaches the engine by another route still meets the rules (ADR-050).
        var problems = new List<string>();
        if (spec.Id?.StartsWith(SquadRules.FanIdPrefix, StringComparison.Ordinal) != true)
        {
            problems.Add($"the team ID must start with '{SquadRules.FanIdPrefix}'");
        }

        problems.AddRange(SquadRules.Problems([.. profile.Squad.Select(p => (p.ShirtNumber, p.Position))]));
        problems.AddRange(TeamProfileCatalog.Problems(profile));
        return problems.Count == 0
            ? profile
            : throw new ArgumentException($"Fan team '{spec.Id}' can't be played: {string.Join("; ", problems)}.", nameof(spec));
    }

    // Penalty aim and dive bias are left null, which means the league average (ADR-020), as for
    // most built-in players.
    private static Player ToPlayer(FanPlayerSpec? spec)
    {
        if (spec is null)
        {
            throw new ArgumentException("A fan team's player list has an empty entry.", _playersParameter);
        }

        var ratings = RatingsOf(spec.Archetype);
        return new Player(
            spec.Id,
            spec.Name,
            spec.ShirtNumber,
            Archetypes.PositionOf(spec.Archetype),
            ratings.Role,
            spec.PreferredFoot,
            ratings.TopSpeedKmh,
            ratings.Finishing,
            ratings.Passing,
            ratings.Discipline);
    }

    /// <summary>
    /// The fixed ratings of each archetype. Built-in ranges per position, which every value
    /// sits inside: goalkeepers 29.1-30.1 km/h, finishing 0.05, passing 0.45-0.70, discipline
    /// 0.7; defenders 31.4-34.9, 0.10-0.23, 0.37-0.62, 0.2-0.7; midfielders 31.0-33.9,
    /// 0.23-0.50, 0.49-0.75, 0.2-0.7; forwards 31.5-36.6, 0.43-0.68, 0.35-0.57, 0.3-0.6.
    /// </summary>
    /// <param name="archetype">The archetype.</param>
    /// <returns>Its ratings.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The archetype isn't defined.</exception>
    internal static ArchetypeRatings RatingsOf(PlayerArchetype archetype) => archetype switch
    {
        PlayerArchetype.ShotStopper => new("shot-stopping goalkeeper who stays on the line", 29.6, 0.05, 0.48, 0.7),
        PlayerArchetype.SweeperKeeper => new("sweeper keeper, comfortable on the ball", 30.0, 0.05, 0.66, 0.7),
        PlayerArchetype.Stopper => new("stopper centre-back who wins tackles and headers", 32.4, 0.18, 0.40, 0.45),
        PlayerArchetype.BallPlayingDefender => new("ball-playing centre-back who starts attacks", 31.8, 0.12, 0.60, 0.6),
        PlayerArchetype.AttackingFullBack => new("attacking full-back who overlaps and crosses", 34.6, 0.20, 0.50, 0.55),
        PlayerArchetype.BallWinner => new("ball-winning midfielder who presses", 32.8, 0.25, 0.51, 0.4),
        PlayerArchetype.DeepPlaymaker => new("deep-lying playmaker", 31.2, 0.27, 0.73, 0.65),
        PlayerArchetype.BoxToBox => new("box-to-box midfielder who runs both ways", 33.6, 0.42, 0.55, 0.5),
        PlayerArchetype.AttackingMidfielder => new("attacking midfielder who plays between the lines", 32.4, 0.48, 0.70, 0.6),
        PlayerArchetype.Winger => new("winger who beats players and crosses", 36.2, 0.46, 0.50, 0.55),
        PlayerArchetype.InsideForward => new("inside forward who cuts in to shoot", 35.6, 0.60, 0.50, 0.55),
        PlayerArchetype.TargetForward => new("target forward who holds the ball up and wins headers", 31.8, 0.54, 0.40, 0.4),
        PlayerArchetype.Poacher => new("poacher who lives in the box", 33.0, 0.66, 0.38, 0.5),
        _ => throw new ArgumentOutOfRangeException(nameof(archetype), archetype, "Unknown archetype.")
    };

    /// <summary>
    /// The fixed tendencies of each manager style (ADR-014). Built-in ranges, which every value
    /// sits inside: pressing 0.45-0.8, width 0.5-0.7, directness 0.4-0.65, shot appetite 0.5-0.55.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>Its tendencies.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The style isn't defined.</exception>
    internal static TeamTendencies TendenciesOf(ManagerStyle style) => style switch
    {
        ManagerStyle.Balanced => new(PressingIntensity: 0.6, Width: 0.6, Directness: 0.5, ShotAppetite: 0.52),
        ManagerStyle.HighPress => new(PressingIntensity: 0.8, Width: 0.65, Directness: 0.5, ShotAppetite: 0.55),
        ManagerStyle.Possession => new(PressingIntensity: 0.6, Width: 0.6, Directness: 0.4, ShotAppetite: 0.5),
        ManagerStyle.Counter => new(PressingIntensity: 0.45, Width: 0.5, Directness: 0.65, ShotAppetite: 0.5),
        ManagerStyle.Direct => new(PressingIntensity: 0.5, Width: 0.5, Directness: 0.65, ShotAppetite: 0.55),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown manager style.")
    };
}
