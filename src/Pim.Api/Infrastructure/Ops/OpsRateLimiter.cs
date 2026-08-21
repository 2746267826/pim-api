using System.Collections.Concurrent;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsRateLimiter
{
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private const int MaxConcurrent = 2;

    private sealed class Slot
    {
        public int Count;
        public readonly object Lock = new();
    }

    public bool TryAcquire(string ip, out int retryAfter)
    {
        retryAfter = 5;
        var key = ip ?? "unknown";
        var slot = _slots.GetOrAdd(key, _ => new Slot());
        lock (slot.Lock)
        {
            if (slot.Count >= MaxConcurrent)
                return false;
            slot.Count++;
            return true;
        }
    }

    // Kept for compatibility with older call sites (unused after改3); no-op.
    public bool TryAcquire(string ip, long bytes, out int retryAfter) => TryAcquire(ip, out retryAfter);

    public void Release(string ip)
    {
        var key = ip ?? "unknown";
        if (!_slots.TryGetValue(key, out var slot)) return;
        lock (slot.Lock)
        {
            if (slot.Count > 0) slot.Count--;
        }
    }

    // No-op: per-minute byte accounting removed (单次 5MB 截断由 OpsLogsService/OpsDbService 负责)
    public void AddBytes(string ip, long bytes) { }

    // For testing: clear store
    internal void Clear() => _slots.Clear();
}
