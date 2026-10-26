namespace Touchline.Domain.Matches;

/// <summary>
/// How long the half-time break lasts when a match is played back at a given speed (ADR-066).
/// </summary>
public static class HalfTimeBreak
{
    /// <summary>The half-time break on the match clock, before it is divided by the speed.</summary>
    public static readonly TimeSpan MatchLength = TimeSpan.FromMinutes(15);

    /// <summary>The shortest half-time pause at any speed, so the numbers card can be read.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromSeconds(15);

    /// <summary>The pause at a speed.</summary>
    /// <param name="speed">How many times faster than the match clock the match is played. Must be positive.</param>
    /// <returns>The match-length break divided by the speed, but never less than the floor.</returns>
    public static TimeSpan For(double speed) =>
        TimeSpan.FromTicks(Math.Max((long)(MatchLength.Ticks / speed), Floor.Ticks));
}
