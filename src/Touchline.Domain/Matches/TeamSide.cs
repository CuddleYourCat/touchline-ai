namespace Touchline.Domain.Matches;

/// <summary>
/// Which of the two teams in a match. The home team attacks towards the far goal line
/// (X = 105) for the whole match. The simulator doesn't switch ends at half-time,
/// which keeps every coordinate comparable.
/// </summary>
public enum TeamSide
{
    /// <summary>The home team, attacking towards X = 105.</summary>
    Home,

    /// <summary>The away team, attacking towards X = 0.</summary>
    Away
}
