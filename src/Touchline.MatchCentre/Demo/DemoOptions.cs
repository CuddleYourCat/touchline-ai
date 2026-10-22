using System.ComponentModel.DataAnnotations;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// How the demo match is replayed. Bound from <c>MatchCentre:Demo</c>.
/// </summary>
public sealed class DemoOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:Demo";

    /// <summary>
    /// How much faster than real time the match plays. At the default 10, a match takes about
    /// nine and a half minutes.
    /// </summary>
    [Range(0.1, 100_000)]
    public double Speed { get; set; } = 10;

    /// <summary>The pause after full time before the next lap kicks off.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan LapGap { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// How the live stream behaves. Bound from <c>MatchCentre:Stream</c>.
/// </summary>
public sealed class StreamOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:Stream";

    /// <summary>The longest the stream stays quiet before a heartbeat goes out.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:02:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}
