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
    /// <exception cref="ArgumentException">A name or ID is missing, or the starting eleven isn't eleven different players.</exception>
    public TeamSheet(string id, string name, string shortName, IReadOnlyList<string> startingEleven)
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
    /// Compares by value, including the players in order. A record's generated equality would
    /// compare the list by reference, so two identical sheets read from JSON would differ.
    /// </summary>
    /// <param name="other">The sheet to compare with.</param>
    /// <returns>True when every field and every starting player matches.</returns>
    public bool Equals(TeamSheet? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && ShortName == other.ShortName
        && StartingEleven.SequenceEqual(other.StartingEleven);

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

        return hash.ToHashCode();
    }
}

/// <summary>
/// The context of a match, carried by the first event of the stream (ADR-019), so every
/// consumer, including stream-only ones such as the overlay feed, starts with the teams and
/// line-ups.
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
    /// <exception cref="ArgumentNullException">A team sheet is missing.</exception>
    /// <exception cref="ArgumentException">The venue is missing, or both sheets are the same team.</exception>
    public MatchMetadata(TeamSheet home, TeamSheet away, string venue, DateTimeOffset kickOffUtc, int seed)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);
        if (home.Id == away.Id)
        {
            throw new ArgumentException($"A team can't play itself ('{home.Id}').", nameof(away));
        }

        Home = home;
        Away = away;
        Venue = DetailRules.Text(venue, nameof(venue));
        KickOffUtc = kickOffUtc.ToUniversalTime();
        Seed = seed;
    }

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
