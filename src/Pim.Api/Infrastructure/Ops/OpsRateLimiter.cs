using System.Collections.Concurrent;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsRateLimiter
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, WindowState> _store = new();
    private const int MaxRequests = 30;
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class WindowState
    {
        public int Count;
        public long Bytes;
        public DateTimeOffset WindowStart;
        public readonly object Lock = new();
    }

    public OpsRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public bool TryAcquire(string ip, long bytes, out int retryAfter)
    {
        retryAfter = 60;
        var now = _time.GetUtcNow();
        var key = ip ?? "unknown";
        var state = _store.GetOrAdd(key, _ => new WindowState { WindowStart = now });
        lock (state.Lock)
        {
            if (now - state.WindowStart >= Window)
            {
                state.Count = 0;
                state.Bytes = 0;
                state.WindowStart = now;
            }

            var elapsed = now - state.WindowStart;
            var remaining = Window - elapsed;
            retryAfter = (int)Math.Ceiling(remaining.TotalSeconds);
            if (retryAfter <= 0) retryAfter = 60;

            if (state.Count >= MaxRequests)
                return false;
            if (state.Bytes >= MaxBytes)
                return false;
            if (state.Bytes + bytes > MaxBytes)
                return false;

            state.Count++;
            state.Bytes += bytes;
            return true;
        }
    }

    public void AddBytes(string ip, long bytes)
    {
        if (bytes <= 0) return;
        var now = _time.GetUtcNow();
        var key = ip ?? "unknown";
        var state = _store.GetOrAdd(key, _ => new WindowState { WindowStart = now });
        lock (state.Lock)
        {
            if (now - state.WindowStart >= Window)
            {
                state.Count = 1;
                state.Bytes = bytes;
                state.WindowStart = now;
                return;
            }
            state.Bytes += bytes;
        }
    }

    // For testing: clear store
    internal void Clear() => _store.Clear();
}
