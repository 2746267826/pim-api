using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

/// <summary>
/// 每**账号**的同步互斥（单例）：同一绑定同一时刻只允许一个同步执行。
/// 并发同步会让全量重扫的 LastSeenAt 清理误删活条目、并回退游标。
/// 键是账号行 id，因此多账号可以并行同步而互不阻塞（F2.4）。
/// </summary>
public sealed class FileProviderSyncGate
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
/// 跨请求共享的 access token 内存缓存 + 每账号刷新锁（单例）。
/// 令牌服务是 Scoped——缓存若挂在实例上会随作用域失效，并发刷新还有
/// refresh token 轮换竞争，因此缓存与锁都放在单例上。
/// 键是账号行 id：多账号各自的令牌与锁完全独立（F2.4）。
/// </summary>
public sealed class FileProviderTokenCache
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

    /// <summary>串行化同一账号的刷新；返回释放句柄。</summary>
    public async Task<IDisposable> EnterRefreshAsync(Guid providerId, CancellationToken ct = default)
    {
        var gate = _refreshLocks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private readonly SemaphoreSlim _gate = gate;

        public void Dispose() => _gate.Release();
    }
}

/// <summary>
/// 按**账号行**打开通用 provider 会话的唯一入口。
///
/// 存在的意义是消灭「按 (UserId, provider == "onedrive") 取唯一来源」这类写法：
/// 会话构造必须带具体账号行，消费方拿不到「隐式的那个 OneDrive」。
/// </summary>
public sealed class FileProviderSessionFactory(
    IFileProviderFactory providers,
    FileProviderTokenService tokens)
{
    private readonly IFileProviderFactory _providers = providers;
    private readonly FileProviderTokenService _tokens = tokens;

    public Task<IFileProviderSession> OpenAsync(FileProviderEntity provider, CancellationToken ct = default)
        => OpenAsync(provider, requireConnected: true, ct);

    /// <summary>
    /// 打开会话。<paramref name="requireConnected"/> 为 false 时允许 status 非 connected
    /// （仅用于绑定流程里的探测，正常读写一律要求 true）。
    /// </summary>
    public async Task<IFileProviderSession> OpenAsync(
        FileProviderEntity provider,
        bool requireConnected,
        CancellationToken ct = default)
    {
        if (requireConnected && provider.Status != "connected")
        {
            throw new Pim.Core.Exceptions.DomainException(5321, "文件来源尚未完成绑定");
        }

        var implementation = _providers.Get(provider.Provider);
        var context = new FileProviderContext(
            provider.Id,
            provider.UserId,
            provider.Provider,
            provider.ClientId,
            provider.DriveId,
            provider.AccountId,
            tokenCt => _tokens.GetAccessTokenAsync(provider.Id, tokenCt));
        return await implementation.OpenSessionAsync(context, ct);
    }
}

/// <summary>
/// 按账号定位 provider 行的共享查询（F2.1/F2.2）：所有「取一个来源」的地方都从这里走，
/// 避免再次散落 <c>SingleOrDefault(UserId, Provider == "onedrive")</c>。
/// </summary>
public static class FileProviderLookup
{
    /// <summary>取该用户在某 provider 下的**默认**账号（附件/闪念图片的落点）。</summary>
    public static Task<FileProviderEntity?> FindDefaultAsync(
        PimDbContext db,
        Guid userId,
        string? providerKey = null,
        CancellationToken ct = default)
    {
        var query = db.Set<FileProviderEntity>()
            .Where(row => row.UserId == userId && row.Status == "connected");
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            query = query.Where(row => row.Provider == providerKey);
        }

        return query
            .OrderByDescending(row => row.IsDefault)
            .ThenBy(row => row.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 解析**列表**的作用域账号（工单 05 / F5 的口径）：
    /// <list type="bullet">
    /// <item>显式给了 <paramref name="providerId"/> → 用它（必须属于当前用户且已连接）；</item>
    /// <item>没给 → 该用户只有**一个**已连接账号时沿用它（保持 MCP <c>get_files</c> 等旧调用的兼容）；</item>
    /// <item>没给且有多个 → 明确报错要求指定，**不做跨账号合并**：<c>path</c> 是账号相对路径，
    /// 把两个盘的同名目录合成一棵树会得到语义错乱的树，也无法说清「这个 /Docs 是哪个盘的」。</item>
    /// </list>
    /// </summary>
    public static async Task<Guid> ResolveListScopeAsync(
        PimDbContext db,
        Guid userId,
        Guid? providerId,
        CancellationToken ct = default)
    {
        if (providerId is { } explicitId)
        {
            var row = await db.Set<FileProviderEntity>()
                .AsNoTracking()
                .Where(item => item.Id == explicitId && item.UserId == userId)
                .Select(item => new { item.Status })
                .FirstOrDefaultAsync(ct);
            if (row is null)
            {
                throw new Pim.Core.Exceptions.DomainException(5320, "文件来源绑定不存在");
            }

            if (row.Status != "connected")
            {
                // 账号在但用不了：说清是「授权失效」，别让用户以为是文件没了
                throw row.Status == "expired"
                    ? new Pim.Core.Exceptions.DomainException(5322, "网盘授权已失效，请到设置页重新连接该账号")
                    : new Pim.Core.Exceptions.DomainException(5321, "文件来源尚未完成绑定");
            }

            return explicitId;
        }

        var connected = await db.Set<FileProviderEntity>()
            .Where(row => row.UserId == userId && row.Status == "connected")
            .OrderByDescending(row => row.IsDefault)
            .ThenBy(row => row.CreatedAt)
            .Select(row => row.Id)
            .ToListAsync(ct);

        return connected.Count switch
        {
            0 => throw new Pim.Core.Exceptions.DomainException(5343, "尚未绑定网盘账号"),
            1 => connected[0],
            _ => throw new Pim.Core.Exceptions.DomainException(
                5342, "存在多个网盘账号，请指定 providerId 后重试"),
        };
    }
}
