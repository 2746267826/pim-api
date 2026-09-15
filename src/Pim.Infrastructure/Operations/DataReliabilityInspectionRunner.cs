using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Invariants;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 体检运行的协调器（#260）：把"读最近一次结果"和"重新体检"两种模式收敛到一处，
/// 并保证重复/并发调用不会堆积成多次全量扫库。
/// </summary>
public interface IDataReliabilityInspectionRunner
{
    /// <summary>读取最近一次结果；缓存为空时才真正跑一次体检。</summary>
    Task<DataReliabilityInspectionReport> GetLatestAsync(CancellationToken ct = default);

    /// <summary>强制重新体检。</summary>
    Task<DataReliabilityInspectionReport> RefreshAsync(CancellationToken ct = default);

    /// <summary>当前是否有一次体检正在执行。</summary>
    bool IsRunning { get; }
}

/// <summary>
/// 单飞（single-flight）实现：同一时刻只允许一次体检在跑，其它调用者直接 await 同一次运行并拿到同一份结果
/// （同一个 <c>Version</c>），因此"连点 5 次重新体检"不会产生 5 次全量扫库。
/// </summary>
public sealed class DataReliabilityInspectionRunner : IDataReliabilityInspectionRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataReliabilityInspectionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DataReliabilityInspectionRunner> _logger;
    private readonly object _gate = new();

    private Task<DataReliabilityInspectionReport>? _inFlight;

    public DataReliabilityInspectionRunner(
        IServiceScopeFactory scopeFactory,
        IDataReliabilityInspectionStore store,
        TimeProvider timeProvider,
        ILogger<DataReliabilityInspectionRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is { IsCompleted: false };
            }
        }
    }

    public Task<DataReliabilityInspectionReport> GetLatestAsync(CancellationToken ct = default)
    {
        var latest = _store.Latest;
        return latest != null ? Task.FromResult(latest) : StartRun(ct);
    }

    public Task<DataReliabilityInspectionReport> RefreshAsync(CancellationToken ct = default) => StartRun(ct);

    private Task<DataReliabilityInspectionReport> StartRun(CancellationToken ct)
    {
        // 共享运行不绑定调用方的取消令牌：一个客户端断开不应该把其它调用者正在等待的同一份结果一起取消。
        // 单次体检自身的超时保护由 InvariantOptions.InspectionTimeoutSeconds 在取数层兜底。
        _ = ct;

        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } running)
            {
                return running;
            }

            var task = Task.Run(() => RunOnceAsync(CancellationToken.None), CancellationToken.None);
            _inFlight = task;
            return task;
        }
    }

    private async Task<DataReliabilityInspectionReport> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            // Inspector 是 Scoped（持有 PimDbContext），必须在独立 scope 内解析。
            using var scope = _scopeFactory.CreateScope();
            var inspector = scope.ServiceProvider.GetRequiredService<IDataReliabilityReportInspector>();
            var report = await inspector.InspectReportAsync(_timeProvider.GetUtcNow(), ct);
            return _store.Publish(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据可信度体检执行失败");
            throw;
        }
        finally
        {
            // 无论成功、失败还是取消都要复位，否则一次失败会让后续调用永远拿到那个失败的 Task。
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }
}
