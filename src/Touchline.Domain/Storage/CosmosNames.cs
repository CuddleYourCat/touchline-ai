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

    /// <summary>The change feed trigger's checkpoints, partitioned by <c>/id</c>.</summary>
    public const string Leases = "leases";
}
