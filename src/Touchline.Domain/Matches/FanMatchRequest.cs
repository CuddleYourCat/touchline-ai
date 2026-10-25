using System.Globalization;
using System.Security.Cryptography;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.Domain.Matches;

/// <summary>Where a fan match request is in its life (ADR-032, ADR-050).</summary>
public enum FanMatchStatus
{
    /// <summary>Written by the Match Centre; the job hasn't picked it up yet.</summary>
    Scheduled,

    /// <summary>The simulator job is playing it.</summary>
    Running,

    /// <summary>Every event has been published.</summary>
    Finished,

    /// <summary>The job couldn't be started, or the match couldn't be played.</summary>
    Failed
}

/// <summary>Whether a fan match request still holds its teams (see <see cref="FanMatchRequest.LivenessAt"/>).</summary>
public enum FanMatchLiveness
{
    /// <summary>Scheduled and within its pick-up deadline, or running within the length of a match.</summary>
    Active,

    /// <summary>Still scheduled after its pick-up deadline: the job was started but never ran it, so it should be marked failed.</summary>
    NotPickedUp,

    /// <summary>Finished, failed, or running for longer than any match can.</summary>
    Over
}

/// <summary>
/// One side of a fan match, frozen when the match was requested.
/// </summary>
/// <param name="TeamId">The team's ID, built-in or <c>fan-</c>.</param>
/// <param name="Team">
/// A snapshot of a fan team, or null for a built-in team, which comes from the embedded catalogue.
/// The snapshot is the roster of record for this match: an edit or a delete of the team after the
/// request can't change the match, its names or its bench.
/// </param>
/// <param name="Sheet">The chosen eleven and bench, or null for the engine's default eleven.</param>
public sealed record FanMatchSide(string TeamId, FanTeamSpec? Team, MatchdaySheet? Sheet);

/// <summary>
/// A fan's request to play an exhibition (ADR-032, ADR-050), as the <c>match-requests</c> Cosmos DB
/// container holds it, partitioned by <c>/id</c>. The Match Centre writes it before starting the
/// simulator job, the job reads it and moves its status on, and the Event Processor and the Match
/// Centre read its snapshots to name the players.
/// </summary>
/// <param name="Id">The request ID, which is also the match ID (see <see cref="FanMatchIds"/>).</param>
/// <param name="CreatedUtc">When the request was written.</param>
/// <param name="Seed">The seed the engine plays with.</param>
/// <param name="KickOffUtc">The kick-off time stamped on the match's metadata.</param>
/// <param name="Home">The home side.</param>
/// <param name="Away">The away side.</param>
/// <param name="Status">Where the request is.</param>
/// <param name="Reason">Why it failed, in a few plain words; null otherwise.</param>
/// <param name="UpdatedUtc">When the status last changed, or null if it never has.</param>
public sealed record FanMatchRequest(
    string Id,
    DateTimeOffset CreatedUtc,
    int Seed,
    DateTimeOffset KickOffUtc,
    FanMatchSide Home,
    FanMatchSide Away,
    FanMatchStatus Status,
    string? Reason = null,
    DateTimeOffset? UpdatedUtc = null)
{
    /// <summary>The longest failure reason stored, so a reason can never grow the document.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>
    /// The profile one side plays as: its fan snapshot built by <see cref="FanTeamProfiles.Build"/>,
    /// or its built-in profile from the catalogue.
    /// </summary>
    /// <param name="side">The side.</param>
    /// <param name="catalogue">The built-in profiles, by team ID.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ArgumentException">
    /// A built-in team isn't in the catalogue, or a snapshot breaks the fan-team rules.
    /// </exception>
    public static TeamProfile ProfileOf(FanMatchSide side, IReadOnlyDictionary<string, TeamProfile> catalogue)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(catalogue);

        if (side.Team is { } snapshot)
        {
            return snapshot.Id == side.TeamId
                ? FanTeamProfiles.Build(snapshot)
                : throw new ArgumentException($"The snapshot is of '{snapshot.Id}', not '{side.TeamId}'.", nameof(side));
        }

        return catalogue.TryGetValue(side.TeamId, out var builtIn)
            ? builtIn
            : throw new ArgumentException($"No built-in team '{side.TeamId}', and no snapshot of it.", nameof(side));
    }

    /// <summary>
    /// The profiles that name this match's players: the catalogue, plus each fan side's snapshot.
    /// Fan IDs start <c>fan-</c> and built-in ones never do, so nothing in the catalogue is replaced.
    /// </summary>
    /// <param name="catalogue">The built-in profiles, by team ID.</param>
    /// <returns>The profiles, by team ID.</returns>
    /// <exception cref="ArgumentException">A snapshot breaks the fan-team rules.</exception>
    public IReadOnlyDictionary<string, TeamProfile> TeamsWith(IReadOnlyDictionary<string, TeamProfile> catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        var teams = new Dictionary<string, TeamProfile>(catalogue, StringComparer.Ordinal);
        foreach (var side in new[] { Home, Away }.Where(s => s.Team is not null))
        {
            teams[side.TeamId] = ProfileOf(side, catalogue);
        }

        return teams;
    }

    /// <summary>
    /// Whether the request still holds its teams at a given time. A scheduled request has a short
    /// pick-up deadline: if no execution has moved it to running by then, the start was accepted
    /// but never ran, and it no longer counts. A running one counts for the length of a match.
    /// </summary>
    /// <param name="now">The time now.</param>
    /// <param name="scheduledFor">How long a scheduled request waits to be picked up, beyond the job's cold start.</param>
    /// <param name="runningFor">How long after it was created a running request counts, beyond the job's replica timeout.</param>
    /// <returns>Where the request stands.</returns>
    public FanMatchLiveness LivenessAt(DateTimeOffset now, TimeSpan scheduledFor, TimeSpan runningFor) => Status switch
    {
        FanMatchStatus.Scheduled => now - CreatedUtc < scheduledFor ? FanMatchLiveness.Active : FanMatchLiveness.NotPickedUp,
        FanMatchStatus.Running => now - CreatedUtc < runningFor ? FanMatchLiveness.Active : FanMatchLiveness.Over,
        _ => FanMatchLiveness.Over,
    };

    /// <summary>Trims a failure reason to <see cref="MaxReasonLength"/>.</summary>
    /// <param name="reason">The reason, or null.</param>
    /// <returns>The reason, at most <see cref="MaxReasonLength"/> characters.</returns>
    public static string? Shorten(string? reason) =>
        reason is { Length: > MaxReasonLength } ? reason[..MaxReasonLength] : reason;
}

/// <summary>
/// Fan match IDs (ADR-050): <c>exh-{yyyyMMddHHmm}-{8 hex}</c>. Lower-case letters, digits and
/// hyphens only, so an ID is a valid Cosmos DB <c>id</c> and a safe Azure AI Search key, and the
/// prefix tells every reader, without a lookup, that a match has a request with its rosters. A
/// built-in exhibition's ID is <c>{home}-{away}-{seed}</c>, and no built-in team ID starts
/// <c>exh-</c>, so the two never meet.
/// </summary>
public static class FanMatchIds
{
    /// <summary>Every fan match ID starts with this.</summary>
    public const string Prefix = "exh-";

    /// <summary>A new ID, with 32 random bits after the minute.</summary>
    /// <param name="now">The time the request is made.</param>
    /// <returns>For example <c>exh-202610241530-9f3c01ab</c>.</returns>
    public static string New(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{now.UtcDateTime:yyyyMMddHHmm}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");

    /// <summary>Whether a match ID is a fan match's, so it has a request to read.</summary>
    /// <param name="matchId">The match ID, or null.</param>
    /// <returns>True when it has the shape <see cref="New"/> gives.</returns>
    public static bool IsFanMatch(string? matchId) =>
        matchId is { Length: 25 }
        && matchId.StartsWith(Prefix, StringComparison.Ordinal)
        && matchId[4..16].All(char.IsAsciiDigit)
        && matchId[16] == '-'
        && matchId[17..].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
