using System.ComponentModel.DataAnnotations;

namespace Touchline.MatchCentre.Caching;

/// <summary>
/// How the Match Centre reaches Redis (ADR-061). Bound from <c>MatchCentre:Redis</c>.
/// </summary>
/// <remarks>
/// Redis is used when either the Aspire connection string <c>ConnectionStrings:redis</c> is set
/// (the local container) or <see cref="Endpoint"/> is (Azure Managed Redis, signed in with
/// Entra ID). With neither, every cache stays in memory and the fan-match limits stay in Cosmos DB.
/// </remarks>
public sealed class RedisOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:Redis";

    /// <summary>The name of the Aspire connection string for the local Redis container.</summary>
    public const string ConnectionName = "redis";

    /// <summary>
    /// The Azure Managed Redis endpoint, <c>host:port</c>, such as
    /// <c>redis-touchline-x.swedencentral.redis.azure.net:10000</c>. Signed in with the app's
    /// managed identity; access keys are off. Empty means no Redis in Azure.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>How long a connection attempt may take.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long one command may take before it counts as a miss.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:00:10")]
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait before trying again after a connection attempt failed outright.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan RetryConnectAfter { get; set; } = TimeSpan.FromSeconds(30);
}
