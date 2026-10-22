namespace Touchline.MatchCentre.Storage;

/// <summary>
/// A small least-recently-used cache: it holds at most <c>capacity</c> entries and drops the one
/// read or added longest ago. Thread-safe, with one lock, because it's tiny and reads are cheap.
/// </summary>
/// <typeparam name="T">The cached value.</typeparam>
/// <param name="capacity">The most entries it holds.</param>
internal sealed class RecentCache<T>(int capacity)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _entries = [];
    private readonly LinkedList<(string Key, T Value)> _recency = new();
    private readonly Lock _gate = new();

    /// <summary>Reads an entry, marking it the most recently used.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">The value, when found.</param>
    /// <returns>True when the entry was cached.</returns>
    public bool TryGet(string key, out T value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Adds or replaces an entry, dropping the least recently used when full.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">The value.</param>
    public void Add(string key, T value)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var existing))
            {
                _recency.Remove(existing);
            }

            _entries[key] = _recency.AddFirst((key, value));
            if (_entries.Count > capacity && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }
}
