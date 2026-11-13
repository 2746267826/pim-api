using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 入库耗时分解的落表入口（#568）。
///
/// 两条硬约束：
/// 1. **best-effort**：任何失败只记 Debug 日志，绝不改变入库结果、也绝不向上抛；
/// 2. **独立作用域**：写表不能落在入库事务里（失败路径已回滚，成功路径也不该为诊断多付一次事务）。
/// </summary>
public sealed class MobileSyncIngestTimingWriter
{
    /// <summary>超过这一段才抓阻塞快照，避免为每条记录都多打一次系统视图。</summary>
    private static readonly TimeSpan BlockerSampleThreshold = TimeSpan.FromSeconds(1);

    private const int BlockerSnapshotMaxChars = 500;
    private const int BlockerSnapshotMaxRows = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MobileSyncIngestTimingWriter> _logger;

    public MobileSyncIngestTimingWriter(
        IServiceScopeFactory scopeFactory,
        ILogger<MobileSyncIngestTimingWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task TryRecordAsync(
        MobileSyncIngestTimingRecord record,
        CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            if (!db.Database.IsRelational())
                return;

            var blockers = record.TotalMs >= (long)BlockerSampleThreshold.TotalMilliseconds
                || record.AdvisoryLockWaitMs >= (long)BlockerSampleThreshold.TotalMilliseconds
                    ? await TrySampleBlockersAsync(db, ct)
                    : null;

            db.Set<MobileSyncIngestTimingEntity>().Add(new MobileSyncIngestTimingEntity
            {
                UserId = record.UserId,
                DeviceId = Truncate(record.DeviceId, 128),
                BatchId = Truncate(record.BatchId, 128),
                WindowStartUtc = record.WindowStartUtc,
                WindowEndUtc = record.WindowEndUtc,
                Attempt = record.Attempt,
                Outcome = Truncate(record.Outcome, 32),
                ConnectionWaitMs = record.ConnectionWaitMs,
                AdvisoryLockWaitMs = record.AdvisoryLockWaitMs,
                SessionRebuildMs = record.SessionRebuildMs,
                MaterializeMs = record.MaterializeMs,
                ComputeMs = record.ComputeMs,
                CommitMs = record.CommitMs,
                TotalMs = record.TotalMs,
                Blockers = blockers,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 诊断数据永远不能影响入库结果（#568）。
            _logger.LogDebug(ex, "Mobile ingest timing record was not persisted.");
        }
    }

    /// <summary>保留期清理，由后台回收器按周期调用（#568）。</summary>
    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            if (!db.Database.IsRelational())
                return 0;

            var cutoff = DateTimeOffset.UtcNow - retention;
            return await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM mobile_sync_ingest_timings WHERE created_at < {cutoff}",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mobile ingest timing retention sweep failed.");
            return 0;
        }
    }

    /// <summary>
    /// 抓一份 <c>pg_stat_activity</c> 快照。定位批次不取设备咨询锁也会一起卡住（#568），
    /// 说明瓶颈在上游，这一段用来回答"当时谁在占着数据库"。
    /// </summary>
    private static async Task<string?> TrySampleBlockersAsync(PimDbContext db, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return null;

        try
        {
            var rows = await db.Database.SqlQueryRaw<string>(
                """
                SELECT (pid::text || '|' || state || '|' || COALESCE(wait_event_type, '-') || '/' ||
                        COALESCE(wait_event, '-') || '|' ||
                        ROUND(EXTRACT(EPOCH FROM (now() - COALESCE(xact_start, query_start)))::numeric, 1)::text || 's'
                       ) AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND state <> 'idle'
                ORDER BY COALESCE(xact_start, query_start) NULLS LAST
                LIMIT 5
                """).ToListAsync(ct);

            if (rows.Count == 0)
                return null;

            var joined = string.Join(" ; ", rows.Take(BlockerSnapshotMaxRows));
            return Truncate(joined, BlockerSnapshotMaxChars);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>一次入库尝试的耗时快照（#568）。</summary>
public sealed record MobileSyncIngestTimingRecord(
    Guid UserId,
    string DeviceId,
    string BatchId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int Attempt,
    string Outcome,
    long ConnectionWaitMs,
    long AdvisoryLockWaitMs,
    long SessionRebuildMs,
    long MaterializeMs,
    long ComputeMs,
    long CommitMs,
    long TotalMs);
