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

    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly OneDriveTokenService _tokens;
    private readonly ILogger<OneDriveSyncService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveSyncService(
        PimDbContext db,
        IOneDriveGraphClient client,
        OneDriveTokenService tokens,
        ILogger<OneDriveSyncService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
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

        var now = _clock.GetUtcNow();
        var accessToken = await _tokens.GetAccessTokenAsync(providerId, ct);

        provider.SyncStatus = "syncing";
        provider.LastError = null;
        await _db.SaveChangesAsync(ct);

        var url = string.IsNullOrEmpty(provider.DeltaLink) ? DefaultDeltaUrl : provider.DeltaLink!;
        var fullRecrawl = false;
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
                    // 游标失效：从头全量重扫，重扫结束后按 LastSeenAt 清理缺失项
                    url = DefaultDeltaUrl;
                    fullRecrawl = true;
                    provider.DeltaResetAt = now;
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
                foreach (var change in page.Items)
                {
                    if (change.IsRemoved)
                    {
                        itemsDeleted += await SoftDeleteAsync(providerId, change.Id, now, ct);
                        continue;
                    }

                    await UpsertAsync(providerId, change, now, ct);
                    itemsApplied++;
                }

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
        catch (Exception exception)
        {
            provider.SyncStatus = "error";
            provider.LastError = exception.Message;
            provider.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<long> SoftDeleteAsync(Guid providerId, string externalId, DateTimeOffset now, CancellationToken ct)
    {
        var items = await _db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == providerId && item.ExternalFileId == externalId && !item.IsDeleted)
            .ToListAsync(ct);
        foreach (var item in items)
        {
            item.IsDeleted = true;
            item.DeletedAt = now;
            item.SyncedAt = now;
        }
        if (items.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
        return items.Count;
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

    private async Task UpsertAsync(Guid providerId, OneDriveDeltaItem change, DateTimeOffset now, CancellationToken ct)
    {
        var item = await _db.Set<FileItemEntity>()
            .SingleOrDefaultAsync(
                existing => existing.ProviderId == providerId && existing.ExternalFileId == change.Id,
                ct);
        var path = DerivePath(change.ParentPath, change.Name);
        if (item is null)
        {
            item = new FileItemEntity
            {
                ProviderId = providerId,
                ExternalFileId = change.Id,
                CreatedAt = now,
            };
            _db.Set<FileItemEntity>().Add(item);
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
        await _db.SaveChangesAsync(ct);
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
