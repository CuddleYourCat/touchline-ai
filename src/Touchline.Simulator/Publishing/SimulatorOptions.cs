using Touchline.Domain.Matches;
using Touchline.Domain.Officials;
using Touchline.Domain.Teams;
using Touchline.Simulator.Engine;

namespace Touchline.Simulator.Publishing;

/// <summary>The simulator worker's configuration: the matches it plays when it starts (ADR-023).</summary>
public sealed class SimulatorOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Simulator";

    /// <summary>The matches to play, one after another.</summary>
    public List<MatchPlan> Matches { get; set; } = [];

    /// <summary>
    /// Whether to stop the host once every match is played. On for the Container Apps job, so
    /// an execution ends with its matches (ADR-024). Off locally, where the worker idles.
    /// </summary>
    public bool ExitWhenDone { get; set; }
}

/// <summary>One match for the worker to play and publish.</summary>
public sealed class MatchPlan
{
    /// <summary>The seed. The same seed and plan replay the same match.</summary>
    public int Seed { get; set; }

    /// <summary>The home team's profile ID.</summary>
    public string Home { get; set; } = "north-bank-fc";

    /// <summary>The away team's profile ID.</summary>
    public string Away { get; set; } = "thames-valley-united";

    /// <summary>Whether the match counts towards the season. An exhibition unless told otherwise.</summary>
    public MatchKind Kind { get; set; } = MatchKind.Exhibition;

    /// <summary>The season, for a fixture.</summary>
    public string? Season { get; set; }

    /// <summary>The round, for a fixture.</summary>
    public int? Round { get; set; }

    /// <summary>For a replay, the match ID of the fixture it re-runs.</summary>
    public string? ReplayOf { get; set; }

    /// <summary>A referee's ID, or null for the seed's referee.</summary>
    public string? RefereeId { get; set; }

    /// <summary>How many times faster than the match clock to publish: 10 plays a match in about 9 minutes, 1 in real time.</summary>
    public double Speed { get; set; } = 10;

    /// <summary>
    /// Builds the simulator for this plan, from the embedded team and referee profiles.
    /// </summary>
    /// <returns>A simulator ready to play.</returns>
    /// <exception cref="ArgumentException">A team or referee ID isn't in the catalog, or the plan describes an invalid match.</exception>
    public MatchSimulator CreateSimulator()
    {
        var teams = TeamProfileCatalog.LoadEmbedded();
        TeamProfile Team(string id) => teams.TryGetValue(id, out var team)
            ? team
            : throw new ArgumentException($"No team profile '{id}'. Known teams: {string.Join(", ", teams.Keys)}.");

        var referee = RefereeId is null ? null
            : RefereeCatalog.LoadEmbedded().SingleOrDefault(r => r.Id == RefereeId)
              ?? throw new ArgumentException($"No referee '{RefereeId}'.");

        return new MatchSimulator(Team(Home), Team(Away), Seed, new MatchOptions
        {
            Kind = Kind,
            Season = Season,
            Round = Round,
            ReplayOf = ReplayOf,
            Referee = referee
        });
    }
}
