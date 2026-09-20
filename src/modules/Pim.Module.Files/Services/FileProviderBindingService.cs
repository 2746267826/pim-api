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

        return providers.Select(MapProvider).ToList();
    }

    private static FileProviderDto MapProvider(FileProviderEntity provider)
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
            provider.TokenExpiresAt);
}
