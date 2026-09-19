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
