using StackExchange.Redis;
using Touchline.MatchCentre.Caching;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>A Redis connection that is whatever the test says: a database, or none (disconnected).</summary>
/// <param name="database">The database, or null for a Redis that isn't connected.</param>
internal sealed class FixedRedisConnection(IDatabase? database) : IRedisConnection
{
    public IDatabase? Database { get; set; } = database;
}
