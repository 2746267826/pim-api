using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

/// <summary>
/// Hangfire 定时任务：遍历全部已连接的 OneDrive 绑定逐个增量同步。
/// 系统上下文运行（EF 全局用户过滤器不生效），单条失败写回 provider 不中断其余。
/// </summary>
public sealed class OneDriveSyncJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OneDriveSyncJob>? _logger;

    public OneDriveSyncJob(IServiceScopeFactory scopeFactory, ILogger<OneDriveSyncJob>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 手动触发某个 provider 的同步（REQ-25：后台化）。
    ///
    /// 由 HTTP 端点以 **Hangfire 后台任务**形式入队，端点本身立即返回「已开始」，
    /// 不阻塞请求；同步进行中用户可继续浏览（AC-25.1 / AC-25.3）。
    /// 系统上下文运行，不依赖请求作用域。
    /// </summary>
    public async Task RunOneAsync(Guid providerId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<OneDriveSyncService>();
        await syncService.SyncAsync(providerId);
    }

    public async Task RunAllAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var syncService = scope.ServiceProvider.GetRequiredService<OneDriveSyncService>();

        var providerIds = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(provider => provider.Provider == "onedrive" && provider.Status == "connected")
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
                    "OneDrive scheduled sync failed for provider {ProviderId}",
                    providerId);
            }
        }
    }
}
