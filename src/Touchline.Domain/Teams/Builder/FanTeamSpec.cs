namespace Touchline.Domain.Teams.Builder;

/// <summary>
/// A fan-built team as the fan describes it (ADR-031): names, colours, a manager with a style,
/// and players with archetypes. No ratings or tendencies: <see cref="FanTeamProfiles.Build"/>
/// supplies those from fixed tables, so the team stays inside the simulator's calibrated
/// ranges (ADR-050). Kept free of any storage or API type, so the Match Centre maps its own
/// stored team onto it.
/// </summary>
/// <param name="Id">The team ID, for example <c>fan-3f9c</c>.</param>
/// <param name="Name">Full club name.</param>
/// <param name="ShortName">Three-letter abbreviation for scoreboards.</param>
/// <param name="Venue">The club's fictional home ground.</param>
/// <param name="Colours">Kit colours.</param>
/// <param name="Manager">The manager, whose style sets the team's tendencies.</param>
/// <param name="Players">The squad.</param>
public sealed record FanTeamSpec(
    string Id,
    string Name,
    string ShortName,
    string Venue,
    TeamColours Colours,
    FanManagerSpec Manager,
    IReadOnlyList<FanPlayerSpec> Players);

/// <summary>A fan team's manager.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Style">How the manager wants the team to play.</param>
public sealed record FanManagerSpec(string Name, ManagerStyle Style);

/// <summary>A fan team's player: the fan picks everything but the ratings.</summary>
/// <param name="Id">Stable player ID, for example <c>fan-3f9c-p7</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="ShirtNumber">Squad number.</param>
/// <param name="PreferredFoot">The foot the player prefers to strike the ball with.</param>
/// <param name="Archetype">The type of player, which sets the position and the ratings.</param>
public sealed record FanPlayerSpec(string Id, string Name, int ShirtNumber, PreferredFoot PreferredFoot, PlayerArchetype Archetype);
