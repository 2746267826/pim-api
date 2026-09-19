using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed record OneDriveSyncResult(int PagesProcessed, int ItemsApplied, int ItemsDeleted, bool FullRecrawl);

/// <summary>
/// OneDrive delta 增量同步：按 deltaLink 游标分页爬取，幂等 upsert 文件树元数据；
/// @removed 与 410 全量重扫后的缺失项做软删（PIM 回收站语义）。
/// 429 按 Retry-After 退避重试（预算内），重试失败向上抛并由调用方写回错误。
/// </summary>
public sealed class OneDriveSyncService
{
    private const string DefaultDeltaUrl = "https://graph.microsoft.com/v1.0/me/drive/root/delta?$top=200";
    private const string RootParentPath = "/drive";
    private const int ThrottleRetryBudget = 3;
    private const int MaxPages = 100_000;
    private const int MaxAuthRefreshes = 5;

    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly OneDriveTokenService _tokens;
    private readonly OneDriveSyncGate _gate;
    private readonly ILogger<OneDriveSyncService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveSyncService(
        PimDbContext db,
        IOneDriveGraphClient client,
        OneDriveTokenService tokens,
        ILogger<OneDriveSyncService>? logger = null,
        TimeProvider? clock = null,
        OneDriveSyncGate? gate = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _gate = gate ?? new OneDriveSyncGate();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<OneDriveSyncResult> SyncAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new DomainException(5320, "OneDrive 绑定不存在");
        if (provider.Provider != "onedrive")
        {
            throw new DomainException(5323, "该文件来源不是 OneDrive");
        }
        if (provider.Status != "connected")
        {
            throw new DomainException(5321, "OneDrive 尚未完成绑定");
        }

        if (!_gate.TryEnter(providerId))
        {
            throw new DomainException(5335, "该绑定正在同步中，请稍后再试");
        }

        try
        {
            return await SyncCoreAsync(provider, providerId, ct);
        }
        finally
        {
            _gate.Exit(providerId);
        }
    }

    private async Task<OneDriveSyncResult> SyncCoreAsync(
        FileProviderEntity provider,
        Guid providerId,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var accessToken = await _tokens.GetAccessTokenAsync(providerId, ct);

        provider.SyncStatus = "syncing";
        provider.LastError = null;
        await _db.SaveChangesAsync(ct);

        var url = string.IsNullOrEmpty(provider.DeltaLink) ? DefaultDeltaUrl : provider.DeltaLink!;
        var fullRecrawl = false;
        var authRefreshes = 0;
        var pagesProcessed = 0;
        long itemsApplied = 0;
        long itemsDeleted = 0;
        string? finalDeltaLink = null;
        var throttleRetries = 0;

        try
        {
            while (pagesProcessed < MaxPages)
            {
                OneDriveDeltaPage page;
                try
                {
                    page = await _client.GetDeltaPageAsync(accessToken, url, ct);
                    throttleRetries = 0;
                }
                catch (OneDriveGraphException exception) when (exception.StatusCode == (int)HttpStatusCode.Gone)
                {
                    // 游标失效：从头全量重扫，重扫结束后按 LastSeenAt 清理缺失项。
                    // 护栏：默认起点就 410（或重扫中再次 410）说明 Graph 行为异常，直接失败避免无限循环。
                    if (fullRecrawl || url == DefaultDeltaUrl)
                    {
                        throw;
                    }

                    url = DefaultDeltaUrl;
                    fullRecrawl = true;
                    provider.DeltaResetAt = now;
                    continue;
                }
                catch (OneDriveGraphException exception) when (exception.StatusCode == 401 && authRefreshes < MaxAuthRefreshes)
                {
                    // 首次全量可能爬取超过 token 有效期：失效缓存并重新刷新后重试当前页（上限内允许多次）
                    authRefreshes++;
                    _tokens.InvalidateCached(providerId);
                    accessToken = await _tokens.GetAccessTokenAsync(providerId, ct);
                    continue;
                }
                catch (OneDriveGraphException exception) when (exception.StatusCode == 429)
                {
                    throttleRetries++;
                    if (throttleRetries >= ThrottleRetryBudget)
                    {
                        throw;
                    }

                    var delaySeconds = Math.Max(1, exception.RetryAfterSeconds ?? throttleRetries);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                    continue; // 同一 URL 重试
                }

                pagesProcessed++;
                itemsDeleted += await ApplyPageAsync(providerId, page.Items, now, ct);
                itemsApplied += page.Items.Count(change => !change.IsRemoved);

                provider.SyncedItemCount = itemsApplied;
                provider.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                if (page.DeltaLink is not null)
                {
                    finalDeltaLink = page.DeltaLink;
                    break;
                }
                if (page.NextLink is null)
                {
                    break;
                }
                url = page.NextLink;
            }

            if (fullRecrawl)
            {
                itemsDeleted += await SoftDeleteStaleAsync(providerId, now, ct);
                if (finalDeltaLink is null)
                {
                    // 旧游标已被证明失效：重扫未走到 deltaLink 时不能保留，否则下轮从头再扫
                    provider.DeltaLink = null;
                }
            }

            provider.DeltaLink = finalDeltaLink ?? provider.DeltaLink;
            provider.LastSyncAt = now;
            provider.SyncedItemCount = itemsApplied;
            provider.SyncStatus = "idle";
            provider.LastError = null;
            provider.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            _logger?.LogInformation(
                "OneDrive sync done for provider {ProviderId}: pages={Pages} applied={Applied} deleted={Deleted} fullRecrawl={FullRecrawl}",
                providerId, pagesProcessed, itemsApplied, itemsDeleted, fullRecrawl);
            return new OneDriveSyncResult(pagesProcessed, (int)itemsApplied, (int)itemsDeleted, fullRecrawl);
        }
        catch (DbUpdateConcurrencyException)
        {
            // provider 已被断开删除：原异常已无意义，向上报明确的领域错误
            throw new DomainException(5320, "OneDrive 绑定不存在");
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

    /// <summary>按页批量应用变更：一次查询页内全部既有行，内存合并后单次保存（首扫数万项的性能关键）。</summary>
    private async Task<long> ApplyPageAsync(
        Guid providerId,
        IReadOnlyList<OneDriveDeltaItem> changes,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var upserts = new List<OneDriveDeltaItem>();
        var removalIds = new List<string>();
        foreach (var change in changes)
        {
            if (change.IsRemoved)
            {
                removalIds.Add(change.Id);
            }
            else
            {
                upserts.Add(change);
            }
        }

        var ids = upserts.Select(change => change.Id).Concat(removalIds).ToList();
        var existing = ids.Count == 0
            ? new Dictionary<string, FileItemEntity>(StringComparer.Ordinal)
            : (await _db.Set<FileItemEntity>()
                    .Where(item => item.ProviderId == providerId && ids.Contains(item.ExternalFileId))
                    .ToListAsync(ct))
                .ToDictionary(item => item.ExternalFileId, StringComparer.Ordinal);

        foreach (var change in upserts)
        {
            var path = DerivePath(change.ParentPath, change.Name);
            if (!existing.TryGetValue(change.Id, out var item))
            {
                item = new FileItemEntity
                {
                    ProviderId = providerId,
                    ExternalFileId = change.Id,
                    CreatedAt = now,
                };
                _db.Set<FileItemEntity>().Add(item);
                existing[change.Id] = item;
            }

            item.ParentExternalFileId = change.ParentId;
            item.Path = path;
            item.Name = change.Name;
            item.ItemType = change.IsFolder ? "folder" : "file";
            item.MimeType = change.MimeType;
            item.Size = change.Size;
            item.Etag = change.Ctag;
            item.IsDeleted = false;
            item.DeletedAt = null;
            item.LastSeenAt = now;
            item.ModifiedAt = change.ModifiedAt == DateTimeOffset.UnixEpoch ? item.CreatedAt : change.ModifiedAt;
            item.SyncedAt = now;
        }

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

        return deleted;
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

    internal static string DerivePath(string? parentPath, string name)
    {
        // 根节点：parentReference.path == "/drive"
        if (parentPath is null || parentPath == RootParentPath)
        {
            return "/";
        }

        var marker = "/drive/root:";
        var relative = parentPath.StartsWith(marker, StringComparison.Ordinal)
            ? parentPath[marker.Length..]
            : parentPath == "/drive" ? string.Empty : parentPath;
        if (relative.Length == 0)
        {
            return "/" + name;
        }
        return relative + "/" + name;
    }
}
