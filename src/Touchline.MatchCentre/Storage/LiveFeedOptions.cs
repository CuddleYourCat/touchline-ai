using System.ComponentModel.DataAnnotations;

namespace Touchline.MatchCentre.Storage;

/// <summary>
/// How the Cosmos DB live stream behaves. Bound from <c>MatchCentre:LiveFeed</c>.
/// </summary>
public sealed class LiveFeedOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:LiveFeed";

    /// <summary>
    /// The longest a stream waits for the change feed's first successful poll before it's
    /// refused with a <c>503</c>, so a stream never hangs while Cosmos DB is down.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:02:00")]
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
