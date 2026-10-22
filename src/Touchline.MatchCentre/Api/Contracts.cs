using System.Globalization;
using Touchline.Domain.Matches;
using Touchline.Domain.Squad;
using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.MatchCentre.Api;

// The Match Centre's wire contract (ADR-027). These records are what the UI sees: the OpenAPI
// document is generated from them and committed, and a test fails when the two drift.
// Domain records appear directly where their shape is already the published one (match
// events, team stats, momentum readings, season cards, commentary).

/// <summary>Where a match is in its life.</summary>
public enum MatchStatus
{
    /// <summary>Kick-off hasn't happened yet.</summary>
    Scheduled,

    /// <summary>In play.</summary>
    Live,

    /// <summary>The final whistle has gone.</summary>
    FullTime
}

/// <summary>
/// A match clock reading, ready to show.
/// </summary>
/// <param name="Period">The half.</param>
/// <param name="Minute">Whole minutes on the clock, counting on from 45 in the second half.</param>
/// <param name="Second">Seconds past the minute.</param>
/// <param name="Display">The reading as shown on screen, for example <c>67:12</c>.</param>
public sealed record ClockReading(MatchPeriod Period, int Minute, int Second, string Display)
{
    /// <summary>Converts a domain match time.</summary>
    /// <param name="time">The match time.</param>
    /// <returns>The clock reading.</returns>
    public static ClockReading From(MatchTime time) =>
        new(time.Period, (int)time.Clock.TotalMinutes, time.Clock.Seconds, time.ToString());
}

/// <summary>
/// The score.
/// </summary>
/// <param name="Home">Home goals.</param>
/// <param name="Away">Away goals.</param>
public sealed record Score(int Home, int Away);

/// <summary>
/// A club, as listed.
/// </summary>
/// <param name="Id">The team ID, used everywhere else. Fan-built teams start with <c>fan-</c>.</param>
/// <param name="Name">The full name.</param>
/// <param name="ShortName">A two-to-four letter name for the scoreboard.</param>
/// <param name="Venue">The home ground.</param>
/// <param name="Colours">The kit colours, as hex.</param>
/// <param name="Origin">Built in, or built by a fan (ADR-031).</param>
/// <param name="CrestUrl">The crest as SVG, relative to the API's host.</param>
public sealed record TeamSummary(string Id, string Name, string ShortName, string Venue, TeamColours Colours, TeamOrigin Origin, string CrestUrl);

/// <summary>
/// A player, as the UI shows one. Ratings and traits stay inside the simulator.
/// </summary>
/// <param name="Id">The player ID used in events.</param>
/// <param name="Name">The display name.</param>
/// <param name="ShirtNumber">The squad number.</param>
/// <param name="Position">The broad position.</param>
/// <param name="TeamId">The player's club.</param>
/// <param name="Archetype">The player's archetype, for a fan-built team; null for a built-in one.</param>
/// <param name="PreferredFoot">The foot the player prefers.</param>
public sealed record PlayerSummary(
    string Id,
    string Name,
    int ShirtNumber,
    PlayerPosition Position,
    string TeamId,
    PlayerArchetype? Archetype,
    PreferredFoot PreferredFoot)
{
    /// <summary>Summarises a built-in squad player.</summary>
    /// <param name="player">The player.</param>
    /// <param name="teamId">The player's club.</param>
    /// <returns>The summary.</returns>
    public static PlayerSummary From(Player player, string teamId)
    {
        ArgumentNullException.ThrowIfNull(player);
        return new(player.Id, player.Name, player.ShirtNumber, player.Position, teamId, null, player.PreferredFoot);
    }
}

/// <summary>
/// A club in full: its manager, crest and squad.
/// </summary>
/// <param name="Team">The club.</param>
/// <param name="Manager">The manager, or null for a built-in team without one.</param>
/// <param name="Crest">How the crest is drawn.</param>
/// <param name="Squad">Every player in the squad, by shirt number.</param>
/// <param name="Version">Increases with every update; sent as the <c>ETag</c>, and expected back in <c>If-Match</c>.</param>
public sealed record TeamDetail(TeamSummary Team, ManagerSpec? Manager, CrestSpec Crest, IReadOnlyList<PlayerSummary> Squad, int Version);

/// <summary>
/// One side of a match, as listed.
/// </summary>
/// <param name="Id">The team ID.</param>
/// <param name="Name">The full name.</param>
/// <param name="ShortName">The scoreboard name.</param>
public sealed record MatchTeam(string Id, string Name, string ShortName);

/// <summary>
/// A match, as listed.
/// </summary>
/// <param name="Id">The match ID.</param>
/// <param name="Kind">Exhibition, fixture or replay.</param>
/// <param name="Status">Scheduled, live or full time.</param>
/// <param name="Home">The home side.</param>
/// <param name="Away">The away side.</param>
/// <param name="Score">The score now.</param>
/// <param name="Clock">The clock now.</param>
/// <param name="KickOffUtc">The kick-off time.</param>
/// <param name="Season">The season, for a fixture.</param>
/// <param name="Round">The round, for a fixture.</param>
/// <param name="DataSource">Always <c>synthetic</c>: every club, player and event is generated.</param>
public sealed record MatchSummary(
    string Id,
    MatchKind Kind,
    MatchStatus Status,
    MatchTeam Home,
    MatchTeam Away,
    Score Score,
    ClockReading Clock,
    DateTimeOffset KickOffUtc,
    string? Season,
    int? Round,
    string DataSource);

/// <summary>
/// The score, clock and totals at a point in a match.
/// </summary>
/// <param name="MatchId">The match.</param>
/// <param name="LastSequence">The last event included. Pass it as <c>after</c> to carry on from here.</param>
/// <param name="Clock">The clock.</param>
/// <param name="Score">The score.</param>
/// <param name="Home">The home side's totals.</param>
/// <param name="Away">The away side's totals.</param>
/// <param name="FullTime">Whether the final whistle has gone.</param>
public sealed record LiveState(
    string MatchId,
    long LastSequence,
    ClockReading Clock,
    Score Score,
    TeamStats Home,
    TeamStats Away,
    bool FullTime)
{
    /// <summary>Converts a domain match state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The live state.</returns>
    public static LiveState From(MatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(state.MatchId, state.LastSequence, ClockReading.From(state.Time),
            new Score(state.Home.Goals, state.Away.Goals), state.Home, state.Away, state.FullTime);
    }
}

/// <summary>
/// A match in full: who, where, and the state now.
/// </summary>
/// <param name="Summary">The listing details.</param>
/// <param name="Venue">The ground.</param>
/// <param name="Referee">The referee's name.</param>
/// <param name="HomeLineup">The home starting eleven.</param>
/// <param name="AwayLineup">The away starting eleven.</param>
/// <param name="State">The score, clock and totals now.</param>
public sealed record MatchDetail(
    MatchSummary Summary,
    string Venue,
    string Referee,
    IReadOnlyList<PlayerSummary> HomeLineup,
    IReadOnlyList<PlayerSummary> AwayLineup,
    LiveState State);

/// <summary>
/// A page of match events.
/// </summary>
/// <param name="Events">The events, in sequence order.</param>
/// <param name="NextAfter">Pass this as <c>after</c> for the next page, or null when there are no more events yet.</param>
public sealed record EventPage(IReadOnlyList<MatchEvent> Events, long? NextAfter);

/// <summary>
/// A key or notable moment, with its commentary for the viewer.
/// </summary>
/// <param name="Id">The moment ID, <c>{matchId}:{sequence}</c>.</param>
/// <param name="Tier">Key or notable.</param>
/// <param name="Kind">What happened, as a key-moment or notable-moment kind name, such as <c>Goal</c>.</param>
/// <param name="Sequence">The triggering event.</param>
/// <param name="Clock">When it happened.</param>
/// <param name="Team">The team the triggering event belongs to.</param>
/// <param name="PlayerId">The main player, if there is one.</param>
/// <param name="Reason">The detector's factual reason, such as <c>xG 0.41 from 11 m</c>.</param>
/// <param name="Commentary">The commentary line for the viewer's mode and language, or null if none is written yet.</param>
public sealed record MomentView(
    string Id,
    MomentTier Tier,
    string Kind,
    long Sequence,
    ClockReading Clock,
    TeamSide Team,
    string? PlayerId,
    string Reason,
    CommentaryLine? Commentary);

/// <summary>
/// A fan's question about a match.
/// </summary>
/// <param name="Question">The question, up to <see cref="MaxLength"/> characters.</param>
public sealed record QuestionRequest(string Question)
{
    /// <summary>The longest question accepted. It bounds the input tokens a question can cost.</summary>
    public const int MaxLength = 280;
}

/// <summary>
/// The Analyst's explanation of a key moment, sent on the stream after its commentary.
/// </summary>
/// <param name="MomentId">The moment explained.</param>
/// <param name="Text">Why it happened.</param>
/// <param name="Citations">The possessions and moments it's based on.</param>
public sealed record MomentAnalysis(string MomentId, string Text, IReadOnlyList<Citation> Citations);

/// <summary>
/// A milestone: a first goal, a brace, a 50th pass, a personal-best speed.
/// </summary>
/// <param name="Id">The milestone ID.</param>
/// <param name="Kind">The milestone kind name.</param>
/// <param name="Sequence">The event that reached it.</param>
/// <param name="Clock">When.</param>
/// <param name="Team">The team.</param>
/// <param name="PlayerId">The player, if it's a personal milestone.</param>
/// <param name="Text">A short description.</param>
public sealed record MilestoneView(string Id, string Kind, long Sequence, ClockReading Clock, TeamSide Team, string? PlayerId, string Text);

/// <summary>
/// What a broadcast overlay card shows.
/// </summary>
/// <param name="Title">The main line, such as <c>GOAL</c>.</param>
/// <param name="Subtitle">A second line, or null.</param>
/// <param name="Team">The team it's about, or null.</param>
/// <param name="PlayerId">The player it's about, or null.</param>
public sealed record OverlayPayload(string Title, string? Subtitle, TeamSide? Team, string? PlayerId);

/// <summary>
/// A timecoded card for a graphical overlay on a live feed.
/// </summary>
/// <param name="Id">The card ID.</param>
/// <param name="At">The match time to show it at.</param>
/// <param name="Type">The card type, such as <c>goal</c>, <c>card</c> or <c>milestone</c>.</param>
/// <param name="DurationMs">How long to show it, in milliseconds.</param>
/// <param name="Payload">What it shows.</param>
public sealed record OverlayCard(string Id, ClockReading At, string Type, int DurationMs, OverlayPayload Payload);

/// <summary>
/// Sent every few seconds when nothing else is, so idle proxies keep the stream open.
/// </summary>
/// <param name="At">The server's time.</param>
public sealed record Heartbeat(DateTimeOffset At);

/// <summary>
/// The <c>event:</c> names on the live stream (ADR-027), and the data each one carries.
/// </summary>
public static class StreamEvents
{
    /// <summary><see cref="LiveState"/>: the score, clock and totals.</summary>
    public const string State = "state";

    /// <summary><see cref="MatchEvent"/>: one match event.</summary>
    public const string Event = "event";

    /// <summary><see cref="MomentView"/>: a key or notable moment.</summary>
    public const string Moment = "moment";

    /// <summary><see cref="CommentaryLine"/>: a line of commentary.</summary>
    public const string Commentary = "commentary";

    /// <summary><see cref="MomentAnalysis"/>: why a key moment happened.</summary>
    public const string Analysis = "analysis";

    /// <summary><see cref="MilestoneView"/>: a milestone.</summary>
    public const string Milestone = "milestone";

    /// <summary><see cref="Touchline.Domain.Analytics.MomentumReading"/>: a closed five-minute window.</summary>
    public const string Momentum = "momentum";

    /// <summary><see cref="OverlayCard"/>: a timecoded overlay card.</summary>
    public const string Overlay = "overlay";

    /// <summary><see cref="LiveState"/>: the final state, sent once.</summary>
    public const string FullTime = "fulltime";

    /// <summary><see cref="Heartbeat"/>: keeps the connection open.</summary>
    public const string Heartbeat = "heartbeat";
}

/// <summary>
/// One message on the live stream.
/// </summary>
/// <param name="Event">The <c>event:</c> name, from <see cref="StreamEvents"/>.</param>
/// <param name="Id">
/// The <c>id:</c>: the last event sequence the client has fully seen once this message
/// arrives, so a reconnect with <c>Last-Event-ID</c> carries on after it. Null for heartbeats.
/// </param>
/// <param name="Data">The payload, serialized as one JSON <c>data:</c> line.</param>
public sealed record LiveMessage(string Event, string? Id, object Data)
{
    /// <summary>The stream ID for an event sequence.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <returns>The sequence as invariant text.</returns>
    public static string IdFor(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);
}
