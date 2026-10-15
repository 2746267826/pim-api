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
            throw provider.Status == "expired"
                ? new DomainException(5322, "网盘授权已失效，请到设置页重新连接该账号")
                : new DomainException(5321, "文件来源尚未完成绑定");
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
        // 起点没有游标就是一次全量：包括首扫，也包括**重绑后清空游标**的情况。
        // 只在「游标失效异常」时才置 fullRecrawl 会漏掉后者，导致授权离线期间远端已删的文件
        // 变成永久幽灵索引（复审 Important）。
        var fullRecrawl = cursor is null;
        // 本轮**按 ExternalId 的最终事件**：同一 id 在后面的页里再次出现时，
        // 以最后一条为准（否则「先 deferred 的旧状态」会在补解析阶段覆盖后到的新状态）。
        var lastEventById = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        var deferredById = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        // 本轮已落地的路径：同页/后续页解析父路径时优先用它，而不是过期的缓存或库里的旧值。
        var appliedPathsThisRun = new Dictionary<string, string>(StringComparer.Ordinal);
        var authRefreshes = 0;
        var pagesProcessed = 0;
        long itemsApplied = 0;
        long itemsDeleted = 0;
        string? finalCursor = null;
        var itemsDeferred = 0;
        var deferredChanges = new List<FileChange>();
        var throttleRetries = 0;
        string? droppedMessage = null;
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
                    provider, session, parentPathCache, page.Changes, appliedPathsThisRun, now, ct);
                foreach (var change in page.Changes)
                {
                    lastEventById[change.ExternalId] = change;
                    if (change.IsRemoved)
                    {
                        // 后到的删除取消先前对同一 id 的延迟 upsert
                        deferredById.Remove(change.ExternalId);
                    }
                }

                foreach (var change in applied.Deferred)
                {
                    deferredById[change.ExternalId] = change;
                }
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
            // 补解析用的是「按 id 去重后的**最后事件**」，而不是当初被延迟的那一条：
            // 同一 id 可能在后面某一页带着新名字/新父目录再次出现（甚至已成功应用），
            // 拿旧事件去补解析会把已经落好的新状态覆盖回旧状态（复审 Important）。
            // 用最后事件即使重复应用也是幂等的（同样的值写同样的结果）。
            var drainInput = deferredById.Keys
                .Select(id => lastEventById.TryGetValue(id, out var last) ? last : null)
                .Where(change => change is not null && !change.IsRemoved)
                .Select(change => change!)
                .ToList();
            var drained = await DrainDeferredAsync(
                provider, session, parentPathCache, appliedPathsThisRun, drainInput, now, ct);
            itemsApplied += drained.Applied;
            itemsDeleted += drained.Deleted;
            itemsDeferred = drained.Deferred.Count;

            if (itemsDeferred > 0)
            {
                provider.DeferredBlockCount++;
                if (provider.DeferredBlockCount <= MaxDeferredBlockedSyncs)
                {
                    // 还有条目没落地：**不能推进游标**，否则这些变化再也不会被投递（永久漏项）。
                    // 保留旧游标，下一轮从原处重放；同时不做缺失项清理 —— 这些条目对应的既有行
                    // 本轮没有更新 LastSeenAt，清理会把它们误删（复审 Important）。
                    provider.SyncStatus = "error";
                    provider.LastError =
                        $"有 {itemsDeferred} 项因父目录尚未索引而未落地（第 {provider.DeferredBlockCount} 次），将在下次同步重试";
                    provider.LastSyncAt = now;
                    provider.SyncedItemCount = itemsApplied;
                    provider.UpdatedAt = now;
                    await _db.SaveChangesAsync(ct);
                    _logger?.LogWarning(
                        "Files sync for provider {ProviderId} kept its cursor because {Deferred} item(s) could not be resolved (attempt {Attempt})",
                        provider.Id, itemsDeferred, provider.DeferredBlockCount);
                    return new FileSyncResult(pagesProcessed, (int)itemsApplied, (int)itemsDeleted, fullRecrawl, itemsDeferred);
                }

                // 连续多轮都解析不出来（例如父目录在别人盘里、本就不可见）：
                // 明确放弃并**正常推进**，否则这个条目会把同步永久钉死在这里。
                // 放弃不是静默的：计数进日志，原因写进 LastError 供同步状态接口展示。
                _logger?.LogWarning(
                    "Files sync for provider {ProviderId} gave up on {Deferred} item(s) after {Attempt} blocked attempts",
                    provider.Id, itemsDeferred, provider.DeferredBlockCount);
                droppedMessage = $"有 {itemsDeferred} 项因父目录不可见已跳过（连续 {provider.DeferredBlockCount} 次无法定位）";
            }

            provider.DeferredBlockCount = 0;

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
            // 放弃过条目时保留原因，让同步状态接口能如实显示，而不是静默吞掉
            provider.LastError = droppedMessage;
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
    private const int DeferredDrainRounds = 30;

    /// <summary>
    /// 「解析不出父路径」连续阻塞多少次同步后放弃这些条目。
    /// 有界：否则一个永远不可见的父目录会让同步永久停在原地。
    /// </summary>
    private const int MaxDeferredBlockedSyncs = 3;

    /// <summary>按页批量应用变更：一次查询页内全部既有行，内存合并后单次保存（首扫数万项的性能关键）。</summary>
    private async Task<ApplyOutcome> ApplyPageAsync(
        FileProviderEntity provider,
        IFileProviderSession session,
        Dictionary<string, string> parentPathCache,
        IReadOnlyList<FileChange> changes,
        Dictionary<string, string> appliedPathsThisRun,
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
        foreach (var change in upserts)
        {
            var path = await ResolvePathAsync(provider, session, parentPathCache, appliedPathsThisRun, change, ct);
            if (path is null)
            {
                // 父目录还没在本地索引里出现，且远端回读也没拿到路径。
                // **不猜路径**（猜出来的路径会污染整棵子树），交给 DrainDeferredAsync 补解析；
                // 补不上就明确计数并告警。
                deferred.Add(change);
                continue;
            }

            var isNew = !existing.TryGetValue(change.ExternalId, out var item);
            if (isNew)
            {
                item = new FileItemEntity
                {
                    ProviderId = provider.Id,
                    ExternalFileId = change.ExternalId,
                    CreatedAt = now,
                };
                _db.Set<FileItemEntity>().Add(item);
                existing[change.ExternalId] = item;
            }

            // 只有**已存在的行**才有「旧路径」可言：新行实体的 Path 默认是 "/"，
            // 把它当旧路径会触发一次假的整树改写（曾把 /a/b 改写成 /x/a/b）。
            var previousPath = isNew ? null : item!.Path;
            ApplyUpsert(item!, change, path, now);
            appliedPathsThisRun[change.ExternalId] = path;
            applied++;

            // 目录改名/移动后，远端**不会**为每个后代各发一条 change（Drive 尤其如此），
            // 只改目录自身会让子孙留着旧 Path：列表/搜索会显示错误位置，更要命的是
            // 敏感路径判定会按旧路径放行（复审 Important）。
            if (change.IsFolder && previousPath is { Length: > 0 } && previousPath != path)
            {
                await RewriteDescendantPathsAsync(provider.Id, previousPath, path, now, ct);
                InvalidateCacheUnder(parentPathCache, previousPath, path);
                InvalidateCacheUnder(appliedPathsThisRun, previousPath, path);
            }

            if (change.IsFolder)
            {
                await ReconcileChildPathsAsync(
                    provider.Id, change.ExternalId, path, now, parentPathCache, appliedPathsThisRun, ct);
            }
        }

        long deleted = 0;
        foreach (var id in removalIds)
        {
            appliedPathsThisRun.Remove(id);
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

    /// <summary>
    /// 把 <paramref name="oldPath"/> 前缀下的子孙 Path 改写为 <paramref name="newPath"/> 前缀。
    /// 与写服务的同名逻辑保持一致：目录一旦换位置，整棵子树都要跟着走。
    /// </summary>
    private async Task RewriteDescendantPathsAsync(
        Guid providerId,
        string oldPath,
        string newPath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var prefix = oldPath.TrimEnd('/') + "/";
        var descendants = await _db.Set<FileItemEntity>()
            .Where(row => row.ProviderId == providerId
                && row.Path.StartsWith(prefix)
                && !row.IsDeleted)
            .ToListAsync(ct);
        if (descendants.Count == 0)
        {
            return;
        }

        var trimmedOld = oldPath.TrimEnd('/');
        foreach (var descendant in descendants)
        {
            descendant.Path = newPath.TrimEnd('/') + descendant.Path[trimmedOld.Length..];
            descendant.SyncedAt = now;
        }
    }


    /// <summary>
    /// 按**父 id** 校正该目录下已经在本地出现的直接子项路径，并级联整棵子树。
    ///
    /// 为什么改名以外也要做：delta 不保证顺序，子项可能先于父目录到达，此时子项的路径是按
    /// 远端给的「父路径提示」算出来的；父目录随后带着**不同的**最终路径出现时，
    /// 先前落地的子项就成了陈旧路径（列表/搜索显示错位置，按路径判断的敏感保护也会放行）。
    /// 逐父 id 校正比「按旧路径前缀改写」更可靠：新目录在本地根本没有旧路径可言。
    /// </summary>
    private async Task ReconcileChildPathsAsync(
        Guid providerId,
        string folderExternalId,
        string folderPath,
        DateTimeOffset now,
        Dictionary<string, string> parentPathCache,
        Dictionary<string, string> appliedPathsThisRun,
        CancellationToken ct)
    {
        var children = await _db.Set<FileItemEntity>()
            .Where(row => row.ProviderId == providerId
                && row.ParentExternalFileId == folderExternalId
                && !row.IsDeleted)
            .ToListAsync(ct);
        if (children.Count == 0)
        {
            return;
        }

        foreach (var child in children)
        {
            var expected = folderPath == "/"
                ? "/" + child.Name
                : folderPath.TrimEnd('/') + "/" + child.Name;
            if (string.Equals(child.Path, expected, StringComparison.Ordinal))
            {
                continue;
            }

            var stalePath = child.Path;
            child.Path = expected;
            child.SyncedAt = now;
            appliedPathsThisRun[child.ExternalFileId] = expected;
            await RewriteDescendantPathsAsync(providerId, stalePath, expected, now, ct);
            InvalidateCacheUnder(parentPathCache, stalePath, expected);
            InvalidateCacheUnder(appliedPathsThisRun, stalePath, expected);
        }
    }

    /// <summary>
    /// 目录换位置后，**所有**按父 id 缓存的路径都要跟着改：cache（跨页）与本轮已落地路径
    /// （解析时优先级最高）。漏掉后者会让「子目录先出现 → 祖先改名 → 孙项出现」这条链
    /// 解析到旧位置，进而绕过按路径判断的敏感保护（复审 Important）。
    /// </summary>
    private static void InvalidateCacheUnder(
        Dictionary<string, string> cache,
        string oldPath,
        string newPath)
    {
        var prefix = oldPath.TrimEnd('/') + "/";
        var trimmedOld = oldPath.TrimEnd('/');
        foreach (var key in cache.Keys.ToList())
        {
            var cached = cache[key];
            if (cached == oldPath || cached.StartsWith(prefix, StringComparison.Ordinal))
            {
                cache[key] = newPath.TrimEnd('/') + cached[trimmedOld.Length..];
            }
        }
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
        Dictionary<string, string> appliedPathsThisRun,
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
                var path = await ResolvePathAsync(provider, session, parentPathCache, appliedPathsThisRun, change, ct);
                if (path is null)
                {
                    stillPending.Add(change);
                    continue;
                }

                var isNewItem = !existing.TryGetValue(change.ExternalId, out var item);
                if (isNewItem)
                {
                    item = new FileItemEntity
                    {
                        ProviderId = provider.Id,
                        ExternalFileId = change.ExternalId,
                        CreatedAt = now,
                    };
                    _db.Set<FileItemEntity>().Add(item);
                    // 立刻进字典：同一 id 在待处理列表里出现多次时，
                    // 不进字典会重复 Add，撞 (provider_id, external_file_id) 唯一键。
                    existing[change.ExternalId] = item;
                }

                var previousPath = isNewItem ? null : item!.Path;
                ApplyUpsert(item!, change, path, now);
                appliedPathsThisRun[change.ExternalId] = path;
                rows[change.ExternalId] = item;
                progressed.Add(change);
                applied++;

                if (change.IsFolder && previousPath is { Length: > 0 } && previousPath != path)
                {
                    await RewriteDescendantPathsAsync(provider.Id, previousPath, path, now, ct);
                    InvalidateCacheUnder(parentPathCache, previousPath, path);
                    InvalidateCacheUnder(appliedPathsThisRun, previousPath, path);
                }

                if (change.IsFolder)
                {
                    await ReconcileChildPathsAsync(
                        provider.Id, change.ExternalId, path, now, parentPathCache, appliedPathsThisRun, ct);
                }
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
        Dictionary<string, string> appliedPathsThisRun,
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

        // 本轮已经落地过的父目录路径优先：同页里「父改名在前、子在后」时，
        // 缓存与数据库都还是旧值，只有本轮的结果是新的。
        var parentPath = change.ParentExternalId is { } pe && appliedPathsThisRun.TryGetValue(pe, out var currentParentPath)
            ? currentParentPath
            : change.ParentPath;
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
