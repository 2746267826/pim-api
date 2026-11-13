using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// 额度是**保护数据库连接池**的上限，不是吞吐上限：同设备的串行由
/// <c>pg_advisory_xact_lock(user, device)</c> 负责，跨设备不该互相排队——所以额度用满时不再排队等待，
/// 而是退化成"跟请求同生命周期"的老路径（见 <see cref="RunAsync"/>），避免把别的设备卡在信号量上。
/// </summary>
public sealed class MobileSyncIngestRunner : IDisposable
{
    internal const int DefaultConcurrencyLimit = 8;
    internal const int MaxConcurrencyLimit = 16;
    internal const string ConcurrencyLimitKey = "Mobile:Sync:IngestContinuationLimit";

    /// <summary>拿不到额度的最长等待；超过就降级，而不是让请求一直挂到客户端 90 秒超时。</summary>
    internal static readonly TimeSpan GateWaitTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MobileSyncIngestRunner> _logger;
    private readonly SemaphoreSlim _gate;

    public MobileSyncIngestRunner(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<MobileSyncIngestRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
        Limit = ResolveConcurrencyLimit(configuration[ConcurrencyLimitKey]);
        _gate = new SemaphoreSlim(Limit);
    }

    /// <summary>当前生效的并发上限（诊断用）。</summary>
    public int Limit { get; }

    /// <summary>
    /// 执行一次入库。
    ///
    /// 正常路径：拿到执行额度后，工作只跟服务端有关（停服信号 + 服务端自己的 12 分钟预算），
    /// 客户端断开也照常跑完。
    ///
    /// 饱和路径：额度用满且等待超时，退化为跟请求同生命周期执行。理由是排队比放弃更糟——
    /// 排队会让请求一直挂着直到客户端 90 秒超时，跨设备形成互相阻塞；退化之后客户端断开会走
    /// "中断退回租约 + 后台回收"，批次同样能收敛。
    ///
    /// 用户身份由调用方在原请求作用域解析后传入——独立作用域里没有 <c>HttpContext</c>。
    /// </summary>
    public async Task<MobileUsageIngestResult> RunAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        CancellationToken requestAborted = default)
    {
        var acquired = await _gate.WaitAsync(GateWaitTimeout, requestAborted).ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<MobileUsageIngestService>();

            if (!acquired)
            {
                _logger.LogWarning(
                    "Mobile ingest continuation slots are saturated ({Limit}); falling back to request-lifetime ingest for device {DeviceId}",
                    Limit,
                    request.DeviceId);
                return await service.IngestForUserAsync(userId, request, requestAborted).ConfigureAwait(false);
            }

            using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            return await service.IngestForUserAsync(userId, request, work.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
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
