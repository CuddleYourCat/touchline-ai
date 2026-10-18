using System.Globalization;
using System.Text.Json.Serialization;

namespace Touchline.Domain.Matches;

/// <summary>Which part of the match an event belongs to.</summary>
public enum MatchPeriod
{
    /// <summary>Clock runs from 0:00 to 45:00 plus added time.</summary>
    FirstHalf,

    /// <summary>Clock runs from 45:00 to 90:00 plus added time.</summary>
    SecondHalf
}

/// <summary>
/// The match clock as a broadcaster shows it. The second half starts at 45:00, so added
/// time at the end of the first half can show a clock beyond 45:00 while still in
/// <see cref="MatchPeriod.FirstHalf"/>.
/// </summary>
public readonly record struct MatchTime : IComparable<MatchTime>
{
    private static readonly TimeSpan _secondHalfStart = TimeSpan.FromMinutes(45);

    // 45 minutes plus the longest added time we'd ever simulate.
    private static readonly TimeSpan _firstHalfLatest = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Creates a match time.
    /// </summary>
    /// <param name="period">The half the event happened in.</param>
    /// <param name="clock">The match clock, for example 63 minutes 12 seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The clock is negative, a first-half clock is 60:00 or later, or a second-half clock is before 45:00.
    /// </exception>
    // [JsonConstructor]: without it, System.Text.Json uses the implicit parameterless
    // constructor and every deserialized time becomes FirstHalf 00:00.
    [JsonConstructor]
    public MatchTime(MatchPeriod period, TimeSpan clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clock, TimeSpan.Zero);

        var valid = period switch
        {
            MatchPeriod.FirstHalf => clock < _firstHalfLatest,
            MatchPeriod.SecondHalf => clock >= _secondHalfStart,
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown period.")
        };
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(nameof(clock), clock, $"{clock} isn't a valid clock reading for the {period}.");
        }

        Period = period;
        Clock = clock;
    }

    /// <summary>The half the event happened in.</summary>
    public MatchPeriod Period { get; }

    /// <summary>The match clock.</summary>
    public TimeSpan Clock { get; }

    /// <summary>
    /// Convenience factory for a clock reading in minutes and seconds.
    /// </summary>
    /// <param name="period">The half.</param>
    /// <param name="minute">Whole minutes on the clock.</param>
    /// <param name="second">Seconds past the minute.</param>
    /// <returns>The match time.</returns>
    public static MatchTime At(MatchPeriod period, int minute, int second = 0) =>
        new(period, TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(second));

    /// <summary>
    /// Orders by half first, then by clock, so first-half added time (45:30) sorts
    /// before the start of the second half (45:00).
    /// </summary>
    /// <param name="other">The time to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(MatchTime other)
    {
        var byPeriod = Period.CompareTo(other.Period);
        return byPeriod != 0 ? byPeriod : Clock.CompareTo(other.Clock);
    }

    /// <summary>The clock as it appears on screen, for example <c>63:12</c>.</summary>
    /// <returns>Minutes and seconds.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Clock.TotalMinutes:00}:{Clock.Seconds:00}");
}
