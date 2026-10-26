using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Touchline.Domain;
using Touchline.Domain.Matches;

namespace Touchline.MatchCentre.Demo;

/// <summary>
/// The recorded demo match: seed 35600, North Bank FC against Thames Valley United, a 3–3 with
/// action from the first minute and a stoppage-time penalty and goal. It's real simulator
/// output, stored as gzipped JSON lines (one event a line, <see cref="DomainJson"/>) and
/// embedded in the assembly. A unit test replays the seed and
/// fails if the engine no longer produces this exact match, so the recording can't drift
/// unnoticed.
/// </summary>
internal static class DemoRecording
{
    /// <summary>The seed the match was played with.</summary>
    public const int Seed = 35600;

    /// <summary>The home team.</summary>
    public const string HomeTeamId = "north-bank-fc";

    /// <summary>The away team.</summary>
    public const string AwayTeamId = "thames-valley-united";

    /// <summary>The embedded resource's name.</summary>
    public const string ResourceName = "demo-match.jsonl.gz";

    private static readonly Lazy<IReadOnlyList<MatchEvent>> _events = new(() =>
    {
        using var stream = typeof(DemoRecording).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing.");
        return Read(stream);
    });

    /// <summary>The recorded events, in sequence order.</summary>
    public static IReadOnlyList<MatchEvent> Events => _events.Value;

    /// <summary>Reads a recording.</summary>
    /// <param name="gzipped">Gzipped JSON lines.</param>
    /// <returns>The events.</returns>
    public static IReadOnlyList<MatchEvent> Read(Stream gzipped)
    {
        using var reader = new StreamReader(new GZipStream(gzipped, CompressionMode.Decompress), Encoding.UTF8);
        var events = new List<MatchEvent>();
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            events.Add(JsonSerializer.Deserialize<MatchEvent>(line, DomainJson.Options)
                ?? throw new InvalidDataException("The recording has an empty line."));
        }

        return events;
    }

    /// <summary>Writes a recording.</summary>
    /// <param name="events">The events, in sequence order.</param>
    /// <param name="destination">Where to write the gzipped JSON lines.</param>
    public static void Write(IEnumerable<MatchEvent> events, Stream destination)
    {
        using var writer = new StreamWriter(new GZipStream(destination, CompressionLevel.SmallestSize), new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (var matchEvent in events)
        {
            writer.WriteLine(JsonSerializer.Serialize(matchEvent, DomainJson.Options));
        }
    }
}
