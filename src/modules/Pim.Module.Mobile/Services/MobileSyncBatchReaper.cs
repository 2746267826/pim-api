using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 孤儿批次回收器（#568）。
///
/// 两个动作，故意不做第三个：
/// <list type="number">
/// <item><b>回收过期租约</b>：处理者已经消失的 <c>pending</c> 批次把 <c>LeaseUntilUtc</c> 擦掉，
/// 让它重新可被认领。这里**不改状态**——把可重试的批次判死会让客户端后续重投撞 409 并永久卡住该窗口。</item>
/// <item><b>归档</b>：仅当窗口已超出补传期（<see cref="MobileGapService.MaxBackfillAge"/>），
/// 或同窗口已被成功批次覆盖时，才把 <c>pending</c> 归档成 <c>abandoned</c>；不按「多久没动静」判死，
/// 客户端本地待传行是无限期保留的，提前终态化会让本来能补回来的数据静默丢失。</item>
/// </list>
///
/// 顺带两个运维观测：长时间活跃的事务数（生产上「所有端点一起变慢」往往就是它），以及耗时表保留期清理。
/// 单实例用 advisory lock 防重入；两个 UPDATE 都带 <c>status = 'pending'</c> 条件，多实例也安全。
/// </summary>
public sealed class MobileSyncBatchReaper : BackgroundService
{
    /// <summary>租约过期后再等这么久才回收：与积压巡检「处理者已消失」的判定窗口一致。</summary>
    internal static readonly TimeSpan ReleaseGrace = MobileSyncBatchStatus.ActiveWindow;

    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan TimingRetention = TimeSpan.FromDays(30);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    /// <summary>长事务阈值：超过它的活跃事务会在下一次慢入库时被抓进 blockers。</summary>
    private static readonly TimeSpan LongTransactionThreshold = TimeSpan.FromMinutes(2);

    private const long ReaperLockKey = 0x5A17_5680_1EAF_0001;
    private const string ArchiveNote =
        "orphaned pending batch archived after its window left the backfill horizon, or the same window was already covered by a successful batch";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MobileSyncIngestTimingWriter _timingWriter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MobileSyncBatchReaper> _logger;

    public MobileSyncBatchReaper(
        IServiceScopeFactory scopeFactory,
        MobileSyncIngestTimingWriter timingWriter,
        TimeProvider timeProvider,
        ILogger<MobileSyncBatchReaper> logger)
    {
        _scopeFactory = scopeFactory;
        _timingWriter = timingWriter;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mobile sync batch reaper iteration failed");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>执行一轮回收。测试直接调用它。</summary>
    public async Task<MobileSyncReapSummary> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        if (!db.Database.IsRelational()
            || db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return MobileSyncReapSummary.Empty;

        var now = _timeProvider.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 单实例防重入：两个实例同时扫不会重复处理（两个 UPDATE 本就幂等，这里只是减少无谓竞争）。
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({ReaperLockKey})",
            ct);

        var released = await ReleaseExpiredLeasesAsync(db, now, ct);
        var archived = await ArchiveUnrecoverableAsync(db, now, ct);
        await RecordLongTransactionsAsync(db, ct);
        await tx.CommitAsync(ct);

        // 保留期清理走独立作用域，不参与上面的事务。
        var pruned = await _timingWriter.PruneAsync(TimingRetention, ct);

        if (released > 0 || archived > 0 || pruned > 0)
        {
            _logger.LogInformation(
                "Mobile sync reaper: released {Released} expired lease(s), archived {Archived} batch(es), pruned {Pruned} timing row(s)",
                released,
                archived,
                pruned);
        }

        return new MobileSyncReapSummary(released, archived, pruned);
    }

    /// <summary>
    /// 只擦租约、不改状态。只处理**显式持有过期租约**的行：擦成 JSON null 之后再跑一轮不会再命中，
    /// 因此重复执行是空操作。历史行（没有 LeaseUntilUtc 字段、没有 LeaseId）本来就被认领 SQL
    /// 按「created_at + 租约」判为可认领，不需要这里再写一次。
    /// </summary>
    private static async Task<int> ReleaseExpiredLeasesAsync(
        PimDbContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var cutoff = now - ReleaseGrace;
        FormattableString release = $@"
            UPDATE mobile_sync_batches
               SET error_json = jsonb_set(
                     jsonb_set(
                       COALESCE(error_json, '{{}}'::jsonb),
                       '{{LeaseUntilUtc}}', 'null'::jsonb, true),
                     '{{RetryAfterUtc}}', to_jsonb({now}), true)
             WHERE status = {MobileSyncBatchStatus.Pending}
               AND (error_json->>'LeaseUntilUtc') IS NOT NULL
               AND (error_json->>'LeaseUntilUtc')::timestamptz < {cutoff}
               AND COALESCE((error_json->>'RetryAfterUtc')::timestamptz, {now}) <= {now};";
        return await db.Database.ExecuteSqlInterpolatedAsync(release, ct);
    }

    /// <summary>
    /// 归档两种「数据已确定拿不回来」的 pending：窗口早于补传期，或同窗口已被成功批次覆盖。
    /// 不按时间沉默判死——见类型注释。
    /// </summary>
    private async Task<int> ArchiveUnrecoverableAsync(
        PimDbContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var backfillCutoff = now - MobileGapService.MaxBackfillAge;
        FormattableString archive = $@"
            UPDATE mobile_sync_batches AS b
               SET status = {MobileSyncBatchStatus.Abandoned},
                   completed_at_utc = {now},
                   error_json = COALESCE(b.error_json, '{{}}'::jsonb) || jsonb_build_object(
                       'FailureCode', 'abandoned-lease',
                       'ArchivedAtUtc', to_jsonb({now}),
                       'ArchiveNote', {ArchiveNote})
             WHERE b.status = {MobileSyncBatchStatus.Pending}
               AND (
                    b.window_end_utc < {backfillCutoff}
                    OR EXISTS (
                        SELECT 1 FROM mobile_sync_batches AS c
                         WHERE c.user_id = b.user_id
                           AND c.device_id = b.device_id
                           AND c.id <> b.id
                           AND c.status = {MobileSyncBatchStatus.Completed}
                           AND c.accepted_count > 0
                           AND c.window_start_utc = b.window_start_utc
                           AND c.window_end_utc = b.window_end_utc));";
        return await db.Database.ExecuteSqlInterpolatedAsync(archive, ct);
    }

    /// <summary>
    /// 长时间活跃事务计数。生产上「定位批次不取设备锁也跟着一起卡」只能由上游阻塞解释，
    /// 这个指标是那种现象最直接的证据。
    /// </summary>
    private async Task RecordLongTransactionsAsync(PimDbContext db, CancellationToken ct)
    {
        try
        {
            var threshold = _timeProvider.GetUtcNow() - LongTransactionThreshold;
            var count = await db.Database.SqlQuery<int>(
                    $"""
                     SELECT COUNT(*)::int AS "Value"
                     FROM pg_stat_activity
                     WHERE datname = current_database()
                       AND state = 'active'
                       AND COALESCE(xact_start, query_start) < {threshold}
                     """)
                .SingleAsync(ct);

            PimMetrics.SyncIngestLongTransactions.Set(count);
            if (count > 0)
            {
                _logger.LogWarning(
                    "Mobile sync reaper: {Count} database session(s) have been active for more than {Threshold}",
                    count,
                    LongTransactionThreshold);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Long transaction sampling failed");
        }
    }

}

/// <summary>一轮回收的结果（测试与日志共用）。</summary>
public sealed record MobileSyncReapSummary(int ReleasedLeases, int ArchivedBatches, int PrunedTimingRows)
{
    public static readonly MobileSyncReapSummary Empty = new(0, 0, 0);
}
