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

    /// <summary>
    /// A fan match request to play (ADR-050), or null to play <see cref="Matches"/>. The Match
    /// Centre sets it, as <c>Simulator__RequestId</c>, on the one execution it starts. When set,
    /// the request is the whole run: <see cref="Matches"/> is ignored.
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>How many times faster than the match clock a fan match is published. 10 plays one in about 9 minutes.</summary>
    public double RequestSpeed { get; set; } = 10;

    /// <summary>Whether this run plays a fan match request rather than the configured matches.</summary>
    public bool PlaysRequest => !string.IsNullOrWhiteSpace(RequestId);
}

/// <summary>Builds the engine for a fan match request (ADR-050).</summary>
public static class FanMatchPlans
{
    /// <summary>
    /// The simulator for a request: each side's profile (the embedded catalogue for a built-in
    /// team, <see cref="Touchline.Domain.Teams.Builder.FanTeamProfiles.Build"/> for a fan
    /// snapshot), its sheets, its seed, and the request ID as the match ID.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A simulator ready to play.</returns>
    /// <exception cref="ArgumentException">A team can't be found or built, or a sheet doesn't fit its squad.</exception>
    public static MatchSimulator CreateSimulator(FanMatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalogue = TeamProfileCatalog.LoadEmbedded();
        return new MatchSimulator(
            FanMatchRequest.ProfileOf(request.Home, catalogue),
            FanMatchRequest.ProfileOf(request.Away, catalogue),
            request.Seed,
            new MatchOptions
            {
                MatchId = request.Id,
                Kind = MatchKind.Exhibition,
                KickOffUtc = request.KickOffUtc,
                HomeSheet = request.Home.Sheet,
                AwaySheet = request.Away.Sheet
            });
    }
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
    /// The kick-off time in the match's metadata, or null for the default. When null, an
    /// exhibition kicks off now by the wall clock; a fixture or replay keeps the engine's
    /// <see cref="MatchSimulator.DefaultKickOffUtc"/>.
    /// </summary>
    /// <remarks>
    /// Repeatability: an exhibition with no kick-off set gets different metadata on every run,
    /// while its match ID stays the same. A stored match keeps the kick-off from its first run.
    /// Pin this value wherever output must be exactly repeatable.
    /// </remarks>
    public DateTimeOffset? KickOff { get; set; }

    /// <summary>
    /// Builds the simulator for this plan, from the embedded team and referee profiles.
    /// </summary>
    /// <param name="time">The clock that gives an exhibition's kick-off time when <see cref="KickOff"/> is unset.</param>
    /// <param name="unavailable">Players suspended for this round (ADR-025), or null for none.</param>
    /// <returns>A simulator ready to play.</returns>
    /// <exception cref="ArgumentException">A team or referee ID isn't in the catalog, or the plan describes an invalid match.</exception>
    public MatchSimulator CreateSimulator(TimeProvider time, IReadOnlySet<string>? unavailable = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        var teams = TeamProfileCatalog.LoadEmbedded();
        TeamProfile Team(string id) => teams.TryGetValue(id, out var team)
            ? team
            : throw new ArgumentException($"No team profile '{id}'. Known teams: {string.Join(", ", teams.Keys)}.");

        var referee = RefereeId is null ? null
            : RefereeCatalog.LoadEmbedded().SingleOrDefault(r => r.Id == RefereeId)
              ?? throw new ArgumentException($"No referee '{RefereeId}'.");

        return new MatchSimulator(Team(Home), Team(Away), Seed, new MatchOptions
        {
            KickOffUtc = KickOff ?? (Kind == MatchKind.Exhibition ? time.GetUtcNow() : null),
            Kind = Kind,
            Season = Season,
            Round = Round,
            ReplayOf = ReplayOf,
            Referee = referee,
            Unavailable = unavailable ?? new HashSet<string>()
        });
    }
}
