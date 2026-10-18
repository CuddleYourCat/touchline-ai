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

/// <summary>
/// A fictional player (ADR-002). <see cref="Role"/> describes their type in plain words,
/// for example "inverted winger, left-footed", which the agents can use when explaining
/// a moment.
/// </summary>
/// <param name="Id">Stable identifier used in events, for example <c>nbfc-07</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="ShirtNumber">Squad number.</param>
/// <param name="Position">Broad position.</param>
/// <param name="Role">Playing style, in plain words.</param>
public sealed record Player(string Id, string Name, int ShirtNumber, PlayerPosition Position, string Role);

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
public sealed record TeamTendencies(double PressingIntensity, double Width, double Directness, double ShotAppetite);

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
