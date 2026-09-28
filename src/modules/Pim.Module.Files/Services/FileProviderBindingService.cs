using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

/// <summary>
/// 文件来源（provider）读取服务（文件模块 v2）。
///
/// v2 只有 OneDrive 一种来源：绑定走设备码（<see cref="OneDriveBindingService"/>）、
/// 内容与写操作走 Graph（<see cref="OneDriveContentService"/> / <see cref="OneDriveWriteService"/>）。
/// Nextcloud/WebDAV 绑定、连接测试与连接解析随 P4 一并退役，因此本服务不再依赖任何适配器。
/// </summary>
public sealed class FileProviderBindingService(
    PimDbContext db,
    ICurrentUserService currentUser)
{
    private readonly PimDbContext _db = db;
    private readonly ICurrentUserService _currentUser = currentUser;

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<IReadOnlyList<FileProviderDto>> ListProvidersAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var providers = await _db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(provider => provider.UserId == userId)
            .OrderBy(provider => provider.Provider)
            .ThenBy(provider => provider.Username)
            .ToListAsync(ct);

        // #353：一次查询取回各 provider 的已索引总量（未删除），避免逐个 provider 再查一次。
        var providerIds = providers.Select(provider => provider.Id).ToList();
        var indexedTotals = providerIds.Count == 0
            ? new Dictionary<Guid, long>()
            : await _db.Set<FileItemEntity>()
                .AsNoTracking()
                .Where(item => providerIds.Contains(item.ProviderId) && !item.IsDeleted)
                .GroupBy(item => item.ProviderId)
                .Select(group => new { ProviderId = group.Key, Total = (long)group.Count() })
                .ToDictionaryAsync(item => item.ProviderId, item => item.Total, ct);

        return providers
            .Select(provider => MapProvider(
                provider,
                indexedTotals.TryGetValue(provider.Id, out var total) ? total : 0))
            .ToList();
    }

    private static FileProviderDto MapProvider(FileProviderEntity provider, long totalIndexedCount = 0)
        => new(
            provider.Id,
            provider.Provider,
            provider.BaseUrl,
            provider.InternalBaseUrl,
            provider.Username,
            provider.Status,
            provider.LastSyncAt,
            provider.LastError,
            provider.CreatedAt,
            provider.UpdatedAt,
            provider.ClientId,
            provider.DriveId,
            provider.AccountId,
            provider.AccountName,
            provider.SyncStatus,
            provider.SyncedItemCount,
            provider.DeltaResetAt,
            provider.TokenExpiresAt,
            totalIndexedCount);
}
