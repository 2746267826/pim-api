namespace Pim.Infrastructure.Notes;

/// <summary>
/// Small process-local LRU used for verification hints. Entries are disposable accelerators:
/// eviction only forces a re-check, never changes the accepted material set.
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _order = new();
    private readonly object _sync = new();

    internal BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity, comparer);
    }

    internal int Capacity => _capacity;

    internal int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    internal bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    internal void Set(TKey key, TValue value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }
            var node = _order.AddFirst(new Entry(key, value));
            _entries[key] = node;
            while (_entries.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(last.Value.Key);
            }
        }
    }

    internal int RemoveWhere(Func<TKey, bool> predicate)
    {
        lock (_sync)
        {
            var keys = _entries.Keys.Where(predicate).ToArray();
            foreach (var key in keys)
            {
                var node = _entries[key];
                _order.Remove(node);
                _entries.Remove(key);
            }
            return keys.Length;
        }
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}
