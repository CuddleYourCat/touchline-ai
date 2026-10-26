using System.Globalization;
using System.Text.Json.Serialization;
using Touchline.Domain.Matches;

namespace Touchline.Domain.Squad;

/// <summary>
/// The read-only match facts the Analyst's tools can see (ADR-044): a narrow port, so the agent
/// library never depends on the Match Centre's read model or its contracts. The Match Centre
/// implements it over its read model. Every method caps what it returns.
/// </summary>
public interface IMatchFacts
{
    /// <summary>Reads the match's teams, score, clock, team stats and names.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The summary, or null if there's no such match.</returns>
    Task<MatchFactSheet?> GetSummaryAsync(string matchId, CancellationToken cancellationToken);

    /// <summary>Reads the match's key moments so far, latest last, at most <see cref="MatchFactLimits.MaxMoments"/>.</summary>
    /// <param name="matchId">The match.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The moments, empty if there are none or no such match.</returns>
    Task<IReadOnlyList<MomentFact>> GetKeyMomentsAsync(string matchId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the notable events (shots, fouls, cards, set pieces, not every pass) between two
    /// clock minutes, inclusive, at most <see cref="MatchFactLimits.MaxEvents"/>.
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="fromMinute">The first clock minute.</param>
    /// <param name="toMinute">The last clock minute.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events in sequence order, empty if there are none or no such match.</returns>
    Task<IReadOnlyList<EventFact>> GetEventsAsync(string matchId, int fromMinute, int toMinute, CancellationToken cancellationToken);
}

/// <summary>The caps on what <see cref="IMatchFacts"/> returns, so no tool result can blow the input budget.</summary>
public static class MatchFactLimits
{
    /// <summary>The most moments one call returns.</summary>
    public const int MaxMoments = 20;

    /// <summary>The most events one call returns.</summary>
    public const int MaxEvents = 20;
}

/// <summary>A match's headline facts, and the names the Analyst may use.</summary>
/// <param name="MatchId">The match.</param>
/// <param name="HomeTeam">The home team's name.</param>
/// <param name="AwayTeam">The away team's name.</param>
/// <param name="HomeGoals">Home goals so far.</param>
/// <param name="AwayGoals">Away goals so far.</param>
/// <param name="Clock">The match clock, such as <c>67:12</c>.</param>
/// <param name="FullTime">Whether the match has finished.</param>
/// <param name="Home">The home team's stats so far.</param>
/// <param name="Away">The away team's stats so far.</param>
/// <param name="HomePlayers">The home line-up's full names.</param>
/// <param name="AwayPlayers">The away line-up's full names.</param>
/// <param name="OtherKnownNames">Team names and short names, the venue and the referee.</param>
public sealed record MatchFactSheet(
    string MatchId,
    string HomeTeam,
    string AwayTeam,
    int HomeGoals,
    int AwayGoals,
    string Clock,
    bool FullTime,
    TeamStats Home,
    TeamStats Away,
    IReadOnlyList<string> HomePlayers,
    IReadOnlyList<string> AwayPlayers,
    IReadOnlyList<string> OtherKnownNames)
{
    /// <summary>
    /// Every player in both teams' squads (substitutes included), from the team profiles, so a
    /// substitute's name passes the roster guardrail. Empty when there's no profile, and then the
    /// line-ups are the fallback. Left out of the tool result, so it costs the model no input tokens.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> SquadNames { get; init; } = [];

    /// <summary>Every player name the roster guardrail allows in an answer about this match.</summary>
    [JsonIgnore]
    public IEnumerable<string> AllowedPlayerNames =>
        HomePlayers.Concat(AwayPlayers).Concat(SquadNames).Distinct(StringComparer.Ordinal);
}

/// <summary>The IDs the Analyst's tools hand out for an answer to cite.</summary>
public static class MatchFactIds
{
    /// <summary>The match itself, cited for its summary: <c>match:{matchId}</c>.</summary>
    /// <param name="matchId">The match.</param>
    /// <returns>For example <c>match:demo-1</c>.</returns>
    public static string Match(string matchId) => $"match:{matchId}";

    /// <summary>
    /// One match event, <c>event:{matchId}:{sequence}</c>, so a client can tell it apart from a
    /// moment ID (<see cref="MomentIds.For"/>).
    /// </summary>
    /// <param name="matchId">The match.</param>
    /// <param name="sequence">The event's sequence number.</param>
    /// <returns>For example <c>event:demo-1:412</c>.</returns>
    public static string Event(string matchId, long sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"event:{matchId}:{sequence}");
}

/// <summary>One key moment, as a tool sees it.</summary>
/// <param name="Id">The moment ID (<see cref="MomentIds.For"/>), which an answer cites.</param>
/// <param name="Clock">When it happened, such as <c>23:00</c>.</param>
/// <param name="Kind">What it was, such as <c>Goal</c>.</param>
/// <param name="Team">The team it belongs to.</param>
/// <param name="Reason">The detector's plain-words reason.</param>
public sealed record MomentFact(string Id, string Clock, string Kind, TeamSide Team, string Reason);

/// <summary>One match event, as a tool sees it.</summary>
/// <param name="Id">The event's ID (<see cref="MatchFactIds.Event"/>), which an answer cites.</param>
/// <param name="Clock">When it happened.</param>
/// <param name="Type">What it was.</param>
/// <param name="Team">The team that made it.</param>
/// <param name="Player">The player's name, if they're in a line-up.</param>
/// <param name="Outcome">A short outcome, such as a shot's result or a card's colour.</param>
public sealed record EventFact(string Id, string Clock, MatchEventType Type, TeamSide Team, string? Player, string? Outcome);

/// <summary>
/// How a substitution reads as a fact (ADR-064): the player who went off is the event's player,
/// and the outcome names the one who came on.
/// </summary>
public static class SubstitutionFacts
{
    /// <summary>The outcome of a substitution, for an <see cref="EventFact"/>.</summary>
    /// <param name="substitution">The substitution event.</param>
    /// <param name="names">Player names by ID, as the roster spells them.</param>
    /// <returns><c>replaced by {name}</c>, or <c>substituted</c> when the player coming on has no roster name.</returns>
    public static string Outcome(MatchEvent substitution, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(substitution);
        ArgumentNullException.ThrowIfNull(names);
        return substitution.ReplacementPlayerId is { } on && names.TryGetValue(on, out var name)
            ? $"replaced by {name}"
            : "substituted";
    }
}
