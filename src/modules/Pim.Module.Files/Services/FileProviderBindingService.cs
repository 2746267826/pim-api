using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

/// <summary>
/// 网盘**账号**读取服务（工单 05 / F2）。
///
/// 绑定走 <see cref="FileProviderAuthorizationService"/>（设备码与浏览器跳转两种形态），
/// 内容与写操作走 provider 抽象（<see cref="FileContentService"/> / <see cref="FileWriteService"/>）。
/// 本服务只做列表与映射，不再假设「一个用户一个来源」。
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
            .ThenBy(provider => provider.CreatedAt)
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
            totalIndexedCount,
            provider.IsDefault);
}
