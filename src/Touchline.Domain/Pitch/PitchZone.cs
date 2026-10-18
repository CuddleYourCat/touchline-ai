using Touchline.Domain.Matches;

namespace Touchline.Domain.Pitch;

/// <summary>Which third of the pitch, seen from the team in possession.</summary>
public enum Third
{
    /// <summary>The third nearest the team's own goal.</summary>
    Defensive,

    /// <summary>The middle third.</summary>
    Middle,

    /// <summary>The third nearest the goal the team is attacking.</summary>
    Attacking
}

/// <summary>Which vertical channel of the pitch, seen from the team in possession.</summary>
public enum Channel
{
    /// <summary>The channel on the attacking team's left.</summary>
    Left,

    /// <summary>The central channel.</summary>
    Centre,

    /// <summary>The channel on the attacking team's right.</summary>
    Right
}

/// <summary>
/// One of the nine zones (three thirds by three channels) the simulator moves the ball
/// between (ADR-014). Zones are relative to the team in possession, so "attacking third,
/// left channel" means the same thing for both teams.
/// </summary>
/// <param name="Third">Which third of the pitch.</param>
/// <param name="Channel">Which channel of the pitch.</param>
public readonly record struct PitchZone(Third Third, Channel Channel)
{
    /// <summary>
    /// Finds the zone a position falls in, from the point of view of the team in possession.
    /// </summary>
    /// <param name="position">Where the ball is.</param>
    /// <param name="inPossession">The team with the ball.</param>
    /// <returns>The zone, relative to that team's direction of attack.</returns>
    public static PitchZone Locate(PitchPosition position, TeamSide inPossession)
    {
        // Measure as if attacking left to right: the away team's view is the home
        // team's, rotated half a turn.
        var forward = inPossession == TeamSide.Home ? position.X : PitchDimensions.Length - position.X;

        // Attacking left to right on a TV picture, the top touchline (Y = 68) is on the
        // attacker's left.
        var fromLeft = inPossession == TeamSide.Home ? PitchDimensions.Width - position.Y : position.Y;

        var third = forward switch
        {
            < PitchDimensions.Length / 3 => Third.Defensive,
            < PitchDimensions.Length * 2 / 3 => Third.Middle,
            _ => Third.Attacking
        };

        var channel = fromLeft switch
        {
            < PitchDimensions.Width / 3 => Channel.Left,
            < PitchDimensions.Width * 2 / 3 => Channel.Centre,
            _ => Channel.Right
        };

        return new PitchZone(third, channel);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Third} third, {Channel} channel";
}
