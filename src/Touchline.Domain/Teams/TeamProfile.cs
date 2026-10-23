namespace Touchline.Domain.Teams;

/// <summary>A player's broad position, used to pick who performs each action.</summary>
public enum PlayerPosition
{
    /// <summary>Goalkeeper.</summary>
    Goalkeeper,

    /// <summary>Defender.</summary>
    Defender,

    /// <summary>Midfielder.</summary>
    Midfielder,

    /// <summary>Forward.</summary>
    Forward
}

/// <summary>The foot a player prefers to strike the ball with (ADR-019).</summary>
public enum PreferredFoot
{
    // Numbered from 1, so a zero (an unset value) is caught by validation rather than read
    // as a real foot.

    /// <summary>Left-footed. The right is the weaker foot.</summary>
    Left = 1,

    /// <summary>Right-footed. The left is the weaker foot.</summary>
    Right = 2,

    /// <summary>Two-footed: neither foot is weaker.</summary>
    Both = 3
}

/// <summary>
/// A fictional player (ADR-002). <see cref="Role"/> describes their type in plain words,
/// for example "inverted winger, left-footed", which the agents can use when explaining
/// a moment. The traits (ADR-019) change what happens in a match, never xG or xPass, which
/// stay average-player measures.
/// </summary>
/// <param name="Id">Stable identifier used in events, for example <c>nbfc-07</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="ShirtNumber">Squad number.</param>
/// <param name="Position">Broad position.</param>
/// <param name="Role">Playing style, in plain words.</param>
/// <param name="PreferredFoot">The foot the player prefers to strike the ball with.</param>
/// <param name="TopSpeedKmh">The player's recorded previous best top speed, in km/h.</param>
/// <param name="Finishing">Shooting quality, from 0 to 1, where 0.5 is an average player.</param>
/// <param name="Passing">Passing quality, from 0 to 1, where 0.5 is an average player.</param>
/// <param name="Discipline">Temperament (ADR-020), from 0 (a hot-head) to 1 (never in trouble), where 0.5 is average.</param>
/// <param name="PenaltyAim">Where the player tends to aim a penalty, or null for the league-average aim.</param>
/// <param name="Dive">A goalkeeper's penalty dive bias, or null for the league average. Goalkeepers only.</param>
public sealed record Player(
    string Id,
    string Name,
    int ShirtNumber,
    PlayerPosition Position,
    string Role,
    PreferredFoot PreferredFoot,
    double TopSpeedKmh,
    double Finishing,
    double Passing,
    double Discipline,
    PenaltyAim? PenaltyAim = null,
    DiveBias? Dive = null);

/// <summary>
/// How often a taker aims a penalty at each sixth of the goal, from the taker's point of view
/// (ADR-020). The weights needn't add up to 1: they're relative.
/// </summary>
/// <param name="LowLeft">Bottom left.</param>
/// <param name="LowCentre">Bottom centre.</param>
/// <param name="LowRight">Bottom right.</param>
/// <param name="HighLeft">Top left.</param>
/// <param name="HighCentre">Top centre.</param>
/// <param name="HighRight">Top right.</param>
public sealed record PenaltyAim(double LowLeft, double LowCentre, double LowRight, double HighLeft, double HighCentre, double HighRight)
{
    /// <summary>The league-average aim: mostly low and to the sides.</summary>
    public static PenaltyAim LeagueAverage { get; } = new(0.22, 0.08, 0.22, 0.14, 0.06, 0.14);

    /// <summary>The weights in <see cref="Pitch.GoalZone"/> order.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<double> Weights => [LowLeft, LowCentre, LowRight, HighLeft, HighCentre, HighRight];
}

/// <summary>
/// How often a goalkeeper dives each way for a penalty, from the <b>taker's</b> point of view
/// (ADR-020). The weights are relative.
/// </summary>
/// <param name="Left">Dives to the taker's left.</param>
/// <param name="Stay">Stays in the middle.</param>
/// <param name="Right">Dives to the taker's right.</param>
public sealed record DiveBias(double Left, double Stay, double Right)
{
    /// <summary>The league-average keeper: either side equally, rarely staying put.</summary>
    public static DiveBias LeagueAverage { get; } = new(0.45, 0.10, 0.45);
}

/// <summary>Kit colours, as CSS colour values.</summary>
/// <param name="Primary">Main shirt colour.</param>
/// <param name="Secondary">Trim colour.</param>
public sealed record TeamColours(string Primary, string Secondary);

/// <summary>
/// How a team likes to play. Each value runs from 0 (not at all) to 1 (as much as
/// possible), and together they set the simulator's action probabilities (ADR-014).
/// </summary>
/// <param name="PressingIntensity">How often and how high the team presses when out of possession.</param>
/// <param name="Width">How much the team uses the wide channels rather than the centre.</param>
/// <param name="Directness">How often the team plays forward quickly rather than keeping the ball.</param>
/// <param name="ShotAppetite">How readily the team shoots rather than working a better chance.</param>
public sealed record TeamTendencies(double PressingIntensity, double Width, double Directness, double ShotAppetite)
{
    /// <summary>An average team: 0.5 for every tendency.</summary>
    public static TeamTendencies Default { get; } = new(0.5, 0.5, 0.5, 0.5);
}

/// <summary>
/// A fictional club, defined as data (ADR-002), so fans can pick a team without any code
/// change. No real club, player, crest or trademark appears in a profile.
/// </summary>
public sealed record TeamProfile
{
    /// <summary>Stable identifier, for example <c>north-bank-fc</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Full club name.</summary>
    public required string Name { get; init; }

    /// <summary>Three-letter abbreviation for scoreboards.</summary>
    public required string ShortName { get; init; }

    /// <summary>The club's fictional home ground, named in the match metadata (ADR-019).</summary>
    public required string Venue { get; init; }

    /// <summary>Kit colours.</summary>
    public required TeamColours Colours { get; init; }

    /// <summary>How the team likes to play.</summary>
    public required TeamTendencies Tendencies { get; init; }

    /// <summary>The squad.</summary>
    public required IReadOnlyList<Player> Squad { get; init; }

    /// <summary>
    /// Finds a squad member by ID.
    /// </summary>
    /// <param name="playerId">The player's ID.</param>
    /// <returns>The player, or null when the ID isn't in this squad.</returns>
    public Player? FindPlayer(string playerId) => Squad.FirstOrDefault(p => p.Id == playerId);
}
