using Touchline.Domain.Teams;
using Touchline.Domain.Teams.Builder;

namespace Touchline.MatchCentre.Api;

// The build-your-own-team contract (ADR-031) and fan-started matches (ADR-032).

/// <summary>Where a team came from.</summary>
public enum TeamOrigin
{
    /// <summary>One of the built-in fictional clubs. Read-only.</summary>
    BuiltIn,

    /// <summary>Built by a fan in the team builder.</summary>
    Fan
}

/// <summary>What kind of name is being suggested or checked.</summary>
public enum NameKind
{
    /// <summary>A club name.</summary>
    Team,

    /// <summary>A ground.</summary>
    Venue,

    /// <summary>A manager.</summary>
    Manager,

    /// <summary>A player.</summary>
    Player,

    /// <summary>
    /// Capital letters standing for a club: a short name or crest initials. Checked against real
    /// clubs' abbreviations, so a crest or scoreboard can't imitate one (ADR-031).
    /// </summary>
    Abbreviation
}

/// <summary>
/// How a crest is drawn: the service renders it as SVG, so no image is ever uploaded (ADR-031).
/// </summary>
/// <param name="Shape">The outline.</param>
/// <param name="Emblem">The emblem in the middle.</param>
/// <param name="Initials">One to three capital letters, or empty.</param>
/// <param name="Primary">The main colour, as <c>#rrggbb</c>.</param>
/// <param name="Secondary">The trim colour, as <c>#rrggbb</c>.</param>
public sealed record CrestSpec(CrestShape Shape, CrestEmblem Emblem, string Initials, string Primary, string Secondary);

/// <summary>
/// A manager.
/// </summary>
/// <param name="Name">The manager's name.</param>
/// <param name="Style">How the team plays.</param>
public sealed record ManagerSpec(string Name, ManagerStyle Style);

/// <summary>
/// A player in a team being created or updated.
/// </summary>
/// <param name="Name">The player's name.</param>
/// <param name="ShirtNumber">From 1 to 99, unique in the squad.</param>
/// <param name="Archetype">The type of player, which sets the position and ratings.</param>
/// <param name="PreferredFoot">Left, right or both.</param>
/// <param name="Id">The player's ID when updating an existing player, so their history follows them; leave it out for a new player.</param>
public sealed record PlayerDraft(string Name, int ShirtNumber, PlayerArchetype Archetype, PreferredFoot PreferredFoot, string? Id = null);

/// <summary>
/// A fan-built team, as created or replaced.
/// </summary>
/// <param name="Name">The club's name.</param>
/// <param name="ShortName">Two to four capital letters for the scoreboard.</param>
/// <param name="Venue">The home ground.</param>
/// <param name="Colours">The kit colours.</param>
/// <param name="Crest">The crest.</param>
/// <param name="Manager">The manager.</param>
/// <param name="Squad">The players: see the squad rules in <see cref="TeamBuilderOptions"/>.</param>
public sealed record TeamDraft(
    string Name,
    string ShortName,
    string Venue,
    TeamColours Colours,
    CrestSpec Crest,
    ManagerSpec Manager,
    IReadOnlyList<PlayerDraft> Squad);

/// <summary>
/// A team just created, with the key to edit it.
/// </summary>
/// <param name="Team">The team.</param>
/// <param name="EditKey">
/// The secret that allows updates and deletion, sent back as the <c>Touchline-Edit-Key</c>
/// header. It's shown only once and can't be recovered.
/// </param>
public sealed record TeamCreated(TeamDetail Team, string EditKey);

/// <summary>
/// An archetype, as the builder offers it.
/// </summary>
/// <param name="Id">The archetype.</param>
/// <param name="Position">The position it plays.</param>
/// <param name="Label">A display name.</param>
/// <param name="Description">What the player is like, in a sentence.</param>
public sealed record ArchetypeOption(PlayerArchetype Id, PlayerPosition Position, string Label, string Description);

/// <summary>
/// A manager style, as the builder offers it.
/// </summary>
/// <param name="Id">The style.</param>
/// <param name="Label">A display name.</param>
/// <param name="Description">How the team plays, in a sentence.</param>
public sealed record ManagerStyleOption(ManagerStyle Id, string Label, string Description);

/// <summary>
/// The fewest players a squad needs in one position.
/// </summary>
/// <param name="Position">The position.</param>
/// <param name="Minimum">The fewest players.</param>
public sealed record PositionMinimum(PlayerPosition Position, int Minimum);

/// <summary>
/// The rules a fan-built squad and a matchday sheet must meet.
/// </summary>
/// <param name="MinPlayers">The fewest players in a squad.</param>
/// <param name="MaxPlayers">The most players in a squad.</param>
/// <param name="MinShirtNumber">The lowest shirt number.</param>
/// <param name="MaxShirtNumber">The highest shirt number.</param>
/// <param name="MinimumByPosition">The fewest players in each position.</param>
/// <param name="MaxSubstitutes">The most substitutes on a matchday sheet.</param>
/// <param name="MaxNameLength">The longest name of any kind.</param>
public sealed record SquadRulesView(
    int MinPlayers,
    int MaxPlayers,
    int MinShirtNumber,
    int MaxShirtNumber,
    IReadOnlyList<PositionMinimum> MinimumByPosition,
    int MaxSubstitutes,
    int MaxNameLength);

/// <summary>
/// Everything the team builder offers: the choices and the rules.
/// </summary>
/// <param name="Archetypes">The player archetypes.</param>
/// <param name="ManagerStyles">The manager styles.</param>
/// <param name="CrestShapes">The crest outlines.</param>
/// <param name="CrestEmblems">The crest emblems.</param>
/// <param name="Rules">The squad and sheet rules.</param>
public sealed record TeamBuilderOptions(
    IReadOnlyList<ArchetypeOption> Archetypes,
    IReadOnlyList<ManagerStyleOption> ManagerStyles,
    IReadOnlyList<CrestShape> CrestShapes,
    IReadOnlyList<CrestEmblem> CrestEmblems,
    SquadRulesView Rules);

/// <summary>
/// Suggested fictional names.
/// </summary>
/// <param name="Kind">What they're names for.</param>
/// <param name="Names">The suggestions.</param>
public sealed record NameSuggestions(NameKind Kind, IReadOnlyList<string> Names);

/// <summary>
/// One name to check.
/// </summary>
/// <param name="Kind">What it's a name for.</param>
/// <param name="Name">The name.</param>
public sealed record NameToCheck(NameKind Kind, string Name);

/// <summary>
/// Names to check as a fan types them.
/// </summary>
/// <param name="Names">Up to 30 names.</param>
public sealed record NameCheckRequest(IReadOnlyList<NameToCheck> Names)
{
    /// <summary>The most names one request may check.</summary>
    public const int MaxNames = 30;
}

/// <summary>
/// Whether a name may be used.
/// </summary>
/// <param name="Kind">What it's a name for.</param>
/// <param name="Name">The name.</param>
/// <param name="Allowed">Whether it may be used.</param>
/// <param name="Reason">Why not, in plain words; null when it's allowed.</param>
public sealed record NameVerdict(NameKind Kind, string Name, bool Allowed, string? Reason);

/// <summary>
/// The verdicts on a set of names, in the order they were sent.
/// </summary>
/// <param name="Names">One verdict per name.</param>
public sealed record NameCheckResult(IReadOnlyList<NameVerdict> Names);

/// <summary>
/// A fan's request to play a match (ADR-032). Always an exhibition: it never counts towards a season.
/// </summary>
/// <param name="HomeTeamId">The home team.</param>
/// <param name="AwayTeamId">The away team.</param>
/// <param name="Home">The home team sheet, or null for the engine's default eleven.</param>
/// <param name="Away">The away team sheet, or null for the engine's default eleven.</param>
/// <param name="Seed">A seed to replay a particular match, or null for a random one.</param>
public sealed record MatchRequest(string HomeTeamId, string AwayTeamId, MatchdaySheet? Home = null, MatchdaySheet? Away = null, int? Seed = null);

/// <summary>
/// A fan match accepted and about to kick off.
/// </summary>
/// <param name="MatchId">The new match.</param>
/// <param name="Status">Scheduled until kick-off.</param>
/// <param name="StreamUrl">Where to follow it live.</param>
public sealed record MatchAccepted(string MatchId, MatchStatus Status, string StreamUrl);
