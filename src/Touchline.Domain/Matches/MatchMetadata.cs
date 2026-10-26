namespace Touchline.Domain.Matches;

/// <summary>One team's entry in the match metadata.</summary>
public sealed record TeamSheet
{
    private const int _startingPlayers = 11;

    /// <summary>
    /// Creates a team sheet.
    /// </summary>
    /// <param name="id">The team profile ID, for example <c>north-bank-fc</c>.</param>
    /// <param name="name">Full club name.</param>
    /// <param name="shortName">Three-letter abbreviation.</param>
    /// <param name="startingEleven">The IDs of the eleven players who start, goalkeeper first.</param>
    /// <param name="substitutes">
    /// The IDs of the bench (ADR-064), in sheet order. Null or missing (a document stored before
    /// substitutions) reads as an empty bench.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A name or ID is missing, the starting eleven isn't eleven different players, or the bench
    /// names a blank, repeated or starting player.
    /// </exception>
    public TeamSheet(string id, string name, string shortName, IReadOnlyList<string> startingEleven, IReadOnlyList<string>? substitutes = null)
    {
        Id = DetailRules.Text(id, nameof(id));
        Name = DetailRules.Text(name, nameof(name));
        ShortName = DetailRules.Text(shortName, nameof(shortName));

        if (startingEleven is null
            || startingEleven.Count != _startingPlayers
            || startingEleven.Any(string.IsNullOrWhiteSpace)
            || startingEleven.Distinct().Count() != _startingPlayers)
        {
            throw new ArgumentException($"A starting eleven must be {_startingPlayers} different player IDs.", nameof(startingEleven));
        }

        StartingEleven = [.. startingEleven];

        substitutes ??= [];
        if (substitutes.Any(string.IsNullOrWhiteSpace)
            || substitutes.Distinct().Count() != substitutes.Count
            || substitutes.Any(StartingEleven.Contains))
        {
            throw new ArgumentException("The substitutes must be different player IDs, none of them starting.", nameof(substitutes));
        }

        Substitutes = [.. substitutes];
    }

    /// <summary>The team profile ID.</summary>
    public string Id { get; }

    /// <summary>Full club name.</summary>
    public string Name { get; }

    /// <summary>Three-letter abbreviation.</summary>
    public string ShortName { get; }

    /// <summary>The IDs of the players who start, goalkeeper first.</summary>
    public IReadOnlyList<string> StartingEleven { get; }

    /// <summary>
    /// The IDs of the substitutes on the bench (ADR-064), in sheet order. Empty for a sheet with
    /// no bench, and for a document stored before substitutions.
    /// </summary>
    public IReadOnlyList<string> Substitutes { get; }

    /// <summary>
    /// Compares by value, including the players in order. A record's generated equality would
    /// compare the lists by reference, so two identical sheets read from JSON would differ.
    /// </summary>
    /// <param name="other">The sheet to compare with.</param>
    /// <returns>True when every field, every starting player and every substitute matches.</returns>
    public bool Equals(TeamSheet? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && ShortName == other.ShortName
        && StartingEleven.SequenceEqual(other.StartingEleven)
        && Substitutes.SequenceEqual(other.Substitutes);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(ShortName);
        foreach (var player in StartingEleven)
        {
            hash.Add(player);
        }

        // The count keeps the two lists apart.
        hash.Add(Substitutes.Count);
        foreach (var player in Substitutes)
        {
            hash.Add(player);
        }

        return hash.ToHashCode();
    }
}

/// <summary>The referee named in the match metadata (ADR-020).</summary>
/// <param name="Id">The referee profile ID, for example <c>ref-pell</c>.</param>
/// <param name="Name">Display name.</param>
public sealed record RefereeSheet(string Id, string Name);

/// <summary>What kind of match this is, which decides whether it counts towards the season (ADR-020, ADR-021).</summary>
public enum MatchKind
{
    /// <summary>A demo or test match. Never counts. The default, so nothing pollutes the season by accident.</summary>
    Exhibition,

    /// <summary>An official match. Counts towards the season.</summary>
    Fixture,

    /// <summary>A re-run of a fixture. Never counts, and names the original.</summary>
    Replay
}

/// <summary>
/// The context of a match, carried by the first event of the stream (ADR-019), so every
/// consumer, including stream-only ones such as the overlay feed, starts with the teams,
/// line-ups and referee, and knows whether the match counts (ADR-020).
/// </summary>
public sealed record MatchMetadata
{
    /// <summary>
    /// Creates match metadata.
    /// </summary>
    /// <param name="home">The home team.</param>
    /// <param name="away">The away team.</param>
    /// <param name="venue">The home team's fictional ground.</param>
    /// <param name="kickOffUtc">The scheduled kick-off time. It's stored in UTC whatever offset it's given in.</param>
    /// <param name="seed">The simulator seed, which replays the identical match.</param>
    /// <param name="referee">Who is refereeing.</param>
    /// <param name="kind">Whether the match counts towards the season.</param>
    /// <param name="season">The season, for example "2026-27". Required on a fixture.</param>
    /// <param name="round">The round of the season, from 1. Required on a fixture.</param>
    /// <param name="replayOf">The match ID of the fixture being replayed. Required on a replay, and only allowed there.</param>
    /// <exception cref="ArgumentNullException">A team sheet or the referee is missing.</exception>
    /// <exception cref="ArgumentException">
    /// The venue is missing, both sheets are the same team, or the season details don't match the kind of match.
    /// </exception>
    public MatchMetadata(
        TeamSheet home,
        TeamSheet away,
        string venue,
        DateTimeOffset kickOffUtc,
        int seed,
        RefereeSheet referee,
        MatchKind kind,
        string? season = null,
        int? round = null,
        string? replayOf = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);
        ArgumentNullException.ThrowIfNull(referee);
        if (home.Id == away.Id)
        {
            throw new ArgumentException($"A team can't play itself ('{home.Id}').", nameof(away));
        }

        DetailRules.Text(referee.Id, "referee.Id");
        DetailRules.Text(referee.Name, "referee.Name");

        var problem = DetailRules.Defined(kind, nameof(kind)) switch
        {
            MatchKind.Fixture when string.IsNullOrWhiteSpace(season) || round is not >= 1 => "a fixture needs a season and a round from 1",
            // A season label on an exhibition is almost always a fixture with its kind forgotten,
            // which would silently leave that round out of the season.
            MatchKind.Exhibition when season is not null || round is not null => "an exhibition has no season or round; did you mean a fixture?",
            not MatchKind.Replay when replayOf is not null => $"only a replay names the match it replays, not a {kind}",
            MatchKind.Replay when string.IsNullOrWhiteSpace(replayOf) => "a replay must name the match it replays",
            _ => null
        };
        if (problem is not null)
        {
            throw new ArgumentException($"The match metadata is invalid: {problem}.", nameof(kind));
        }

        Home = home;
        Away = away;
        Venue = DetailRules.Text(venue, nameof(venue));
        KickOffUtc = kickOffUtc.ToUniversalTime();
        Seed = seed;
        Referee = referee;
        Kind = kind;
        Season = season;
        Round = round;
        ReplayOf = replayOf;
    }

    /// <summary>Who is refereeing.</summary>
    public RefereeSheet Referee { get; }

    /// <summary>Whether the match counts towards the season. Only a <see cref="MatchKind.Fixture"/> does.</summary>
    public MatchKind Kind { get; }

    /// <summary>The season, for example "2026-27", or null for an exhibition.</summary>
    public string? Season { get; }

    /// <summary>The round of the season, from 1, or null for an exhibition.</summary>
    public int? Round { get; }

    /// <summary>For a replay, the match ID of the fixture it re-runs.</summary>
    public string? ReplayOf { get; }

    /// <summary>The home team.</summary>
    public TeamSheet Home { get; }

    /// <summary>The away team.</summary>
    public TeamSheet Away { get; }

    /// <summary>The home team's fictional ground.</summary>
    public string Venue { get; }

    /// <summary>The scheduled kick-off time, in UTC.</summary>
    public DateTimeOffset KickOffUtc { get; }

    /// <summary>The simulator seed, which replays the identical match.</summary>
    public int Seed { get; }
}
