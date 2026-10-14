using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

public sealed record FileSyncResult(
    int PagesProcessed,
    int ItemsApplied,
    int ItemsDeleted,
    bool FullRecrawl,
    int ItemsDeferred = 0);

/// <summary>
/// 通用文件树同步引擎（工单 05 / F1）。
///
/// 原先这套逻辑（游标分页推进、410 全量重扫、缺失项软删、401 刷新重试、429 退避）
/// 全部写在 <c>OneDriveSyncService</c> 里，与新 provider 无法复用。抽出来之后
/// **provider 只负责一件事**：把远端变更翻译成 <see cref="FileChange"/> 并给出不透明游标。
///
/// 游标语义（与 <see cref="IIncrementalChangeSource"/> 约定一致）：
/// <list type="bullet">
/// <item>入参 null = 首次或重置后的**全量重扫**；</item>
/// <item>返回 <c>HasMore</c> = true 时 <c>NextCursor</c> 只是本轮分页游标，**不落库**；</item>
/// <item>返回 <c>HasMore</c> = false 时 <c>NextCursor</c> 是要持久化的最终游标（null = 保留原值）；</item>
/// <item>游标失效必须抛 <see cref="ProviderCursorExpiredException"/> —— 引擎据此判定本轮为全量重扫并清理缺失项。</item>
/// </list>
/// 这条约定同时容纳 OneDrive（deltaLink/nextLink 双游标）与 Google（首扫 pageToken → 增量 pageToken）。
/// </summary>
public sealed class FileTreeSyncEngine
{
    private const int ThrottleRetryBudget = 3;
    private const int MaxPages = 100_000;
    private const int MaxAuthRefreshes = 5;

    private readonly PimDbContext _db;
    private readonly FileProviderSessionFactory _sessions;
    private readonly FileProviderTokenService _tokens;
    private readonly IFileProviderFactory _providers;
    private readonly FileProviderSyncGate _gate;
    private readonly ILogger<FileTreeSyncEngine>? _logger;
    private readonly TimeProvider _clock;

    public FileTreeSyncEngine(
        PimDbContext db,
        FileProviderSessionFactory sessions,
        FileProviderTokenService tokens,
        IFileProviderFactory providers,
        ILogger<FileTreeSyncEngine>? logger = null,
        TimeProvider? clock = null,
        FileProviderSyncGate? gate = null)
    {
        _db = db;
        _sessions = sessions;
        _tokens = tokens;
        _providers = providers;
        _gate = gate ?? new FileProviderSyncGate();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<FileSyncResult> SyncAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new DomainException(5320, "文件来源绑定不存在");

        // F1.3：这里原先硬判 `provider.Provider != "onedrive"`；现在只问注册表，
        // 新增网盘不需要改这一行。
        if (!_providers.IsSupported(provider.Provider))
        {
            throw new DomainException(5323, $"该文件来源（{provider.Provider}）不受支持，请重新选择");
        }

        if (provider.Status != "connected")
        {
            throw new DomainException(5321, "文件来源尚未完成绑定");
        }

        if (!_gate.TryEnter(providerId))
        {
            throw new DomainException(5335, "该绑定正在同步中，请稍后再试");
        }

        try
        {
            return await SyncCoreAsync(provider, ct);
        }
        finally
        {
            _gate.Exit(providerId);
        }
    }

    private async Task<FileSyncResult> SyncCoreAsync(FileProviderEntity provider, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var session = await _sessions.OpenAsync(provider, ct);
        var source = session.Changes
            ?? throw new DomainException(5323, $"该文件来源（{provider.Provider}）不支持同步");

        provider.SyncStatus = "syncing";
        provider.LastError = null;
        await _db.SaveChangesAsync(ct);

        var cursor = string.IsNullOrEmpty(provider.DeltaLink) ? null : provider.DeltaLink;
        var fullRecrawl = false;
        var authRefreshes = 0;
        var pagesProcessed = 0;
        long itemsApplied = 0;
        long itemsDeleted = 0;
        string? finalCursor = null;
        var itemsDeferred = 0;
        var deferredChanges = new List<FileChange>();
        var throttleRetries = 0;
        var parentPathCache = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            while (pagesProcessed < MaxPages)
            {
                FileChangePage page;
                try
                {
                    page = await source.GetChangesPageAsync(cursor, ct);
                    throttleRetries = 0;
                }
                catch (ProviderCursorExpiredException)
                {
                    // 游标失效：从头全量重扫，重扫结束后按 LastSeenAt 清理缺失项。
                    // 护栏：本来就在全量重扫（或首次就是 null 游标）还失效，说明远端行为异常，
                    // 直接失败而不是无限循环。
                    if (fullRecrawl || cursor is null)
                    {
                        throw;
                    }

                    cursor = null;
                    fullRecrawl = true;
                    provider.DeltaResetAt = now;
                    continue;
                }
                catch (ProviderUnauthorizedException) when (authRefreshes < MaxAuthRefreshes)
                {
                    // 首次全量可能爬取超过 token 有效期：失效缓存并重新刷新后重试当前页
                    authRefreshes++;
                    _tokens.InvalidateCached(provider.Id);
                    await _tokens.GetAccessTokenAsync(provider.Id, ct);
                    continue;
                }
                catch (ProviderTransientException exception)
                {
                    throttleRetries++;
                    if (throttleRetries >= ThrottleRetryBudget)
                    {
                        throw;
                    }

                    var delaySeconds = Math.Max(1, exception.RetryAfterSeconds ?? throttleRetries);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                    continue; // 同一游标重试
                }

                pagesProcessed++;
                var applied = await ApplyPageAsync(
                    provider, session, parentPathCache, page.Changes, now, ct);
                deferredChanges.AddRange(applied.Deferred);
                itemsDeleted += applied.Deleted;
                itemsApplied += applied.Applied;

                provider.SyncedItemCount = itemsApplied;
                provider.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                if (page.HasMore)
                {
                    cursor = page.NextCursor;
                    if (cursor is null)
                    {
                        break;
                    }

                    continue;
                }

                finalCursor = page.NextCursor;
                break;
            }

            // 全部页面落库后再补解析一轮：此时本地索引已含整棵树，
            // 「父在后面的页里」这种情况才解得开。
            var drained = await DrainDeferredAsync(
                provider, session, parentPathCache, deferredChanges, now, ct);
            itemsApplied += drained.Applied;
            itemsDeleted += drained.Deleted;
            itemsDeferred = drained.Deferred.Count;

            if (fullRecrawl)
            {
                itemsDeleted += await SoftDeleteStaleAsync(provider.Id, now, ct);
                if (finalCursor is null)
                {
                    // 旧游标已被证明失效：重扫未走到新游标时不能保留，否则下轮从头再扫
                    provider.DeltaLink = null;
                }
            }

            provider.DeltaLink = finalCursor ?? provider.DeltaLink;
            provider.LastSyncAt = now;
            provider.SyncedItemCount = itemsApplied;
            provider.SyncStatus = "idle";
            provider.LastError = null;
            provider.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            _logger?.LogInformation(
                "Files sync done for provider {ProviderId} ({ProviderKey}): pages={Pages} applied={Applied} deleted={Deleted} deferred={Deferred} fullRecrawl={FullRecrawl}",
                provider.Id, provider.Provider, pagesProcessed, itemsApplied, itemsDeleted, itemsDeferred, fullRecrawl);
            return new FileSyncResult(pagesProcessed, (int)itemsApplied, (int)itemsDeleted, fullRecrawl, itemsDeferred);
        }
        catch (DbUpdateConcurrencyException)
        {
            // provider 已被断开删除：原异常已无意义，向上报明确的领域错误
            throw new DomainException(5320, "文件来源绑定不存在");
        }
        catch (Exception exception)
        {
            try
            {
                provider.SyncStatus = "error";
                provider.LastError = exception.Message;
                provider.UpdatedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 写回失败同样源于绑定已删除
            }

            throw;
        }
    }

    private sealed record ApplyOutcome(long Applied, long Deleted, List<FileChange> Deferred);

    /// <summary>
    /// 未能当场解析出父路径的条目，延后重试的最大轮数。
    /// 全量首扫时 files.list / delta 的返回顺序不保证「父先于子」，
    /// 因此必须在全部页面落库后补一轮（否则一次首扫会漏掉大量深层条目）。
    /// </summary>
    private const int DeferredDrainRounds = 5;

    /// <summary>按页批量应用变更：一次查询页内全部既有行，内存合并后单次保存（首扫数万项的性能关键）。</summary>
    private async Task<ApplyOutcome> ApplyPageAsync(
        FileProviderEntity provider,
        IFileProviderSession session,
        Dictionary<string, string> parentPathCache,
        IReadOnlyList<FileChange> changes,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var upserts = new List<FileChange>();
        var removalIds = new List<string>();
        foreach (var change in changes)
        {
            if (change.IsRemoved)
            {
                removalIds.Add(change.ExternalId);
            }
            else
            {
                upserts.Add(change);
            }
        }

        var ids = upserts.Select(change => change.ExternalId).Concat(removalIds).ToList();
        var existing = ids.Count == 0
            ? new Dictionary<string, FileItemEntity>(StringComparer.Ordinal)
            : (await _db.Set<FileItemEntity>()
                    .Where(item => item.ProviderId == provider.Id && ids.Contains(item.ExternalFileId))
                    .ToListAsync(ct))
                .ToDictionary(item => item.ExternalFileId, StringComparer.Ordinal);

        long applied = 0;
        var deferred = new List<FileChange>();
        var upsertRows = new Dictionary<string, FileItemEntity>(StringComparer.Ordinal);
        foreach (var change in upserts)
        {
            var path = await ResolvePathAsync(provider, session, parentPathCache, change, ct);
            if (path is null)
            {
                // 父目录还没在本地索引里出现，且远端回读也没拿到路径。
                // **不猜路径**（猜出来的路径会污染整棵子树），交给 DrainDeferredAsync 补解析；
                // 补不上就明确计数并告警。
                deferred.Add(change);
                continue;
            }

            if (!existing.TryGetValue(change.ExternalId, out var item))
            {
                item = new FileItemEntity
                {
                    ProviderId = provider.Id,
                    ExternalFileId = change.ExternalId,
                    CreatedAt = now,
                };
                _db.Set<FileItemEntity>().Add(item);
                existing[change.ExternalId] = item;
                upsertRows[change.ExternalId] = item;
            }

            ApplyUpsert(item, change, path, now);
            applied++;
        }

        // 本页新增（或更新）的行要先落库：补解析下一批条目时要靠它们查父路径。
        // 既有实现每页只 SaveChanges 一次，这里保持一致（一页一次写）。
        _ = upsertRows;

        long deleted = 0;
        foreach (var id in removalIds)
        {
            if (existing.TryGetValue(id, out var item) && !item.IsDeleted)
            {
                item.IsDeleted = true;
                item.DeletedAt = now;
                item.SyncedAt = now;
                deleted++;
            }
        }

        return new ApplyOutcome(applied, deleted, deferred);
    }

    private static void ApplyUpsert(FileItemEntity item, FileChange change, string path, DateTimeOffset now)
    {
        item.ParentExternalFileId = change.ParentExternalId;
        item.Path = path;
        item.Name = change.Name;
        item.ItemType = change.IsFolder ? "folder" : "file";
        item.MimeType = change.MimeType;
        item.Size = change.Size;
        item.Etag = change.ETag;
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.LastSeenAt = now;
        item.ModifiedAt = change.ModifiedAt == DateTimeOffset.UnixEpoch ? item.CreatedAt : change.ModifiedAt;
        item.SyncedAt = now;
    }

    /// <summary>
    /// 补解析未能当场定位父路径的条目。每轮把能解开的写回并落库（这样下一轮又能解开更多），
    /// 直到不再有进展或用完轮数上限。剩下的如实计数，不静默丢弃。
    /// </summary>
    private async Task<ApplyOutcome> DrainDeferredAsync(
        FileProviderEntity provider,
        IFileProviderSession session,
        Dictionary<string, string> parentPathCache,
        IReadOnlyList<FileChange> deferred,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (deferred.Count == 0)
        {
            return new ApplyOutcome(0, 0, []);
        }

        var pending = deferred.ToList();
        long applied = 0;
        for (var round = 0; round < DeferredDrainRounds && pending.Count > 0; round++)
        {
            var progressed = new List<FileChange>();
            var stillPending = new List<FileChange>();
            var rows = new Dictionary<string, FileItemEntity>(StringComparer.Ordinal);

            var ids = pending.Select(change => change.ExternalId).ToList();
            var existing = (await _db.Set<FileItemEntity>()
                    .Where(item => item.ProviderId == provider.Id && ids.Contains(item.ExternalFileId))
                    .ToListAsync(ct))
                .ToDictionary(item => item.ExternalFileId, StringComparer.Ordinal);

            foreach (var change in pending)
            {
                var path = await ResolvePathAsync(provider, session, parentPathCache, change, ct);
                if (path is null)
                {
                    stillPending.Add(change);
                    continue;
                }

                if (!existing.TryGetValue(change.ExternalId, out var item))
                {
                    item = new FileItemEntity
                    {
                        ProviderId = provider.Id,
                        ExternalFileId = change.ExternalId,
                        CreatedAt = now,
                    };
                    _db.Set<FileItemEntity>().Add(item);
                }

                ApplyUpsert(item, change, path, now);
                rows[change.ExternalId] = item;
                progressed.Add(change);
                applied++;
            }

            if (progressed.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            pending = stillPending;
            if (progressed.Count == 0)
            {
                // 这一轮毫无进展：再循环也是白跑
                break;
            }
        }

        if (pending.Count > 0)
        {
            _logger?.LogWarning(
                "Files sync could not resolve the parent path for {Deferred} change(s) of provider {ProviderId}; they stay unindexed until a later sync",
                pending.Count, provider.Id);
        }

        return new ApplyOutcome(applied, 0, pending);
    }

    /// <summary>
    /// 解析条目的 PIM 绝对路径。三级来源，顺序即成本顺序：
    /// 1) provider 直接给的 <see cref="FileChange.ParentPath"/>（OneDrive 有，最便宜）；
    /// 2) 本地索引里父行的 Path（本地查询）；
    /// 3) 远端回读父条目（最贵，按页缓存）。
    /// 三级都拿不到时返回 null，由调用方计入 deferred。
    /// </summary>
    private async Task<string?> ResolvePathAsync(
        FileProviderEntity provider,
        IFileProviderSession session,
        Dictionary<string, string> parentPathCache,
        FileChange change,
        CancellationToken ct)
    {
        // 根目录自身的判据是**父 id 与父路径都缺失**。
        // 只看 ParentExternalId 会把「有父路径、但 provider 没给父 id」的根下条目
        // 误判成根目录自身（Graph 的 delta 在个别情况下就不带 parentReference.id，
        // 而 parentReference.path 仍然给得出 "/drive/root:"）。
        if (change.ParentExternalId is null && change.ParentPath is null)
        {
            return "/";
        }

        var parentPath = change.ParentPath;
        if (parentPath is null && change.ParentExternalId is null)
        {
            // 两者都没有：无法定位，交给补解析/下一轮
            return null;
        }

        if (parentPath is null)
        {
            parentPath = await ResolveParentPathAsync(
                provider, session, parentPathCache, change.ParentExternalId!, ct);
        }

        if (parentPath is null)
        {
            return null;
        }

        return parentPath == "/" ? "/" + change.Name : parentPath.TrimEnd('/') + "/" + change.Name;
    }

    private async Task<string?> ResolveParentPathAsync(
        FileProviderEntity provider,
        IFileProviderSession session,
        Dictionary<string, string> cache,
        string parentExternalId,
        CancellationToken ct)
    {
        if (string.Equals(parentExternalId, session.Account.RootExternalId, StringComparison.Ordinal))
        {
            return "/";
        }

        if (cache.TryGetValue(parentExternalId, out var cached))
        {
            return cached;
        }

        var localPath = await _db.Set<FileItemEntity>()
            .AsNoTracking()
            .Where(row => row.ProviderId == provider.Id && row.ExternalFileId == parentExternalId)
            .Select(row => row.Path)
            .FirstOrDefaultAsync(ct);
        if (localPath is not null)
        {
            cache[parentExternalId] = localPath;
            return localPath;
        }

        var remote = await session.Content.GetItemAsync(parentExternalId, ct);
        // 必须核对返回的 id 与请求的一致：adapter 出错时可能返回另一个条目，
        // 用它的路径会静默污染整棵子树（宁可不解析、下一轮再来）。
        if (remote is not null
            && string.Equals(remote.Id, parentExternalId, StringComparison.Ordinal)
            && remote.Path is { Length: > 0 } remotePath)
        {
            cache[parentExternalId] = remotePath;
            return remotePath;
        }

        return null;
    }

    private async Task<long> SoftDeleteStaleAsync(Guid providerId, DateTimeOffset syncStart, CancellationToken ct)
    {
        var stale = await _db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == providerId
                && !item.IsDeleted
                && (item.LastSeenAt == null || item.LastSeenAt < syncStart))
            .ToListAsync(ct);
        foreach (var item in stale)
        {
            item.IsDeleted = true;
            item.DeletedAt = syncStart;
            item.SyncedAt = syncStart;
        }

        if (stale.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return stale.Count;
    }
}
