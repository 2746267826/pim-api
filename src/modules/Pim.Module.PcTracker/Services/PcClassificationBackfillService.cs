using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Caching;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

/// <summary>分类快照后台补齐统计：processedDays = 实际执行 ensure 的业务日数；writtenSnapshots = 新增快照数。</summary>
public sealed record PcClassificationBackfillStats(int ProcessedDays, int WrittenSnapshots);

/// <summary>分类快照后台定时补齐：对最近 lookbackDays 个业务日（Asia/Shanghai，窗口 [D 04:00, D+1 04:00)），
/// 无事件日跳过；今天（窗口含 now）总是 ensure；过去日仅整日缺口（窗口内快照数为 0）才补齐。
/// 复用 ActivityClassificationRecomputeService 的共享核心，auditId 传 null 不写审计。
/// record_key 级幂等（PcActivityRecordKeyService）保证重入安全。完成后驱逐 /api/v1/pc/ 缓存前缀。</summary>
public sealed class PcClassificationBackfillService
{
    private const int BusinessDayStartHour = 4;
    private const string DefaultTimezoneName = "Asia/Shanghai";
    private const string ChinaFallbackTimezone = "China Standard Time";
    private const string PcCachePrefix = "/api/v1/pc/";

    private readonly PimDbContext _db;
    private readonly ActivityClassificationRecomputeService _recompute;
    private readonly TimeProvider _timeProvider;
    private readonly IAggregateResultCache _cache;
    private readonly ILogger<PcClassificationBackfillService> _logger;

    public PcClassificationBackfillService(
        PimDbContext db,
        ActivityClassificationRecomputeService recompute,
        TimeProvider timeProvider,
        IAggregateResultCache cache,
        ILogger<PcClassificationBackfillService> logger)
    {
        _db = db;
        _recompute = recompute;
        _timeProvider = timeProvider;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PcClassificationBackfillStats> BackfillAsync(
        int lookbackDays = 14,
        CancellationToken ct = default)
    {
        if (lookbackDays < 1)
            lookbackDays = 1;

        var timeZone = ResolveTimezone();
        var now = _timeProvider.GetUtcNow();
        var todayLocal = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var processedDays = 0;
        long writtenSnapshots = 0;

        for (var dayIndex = lookbackDays - 1; dayIndex >= 0; dayIndex--)
        {
            var day = todayLocal.AddDays(-dayIndex);
            var startUtc = ToBusinessDayStartUtc(day, timeZone);
            var endUtc = ToBusinessDayStartUtc(day.AddDays(1), timeZone);

            var hasEvents = await _db.Set<AwEventEntity>()
                .AnyAsync(e => e.Duration > 0 && e.Timestamp >= startUtc && e.Timestamp < endUtc, ct)
                || await _db.Set<TrackerEventEntity>()
                .AnyAsync(e => e.Duration > 0 && e.Timestamp >= startUtc && e.Timestamp < endUtc, ct);
            if (!hasEvents)
                continue;

            var isCurrentWindow = startUtc <= now && now < endUtc;
            if (!isCurrentWindow)
            {
                // #331：即使整日快照齐全，也可能存在**过时**的非应用记录（gap/idle/afk）
                // —— 它们曾被旧规则判成「游戏」等应用类别。若这里直接跳过，
                // 历史污染数据永远进不了 EnsureClassificationsAsync，修复只对新数据生效。
                // 因此把「存在 stale inactive 快照」也视为需要补齐。
                var hasStaleInactiveSnapshots = await HasStaleInactiveSnapshotsAsync(startUtc, endUtc, ct);

                var snapshotCount = await _db.Set<ActivityClassificationEntity>()
                    .CountAsync(snapshot => snapshot.StartedAt >= startUtc && snapshot.StartedAt < endUtc, ct);
                if (snapshotCount > 0 && !hasStaleInactiveSnapshots)
                {
                    var earliestSnapshotStart = await _db.Set<ActivityClassificationEntity>()
                        .Where(s => s.StartedAt >= startUtc && s.StartedAt < endUtc)
                        .MinAsync(s => (DateTimeOffset?)s.StartedAt, ct);

                    var latestSnapshotEnd = await _db.Set<ActivityClassificationEntity>()
                        .Where(s => s.StartedAt >= startUtc && s.StartedAt < endUtc)
                        .MaxAsync(s => (DateTimeOffset?)s.EndedAt, ct);

                    var hasUncoveredEvents = (latestSnapshotEnd is not null && (
                            await _db.Set<TrackerEventEntity>().AnyAsync(e => e.Duration > 0 && e.Timestamp >= latestSnapshotEnd && e.Timestamp < endUtc, ct) ||
                            await _db.Set<AwEventEntity>().AnyAsync(e => e.Duration > 0 && e.Timestamp >= latestSnapshotEnd && e.Timestamp < endUtc, ct)))
                        || (earliestSnapshotStart is not null && (
                            await _db.Set<TrackerEventEntity>().AnyAsync(e => e.Duration > 0 && e.Timestamp >= startUtc && e.Timestamp < earliestSnapshotStart, ct) ||
                            await _db.Set<AwEventEntity>().AnyAsync(e => e.Duration > 0 && e.Timestamp >= startUtc && e.Timestamp < earliestSnapshotStart, ct)));

                    if (!hasUncoveredEvents)
                        continue;
                }
            }

            var before = await _db.Set<ActivityClassificationEntity>().CountAsync(ct);
            await _recompute.EnsureSnapshotsForRangeAsync(startUtc, endUtc, auditId: null, ct);
            var after = await _db.Set<ActivityClassificationEntity>().CountAsync(ct);

            processedDays++;
            writtenSnapshots += after - before;
            _logger.LogDebug(
                "Classification snapshot backfill ensured business day {Day} (UTC [{Start}, {End})): +{Added} snapshots.",
                day.ToString("yyyy-MM-dd"),
                startUtc,
                endUtc,
                after - before);
        }

        _cache.EvictByPrefix(PcCachePrefix);
        return new PcClassificationBackfillStats(processedDays, checked((int)writtenSnapshots));
    }

    /// <summary>
    /// 该业务日是否存在「过时的非应用记录快照」（#331）：记录类型属于 gap/idle/afk，
    /// 但类别不是「未活动」——即修复前被旧规则写成的应用类别（如「游戏」）。
    /// 人工/LLM 纠正过的快照不算过时（它们本就允许带任意类别）。
    /// </summary>
    private async Task<bool> HasStaleInactiveSnapshotsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct)
    {
        var inactiveTypes = PcActivityOverlapResolver.InactiveRecordTypes.ToArray();
        var inactiveCategory = ActivityClassificationResult.InactiveCategoryName;

        return await _db.Set<ActivityClassificationEntity>()
            .AnyAsync(
                snapshot => snapshot.StartedAt >= startUtc
                    && snapshot.StartedAt < endUtc
                    && inactiveTypes.Contains(snapshot.RecordType)
                    && snapshot.CategoryName != inactiveCategory
                    && snapshot.Source != "manual"
                    && snapshot.Source != "corrected"
                    && snapshot.Source != "user_corrected"
                    && snapshot.Source != "llm_corrected",
                ct);
    }

    /// <summary>业务日 D 的窗口起点 [D 04:00 Asia/Shanghai) 换算为 UTC（口径与 PcActivityAggregationService 一致）。</summary>
    private static DateTimeOffset ToBusinessDayStartUtc(DateTime localDate, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDate.Date.AddHours(BusinessDayStartHour), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    /// <summary>固定 Asia/Shanghai（与缓存 TTL 口径一致）；系统缺失时按 China Standard Time 兜底。</summary>
    private static TimeZoneInfo ResolveTimezone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimezoneName);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ChinaFallbackTimezone);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ChinaFallbackTimezone);
        }
    }
}
