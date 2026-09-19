using System.Collections.Concurrent;

namespace Pim.Module.Files.Services;

/// <summary>
/// 每 provider 的同步互斥（单例）：同一绑定同一时刻只允许一个同步执行。
/// 并发同步会让 410 全量重扫的 LastSeenAt 清理误删活条目、并回退 deltaLink 游标（复审 C1）。
/// </summary>
public sealed class OneDriveSyncGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public bool TryEnter(Guid providerId)
        => _locks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1)).Wait(0);

    public void Exit(Guid providerId)
    {
        if (_locks.TryGetValue(providerId, out var gate))
        {
            gate.Release();
        }
    }
}

/// <summary>
/// 跨请求共享的 access token 内存缓存 + 每 provider 刷新锁（单例）。
/// TokenService 是 Scoped——缓存若挂在实例上会随作用域失效，并发刷新还有
/// refresh token 轮换竞争（复审 I2），因此缓存与锁都放在单例上。
/// </summary>
public sealed class OneDriveTokenCache
{
    private readonly ConcurrentDictionary<Guid, (string AccessToken, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    public bool TryGetValid(Guid providerId, DateTimeOffset now, out string accessToken)
    {
        if (_cache.TryGetValue(providerId, out var cached) && cached.ExpiresAt > now)
        {
            accessToken = cached.AccessToken;
            return true;
        }

        accessToken = string.Empty;
        return false;
    }

    public void Set(Guid providerId, string accessToken, DateTimeOffset expiresAt)
        => _cache[providerId] = (accessToken, expiresAt);

    public void Invalidate(Guid providerId) => _cache.TryRemove(providerId, out _);

    /// <summary>串行化同一 provider 的刷新；返回释放句柄。</summary>
    public async Task<IDisposable> EnterRefreshAsync(Guid providerId, CancellationToken ct = default)
    {
        var gate = _refreshLocks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            _gate?.Release();
            _gate = null;
        }
    }
}
