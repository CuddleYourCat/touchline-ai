namespace Touchline.Domain.Storage;

/// <summary>
/// The Cosmos DB database and container names every service shares (ADR-023, ADR-025). Kept here,
/// in the one project both the simulator and the Event Processor reference, so a rename can't
/// leave one side reading a container the other no longer writes. The AppHost and
/// <c>infra/main.bicep</c> create the same names.
/// </summary>
public static class CosmosNames
{
    /// <summary>The database.</summary>
    public const string Database = "touchline";

    /// <summary>Every event, one document per event, partitioned by <c>/matchId</c>.</summary>
    public const string Events = "events";

    /// <summary>Each match's metadata and live state, partitioned by <c>/matchId</c>.</summary>
    public const string Matches = "matches";

    /// <summary>Each player's part in each fixture, partitioned by <c>/playerId</c>.</summary>
    public const string PlayerMatches = "player-matches";

    /// <summary>Each player's rebuilt season, partitioned by <c>/season</c>.</summary>
    public const string PlayerSeasons = "player-seasons";

    /// <summary>Each key moment's commentary lines, partitioned by <c>/matchId</c> (ADR-029).</summary>
    public const string Commentary = "commentary";

    /// <summary>Each finished match's recaps, one per mode and language, partitioned by <c>/matchId</c> (ADR-030).</summary>
    public const string Recaps = "recaps";

    /// <summary>Fan-built teams, one document per team, partitioned by <c>/id</c> (ADR-048). Built-in teams aren't stored.</summary>
    public const string Teams = "teams";

    /// <summary>
    /// Fan match requests, one document per match, partitioned by <c>/id</c> (ADR-050). No time to
    /// live: each is tiny, and it's the roster of record for every later read of its match.
    /// </summary>
    public const string MatchRequests = "match-requests";

    /// <summary>The change feed trigger's checkpoints, partitioned by <c>/id</c>.</summary>
    public const string Leases = "leases";
}
