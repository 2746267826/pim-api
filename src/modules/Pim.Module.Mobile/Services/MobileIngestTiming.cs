using System.Threading;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 单次入库尝试的耗时累加器（#568）。由 <see cref="IngestTimingScope"/> 以 AsyncLocal 携带，
/// 因此 <see cref="MobileSessionInterpreter"/> / <see cref="MobileAnalyticsMaterializationService"/>
/// 不必为了打点改签名。
/// </summary>
public sealed class MobileIngestTimingAccumulator
{
    private long _connectionWaitMs;
    private long _advisoryLockWaitMs;
    private long _sessionRebuildMs;
    private long _materializeMs;
    private long _commitMs;

    public int Attempt { get; set; }

    public long ConnectionWaitMs => Interlocked.Read(ref _connectionWaitMs);

    public long AdvisoryLockWaitMs => Interlocked.Read(ref _advisoryLockWaitMs);

    public long SessionRebuildMs => Interlocked.Read(ref _sessionRebuildMs);

    public long MaterializeMs => Interlocked.Read(ref _materializeMs);

    public long CommitMs => Interlocked.Read(ref _commitMs);

    /// <summary>连接等待取「最大值」：执行策略重跑时只有首次真正付出了建连/取池成本。</summary>
    public void SetConnectionWait(long milliseconds)
        => InterlockedMax(ref _connectionWaitMs, milliseconds);

    /// <summary>等咨询锁是排队时间，重跑会各等一次，按累计记。</summary>
    public void AddAdvisoryLockWait(long milliseconds)
        => InterlockedAdd(ref _advisoryLockWaitMs, milliseconds);

    public void AddSessionRebuild(long milliseconds)
        => InterlockedAdd(ref _sessionRebuildMs, milliseconds);

    public void AddMaterialize(long milliseconds)
        => InterlockedAdd(ref _materializeMs, milliseconds);

    public void AddCommit(long milliseconds)
        => InterlockedAdd(ref _commitMs, milliseconds);

    private static void InterlockedAdd(ref long target, long value)
    {
        if (value <= 0)
            return;
        Interlocked.Add(ref target, value);
    }

    private static void InterlockedMax(ref long target, long value)
    {
        if (value <= 0)
            return;
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

/// <summary>
/// 把当前异步流里的入库尝试与 <see cref="MobileIngestTimingAccumulator"/> 关联（#568）。
///
/// 存在的理由：咨询锁是在 <see cref="MobileSessionInterpreter"/> 与
/// <see cref="MobileAnalyticsMaterializationService"/> 内部取的，若为了量「等锁」给它们加参数，
/// 会把一个纯诊断需求扩散到公开签名上。AsyncLocal 让它保持零签名改动，未开启时开销为零。
/// </summary>
public static class IngestTimingScope
{
    private static readonly AsyncLocal<MobileIngestTimingAccumulator?> Current = new();

    public static MobileIngestTimingAccumulator? Active => Current.Value;

    public static IDisposable Begin(MobileIngestTimingAccumulator accumulator)
    {
        var previous = Current.Value;
        Current.Value = accumulator;
        return new ScopeRestorer(previous);
    }

    public static void RecordAdvisoryLockWait(long milliseconds)
        => Current.Value?.AddAdvisoryLockWait(milliseconds);

    public static void RecordSessionRebuild(long milliseconds)
        => Current.Value?.AddSessionRebuild(milliseconds);

    public static void RecordMaterialize(long milliseconds)
        => Current.Value?.AddMaterialize(milliseconds);

    public static void RecordCommit(long milliseconds)
        => Current.Value?.AddCommit(milliseconds);

    public static void RecordConnectionWait(long milliseconds)
        => Current.Value?.SetConnectionWait(milliseconds);

    private sealed class ScopeRestorer(MobileIngestTimingAccumulator? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
