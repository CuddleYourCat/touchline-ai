using Touchline.MatchCentre.Caching;

namespace Touchline.Tests.Unit.MatchCentre.Caching;

/// <summary>
/// The Lua scripts' rules in C#, over a dictionary standing in for Redis, with one lock playing
/// the part of Redis running a script alone. It mirrors <see cref="LuaFanMatchLimitScripts"/>
/// line by line; the scripts themselves are only exercised against a real Redis.
/// </summary>
internal sealed class InMemoryLimitScripts : IFanMatchLimitScripts
{
    private readonly Lock _gate = new();

    /// <summary>What Redis would hold: each key's value and when it expires.</summary>
    public Dictionary<string, (string Value, DateTimeOffset? ExpiresAt)> Keys { get; } = [];

    /// <summary>Makes every call fail as an unreachable Redis would.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>The time "now", for claim lifetimes.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public Task<bool> TakeAsync(string key, int limit, DateTimeOffset expiresAt) => Run(() =>
    {
        var count = Keys.TryGetValue(key, out var current) ? int.Parse(current.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (count >= limit)
        {
            return false;
        }

        Keys[key] = ((count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), expiresAt);
        return true;
    });

    public Task ReturnAsync(string key) => Run(() =>
    {
        if (Keys.TryGetValue(key, out var current) && int.Parse(current.Value, System.Globalization.CultureInfo.InvariantCulture) is var count and > 0)
        {
            Keys[key] = ((count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), current.ExpiresAt);
        }

        return true;
    });

    public Task<string?> ClaimAsync(string key, string claim, TimeSpan lifetime) => Run(() =>
    {
        if (Keys.TryGetValue(key, out var current))
        {
            return current.Value;
        }

        Keys[key] = (claim, Now + lifetime);
        return (string?)null;
    });

    public Task<bool> ReclaimAsync(string key, string expectedRequestId, string claim, TimeSpan lifetime) => Run(() =>
    {
        if (!Keys.TryGetValue(key, out var current) || ClaimValue.RequestIdOf(current.Value) == expectedRequestId)
        {
            Keys[key] = (claim, Now + lifetime);
            return true;
        }

        return false;
    });

    public Task ReleaseAsync(string key, string requestId) => Run(() =>
    {
        if (Keys.TryGetValue(key, out var current) && ClaimValue.RequestIdOf(current.Value) == requestId)
        {
            Keys.Remove(key);
        }

        return true;
    });

    private Task<T> Run<T>(Func<T> script)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException<T>(failure);
        }

        lock (_gate)
        {
            return Task.FromResult(script());
        }
    }
}
