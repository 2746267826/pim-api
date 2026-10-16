using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

/// <summary>
/// 定时任务：遍历**全部**已连接的网盘账号（不限 provider、不限数量）逐个增量同步。
/// 系统上下文运行（EF 全局用户过滤器不生效），单条失败写回 provider 不中断其余。
///
/// F2.4：同步互斥与游标都按账号行隔离，因此多账号各自独立推进、互不覆盖。
/// </summary>
public sealed class FileSyncJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileProviderFactory _providers;
    private readonly ILogger<FileSyncJob>? _logger;

    public FileSyncJob(
        IServiceScopeFactory scopeFactory,
        IFileProviderFactory providers,
        ILogger<FileSyncJob>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// 手动触发某个账号的同步（REQ-25：后台化）。
    ///
    /// 由 HTTP 端点以 **Hangfire 后台任务**形式入队，端点本身立即返回「已开始」，
    /// 不阻塞请求；同步进行中用户可继续浏览。
    /// 系统上下文运行，不依赖请求作用域。
    /// </summary>
    public async Task RunOneAsync(Guid providerId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<FileTreeSyncEngine>();
        await syncService.SyncAsync(providerId);
    }

    public async Task RunAllAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var syncService = scope.ServiceProvider.GetRequiredService<FileTreeSyncEngine>();

        var supported = _providers.SupportedProviderKeys.ToList();
        var providerIds = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(provider => provider.Status == "connected" && supported.Contains(provider.Provider))
            .Select(provider => provider.Id)
            .ToListAsync();

        foreach (var providerId in providerIds)
        {
            db.ChangeTracker.Clear();
            try
            {
                await syncService.SyncAsync(providerId);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    exception,
                    "Scheduled files sync failed for provider {ProviderId}",
                    providerId);
            }
        }
    }
}
