using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 移动端入库的执行入口（#568）。
///
/// 解决的问题：客户端 OkHttp <c>callTimeout</c> 是 90 秒，而服务端单批预算与生产实测都在分钟级。
/// 把入库直接绑在请求连接上，结果是每次上传都在客户端超时那一刻被取消——工作白做、租约留下、
/// 重投只能拿到 202 空等。这里把工作放进独立作用域，只绑定服务端预算与停服信号：
/// 客户端断开后批次仍会跑完并落终态，下一次重投直接拿到终态回放。
///
/// 并发上限按设备口径定：同一台设备的入库本来就串行在 <c>pg_advisory_xact_lock(user, device)</c> 上，
/// 多开并发买不到速度，只会多占池化连接。默认 2，配置上限 4。
/// </summary>
public sealed class MobileSyncIngestRunner : IDisposable
{
    internal const int DefaultConcurrencyLimit = 2;
    internal const int MaxConcurrencyLimit = 4;
    internal const string ConcurrencyLimitKey = "Mobile:Sync:IngestContinuationLimit";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SemaphoreSlim _gate;

    public MobileSyncIngestRunner(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        Limit = ResolveConcurrencyLimit(configuration[ConcurrencyLimitKey]);
        _gate = new SemaphoreSlim(Limit);
    }

    /// <summary>当前生效的并发上限（诊断用）。</summary>
    public int Limit { get; }

    /// <summary>
    /// 执行一次入库。
    ///
    /// <paramref name="requestAborted"/> 只在排队阶段生效：还没开始处理时客户端断开，直接放弃最划算；
    /// 一旦拿到执行额度，工作就只跟服务端有关（停服信号 + 服务端自己的 12 分钟预算）。
    /// 用户身份由调用方在原请求作用域解析后传入——独立作用域里没有 <c>HttpContext</c>。
    /// </summary>
    public async Task<MobileUsageIngestResult> RunAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        CancellationToken requestAborted = default)
    {
        await _gate.WaitAsync(requestAborted).ConfigureAwait(false);
        try
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<MobileUsageIngestService>();
            return await service.IngestForUserAsync(userId, request, work.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static int ResolveConcurrencyLimit(string? configured)
    {
        if (!int.TryParse(configured, out var parsed))
            return DefaultConcurrencyLimit;
        return Math.Clamp(parsed, 1, MaxConcurrencyLimit);
    }

    public void Dispose() => _gate.Dispose();
}
