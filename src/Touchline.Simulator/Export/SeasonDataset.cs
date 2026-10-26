using Touchline.Domain.Matches;
using Touchline.Domain.Teams;
using Touchline.Simulator.Engine;

namespace Touchline.Simulator.Export;

/// <summary>One simulated match of the dataset: its seed and every event, in sequence order.</summary>
/// <param name="Seed">The seed it was played with, which is also its round.</param>
/// <param name="Events">Every event of the match, the metadata first.</param>
public sealed record SeasonMatch(int Seed, IReadOnlyList<MatchEvent> Events)
{
    /// <summary>The match context, carried by the first event (ADR-019).</summary>
    public MatchMetadata Metadata => Events[0].Metadata!;

    /// <summary>The match ID stamped on every event.</summary>
    public string MatchId => Events[0].MatchId;
}

/// <summary>
/// The synthetic season the dataset export plays (ADR-063): 38 head-to-head matches between the two
/// catalogue clubs, home and away alternating, seeds 1 to 38. Round N is seed N, so a match can be
/// found from either. Every club and player is fictional.
/// </summary>
public static class SeasonDataset
{
    /// <summary>The season label, which goes into each fixture's ID and metadata.</summary>
    public const string Season = "2026-27";

    /// <summary>The home club of the odd-numbered rounds, and the away club of the even ones.</summary>
    public const string FirstClubId = "north-bank-fc";

    /// <summary>The other club.</summary>
    public const string SecondClubId = "thames-valley-united";

    // The first round's kick-off. Later rounds are a week apart, so the dates derive from the round
    // alone and the export never reads the clock.
    private static readonly DateTimeOffset _firstKickOff = new(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The seeds of the season, one per round.</summary>
    public static IReadOnlyList<int> Seeds { get; } = Enumerable.Range(1, 38).ToList();

    /// <summary>Plays the whole season.</summary>
    /// <param name="teams">The team profiles, as <see cref="TeamProfileCatalog.LoadEmbedded"/> returns them.</param>
    /// <returns>One match per seed, in seed order.</returns>
    public static IReadOnlyList<SeasonMatch> Play(IReadOnlyDictionary<string, TeamProfile> teams) =>
        Seeds.Select(seed => Play(teams, seed)).ToList();

    /// <summary>Plays one round of the season.</summary>
    /// <param name="teams">The team profiles.</param>
    /// <param name="seed">The round, which is also the seed.</param>
    /// <returns>The match, with every event.</returns>
    public static SeasonMatch Play(IReadOnlyDictionary<string, TeamProfile> teams, int seed)
    {
        ArgumentNullException.ThrowIfNull(teams);
        var first = teams[FirstClubId];
        var second = teams[SecondClubId];
        var (home, away) = seed % 2 == 1 ? (first, second) : (second, first);

        var options = new MatchOptions
        {
            Kind = MatchKind.Fixture,
            Season = Season,
            Round = seed,
            KickOffUtc = _firstKickOff.AddDays(7 * (seed - 1)),
        };

        var simulator = new MatchSimulator(home, away, seed, options);
        return new SeasonMatch(seed, simulator.Play().ToList());
    }
}
