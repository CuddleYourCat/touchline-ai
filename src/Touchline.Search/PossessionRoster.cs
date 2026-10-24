using Touchline.Domain.Matches;
using Touchline.Domain.Teams;

namespace Touchline.Search;

/// <summary>
/// The names a match's possession documents may use (ADR-056): the two clubs, and every player on
/// their team profiles. Text about a possession is built from these names only, and the roster
/// guardrail checks model text against them, so a model can't slip a real player into the index.
/// </summary>
/// <param name="HomeName">The home club's display name.</param>
/// <param name="AwayName">The away club's display name.</param>
/// <param name="PlayerNames">Player display names by player ID. An ID not here is unknown, and its player is left out.</param>
/// <param name="OtherKnownNames">Other names the text may use: the clubs' names and short names, the venue and the referee.</param>
public sealed record PossessionRoster(
    string HomeName,
    string AwayName,
    IReadOnlyDictionary<string, string> PlayerNames,
    IReadOnlyList<string> OtherKnownNames)
{
    /// <summary>Builds the roster of a stored match from its metadata and the team profiles.</summary>
    /// <param name="metadata">The match's metadata; null when the match has none stored, which gives <c>Home</c>, <c>Away</c> and no players.</param>
    /// <param name="teams">The team profiles to name players from; the embedded catalogue if null.</param>
    /// <returns>The roster.</returns>
    public static PossessionRoster From(MatchMetadata? metadata, IReadOnlyDictionary<string, TeamProfile>? teams = null)
    {
        if (metadata is null)
        {
            return new PossessionRoster("Home", "Away", new Dictionary<string, string>(StringComparer.Ordinal), ["Home", "Away"]);
        }

        teams ??= TeamProfileCatalog.LoadEmbedded();
        var names = new[] { metadata.Home.Id, metadata.Away.Id }
            .Select(id => teams.GetValueOrDefault(id))
            .OfType<TeamProfile>()
            .SelectMany(profile => profile.Squad)
            .GroupBy(player => player.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

        string[] other =
        [
            metadata.Home.Name, metadata.Home.ShortName, metadata.Away.Name, metadata.Away.ShortName,
            metadata.Venue, metadata.Referee.Name
        ];
        return new PossessionRoster(metadata.Home.Name, metadata.Away.Name, names, [.. other.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The display name of a team.</summary>
    /// <param name="side">Home or away.</param>
    /// <returns>The club's name.</returns>
    public string NameOf(TeamSide side) => side == TeamSide.Home ? HomeName : AwayName;

    /// <summary>The display name of a player.</summary>
    /// <param name="playerId">The player's ID, or null.</param>
    /// <returns>The name, or null when the ID is missing or unknown.</returns>
    public string? NameOf(string? playerId) => playerId is null ? null : PlayerNames.GetValueOrDefault(playerId);
}
