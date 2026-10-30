using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 会话重建结果。删除数与创建数可用于判断一次重建的规模（重复上传同一窗口时应为 0 次调用，见 #248）。
/// </summary>
public sealed record MobileSessionRebuildResult(int DeletedCount, int CreatedCount)
{
    public static readonly MobileSessionRebuildResult None = new(0, 0);
}

public sealed class MobileSessionInterpreter
{
    private readonly PimDbContext _db;
    private readonly TimeProvider _timeProvider;

    // Test seam only. Production behavior is unchanged when unset.
    internal Func<CancellationToken, Task>? BeforeReconcileAsync { get; init; }

    public MobileSessionInterpreter(PimDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 重建 [rangeStartUtc, rangeEndUtc] 窗口内的会话。
    ///
    /// 删除与重建必须使用同一口径（#248）：删除按"与窗口重叠"（闭区间）命中会话，
    /// 重建则从被删会话中最早的开始时间起读事件 —— 起点落在窗口之外的会话因此也能被完整重建，
    /// 而不是"删掉却建不回来"（旧实现只读窗口内的事件，造成会话静默丢失）。
    /// </summary>
    public async Task<MobileSessionRebuildResult> RebuildSessionsAsync(
        Guid userId,
        string deviceId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default,
        IReadOnlyCollection<DateTimeOffset>? eventsAppliedInThisCall = null)
    {
        // 反向窗口没有可表达的重建语义：宁可什么都不做，也不能先删后建不回来。
        if (rangeEndUtc < rangeStartUtc)
            return MobileSessionRebuildResult.None;

        // Session reconciliation is read-modify-write. Take the existing (user, device)
        // materialization lock before the first read so two batches cannot both reconcile
        // from the same stale session snapshot. The lock is transaction-scoped and is
        // re-entrant for the later materialization call in the same ingest transaction.
        await AcquireDeviceLockAsync(userId, deviceId, ct);

        var existing = await _db.Set<MobileUsageSessionEntity>()
            .Where(s => s.UserId == userId
                && s.DeviceId == deviceId
                && s.StartUtc <= rangeEndUtc
                && (s.EndUtc == null || s.EndUtc >= rangeStartUtc))
            .ToListAsync(ct);

        // A tail-only batch may continue the one open session at the exact window boundary.
        // Its start event can be many hours earlier; rebuilding from that event would reread
        // the full history for a few milliseconds of new input. The caller supplies changed
        // event timestamps so an event exactly on the boundary still takes the full path.
        if (eventsAppliedInThisCall is not null
            && CanContinueBoundaryOpenSession(existing, rangeStartUtc, rangeEndUtc, eventsAppliedInThisCall))
        {
            return await ContinueBoundaryOpenSessionAsync(
                existing[0],
                rangeStartUtc,
                rangeEndUtc,
                ct);
        }

        // 重建起点同时覆盖请求窗口和已有会话：晚到事件可能早于最早的重叠会话，
        // 仍必须进入本次派生重建。空窗口则只从请求起点开始，不做任意回看。
        var rebuildStartUtc = existing.Count == 0
            ? rangeStartUtc
            : rangeStartUtc < existing.Min(session => session.StartUtc)
                ? rangeStartUtc
                : existing.Min(session => session.StartUtc);

        var events = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && e.DeviceId == deviceId
                && e.EventTimestampUtc >= rebuildStartUtc
                && e.EventTimestampUtc <= rangeEndUtc)
            .OrderBy(e => e.EventTimestampUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var plans = BuildSessionPlans(events, rangeEndUtc);
        if (BeforeReconcileAsync is not null)
            await BeforeReconcileAsync(ct);

        return await ReconcileSessionsAsync(existing, plans, ct);
    }

    private async Task AcquireDeviceLockAsync(Guid userId, string deviceId, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()
            || _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true
            || _db.Database.CurrentTransaction is null)
            return;

        var lockKey = MobileAnalyticsMaterializationLock.KeyFor(userId, deviceId);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            ct);
    }

    private static bool CanContinueBoundaryOpenSession(
        IReadOnlyList<MobileUsageSessionEntity> existing,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        IReadOnlyCollection<DateTimeOffset> eventsAppliedInThisCall)
        => existing.Count == 1
            && existing[0].EndUtc == rangeStartUtc
            && rangeStartUtc <= rangeEndUtc
            && HasFlag(existing[0].QualityFlagsJson, "open-ended")
            && !eventsAppliedInThisCall.Any(timestamp => timestamp <= rangeStartUtc);

    private async Task<MobileSessionRebuildResult> ContinueBoundaryOpenSessionAsync(
        MobileUsageSessionEntity existing,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct)
    {
        // Strictly greater than the boundary: the existing session was already materialized
        // through that instant. A newly applied boundary event is handled by the full path.
        var events = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == existing.UserId
                && e.DeviceId == existing.DeviceId
                && e.EventTimestampUtc > rangeStartUtc
                && e.EventTimestampUtc <= rangeEndUtc)
            .OrderBy(e => e.EventTimestampUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        MobileUsageSessionEntity? openSession = existing;
        var created = 0;
        foreach (var usageEvent in events)
        {
            if (IsForeground(usageEvent.EventType))
            {
                if (openSession is not null)
                {
                    ApplySessionEnd(
                        openSession,
                        usageEvent.EventTimestampUtc,
                        "closed-by-app-switch");
                }
                openSession = AddSession(
                    usageEvent,
                    usageEvent.EventTimestampUtc,
                    "open-ended");
                created++;
                continue;
            }

            if (IsBackground(usageEvent.EventType)
                && openSession is not null
                && string.Equals(openSession.PackageName, usageEvent.PackageName, StringComparison.Ordinal))
            {
                ApplySessionEnd(openSession, usageEvent.EventTimestampUtc);
                openSession = null;
            }
        }

        if (openSession is not null)
            ApplySessionEnd(openSession, rangeEndUtc, "open-ended");

        await _db.SaveChangesAsync(ct);
        return new MobileSessionRebuildResult(0, created);
    }

    private async Task<MobileSessionRebuildResult> ReconcileSessionsAsync(
        IReadOnlyList<MobileUsageSessionEntity> existing,
        IReadOnlyList<SessionPlan> plans,
        CancellationToken ct)
    {
        var existingByKey = existing
            .GroupBy(SessionKey.From)
            .ToDictionary(group => group.Key, group => group.ToList());
        var planCountByKey = plans
            .GroupBy(plan => plan.Key)
            .ToDictionary(group => group.Key, group => group.Count());
        var matched = new HashSet<Guid>();
        var created = new List<MobileUsageSessionEntity>();

        foreach (var plan in plans)
        {
            if (planCountByKey[plan.Key] == 1
                && existingByKey.TryGetValue(plan.Key, out var candidates)
                && candidates.Count == 1)
            {
                var session = candidates[0];
                ApplySessionEnd(
                    session,
                    plan.EndUtc,
                    preserveUnrelatedFlags: true,
                    existingFlagsJson: session.QualityFlagsJson,
                    flags: plan.Flags.ToArray());
                matched.Add(session.Id);
                continue;
            }

            created.Add(AddSession(
                plan.UserId,
                plan.DeviceId,
                plan.PackageName,
                plan.StartUtc,
                plan.EndUtc,
                preserveUnrelatedFlags: false,
                existingFlagsJson: null,
                flags: plan.Flags.ToArray()));
        }

        var removed = existing.Where(session => !matched.Contains(session.Id)).ToList();
        if (removed.Count > 0)
            _db.Set<MobileUsageSessionEntity>().RemoveRange(removed);

        await _db.SaveChangesAsync(ct);
        return new MobileSessionRebuildResult(removed.Count, created.Count);
    }

    private static IReadOnlyList<SessionPlan> BuildSessionPlans(
        IReadOnlyList<MobileUsageEventEntity> events,
        DateTimeOffset rangeEndUtc)
    {
        var plans = new List<SessionPlan>();
        MobileUsageEventEntity? open = null;
        foreach (var usageEvent in events)
        {
            if (IsForeground(usageEvent.EventType))
            {
                if (open is not null)
                    plans.Add(Plan(open, usageEvent.EventTimestampUtc, "closed-by-app-switch"));

                open = usageEvent;
                continue;
            }

            if (IsBackground(usageEvent.EventType)
                && open is not null
                && string.Equals(open.PackageName, usageEvent.PackageName, StringComparison.Ordinal))
            {
                plans.Add(Plan(open, usageEvent.EventTimestampUtc));
                open = null;
            }
        }

        if (open is not null)
            plans.Add(Plan(open, rangeEndUtc, "open-ended"));

        return plans;
    }

    private static SessionPlan Plan(
        MobileUsageEventEntity startEvent,
        DateTimeOffset endUtc,
        params string[] flags)
        => new(
            startEvent.UserId,
            startEvent.DeviceId,
            startEvent.PackageName,
            startEvent.EventTimestampUtc,
            endUtc,
            flags);

    private MobileUsageSessionEntity AddSession(
        MobileUsageEventEntity startEvent,
        DateTimeOffset endUtc,
        params string[] flags)
        => AddSession(
            startEvent.UserId,
            startEvent.DeviceId,
            startEvent.PackageName,
            startEvent.EventTimestampUtc,
            endUtc,
            preserveUnrelatedFlags: false,
            existingFlagsJson: null,
            flags);

    private MobileUsageSessionEntity AddSession(
        Guid userId,
        string deviceId,
        string packageName,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool preserveUnrelatedFlags,
        string? existingFlagsJson,
        params string[] flags)
    {
        var session = new MobileUsageSessionEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            PackageName = packageName,
            StartUtc = startUtc,
            EndUtc = startUtc,
            DurationMs = 0,
            QualityFlagsJson = "[]",
            CreatedAt = _timeProvider.GetUtcNow()
        };
        ApplySessionEnd(
            session,
            endUtc,
            preserveUnrelatedFlags,
            existingFlagsJson,
            flags);
        _db.Set<MobileUsageSessionEntity>().Add(session);
        return session;
    }

    private static void ApplySessionEnd(
        MobileUsageSessionEntity session,
        DateTimeOffset endUtc,
        params string[] flags)
        => ApplySessionEnd(
            session,
            endUtc,
            preserveUnrelatedFlags: false,
            existingFlagsJson: null,
            flags);

    private static void ApplySessionEnd(
        MobileUsageSessionEntity session,
        DateTimeOffset endUtc,
        bool preserveUnrelatedFlags,
        string? existingFlagsJson,
        params string[] flags)
    {
        // 跨天/0ms/1ms边界：确保 EndUtc 合法且 DurationMs 精确
        if (endUtc < session.StartUtc)
            endUtc = session.StartUtc;

        var duration = Math.Max(0, (long)(endUtc - session.StartUtc).TotalMilliseconds);
        var merged = new List<string>();
        if (preserveUnrelatedFlags && !string.IsNullOrWhiteSpace(existingFlagsJson))
        {
            try
            {
                foreach (var flag in JsonSerializer.Deserialize<string[]>(existingFlagsJson) ?? [])
                {
                    if (!DerivedQualityFlags.Contains(flag))
                        merged.Add(flag);
                }
            }
            catch (JsonException)
            {
                // Invalid legacy flags cannot describe a usage session; rebuild canonical flags.
            }
        }

        merged.AddRange(flags);
        if (duration == 0)
            merged.Add("zero-duration");
        else if (duration == 1)
            merged.Add("one-ms");
        if (duration > 8L * 3600 * 1000)
            merged.Add("anomalous_duration");

        session.EndUtc = endUtc;
        session.DurationMs = duration;
        session.QualityFlagsJson = JsonSerializer.Serialize(
            merged
                .Where(flag => !string.IsNullOrWhiteSpace(flag))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasFlag(string flagsJson, string flag)
        => flagsJson.Contains(flag, StringComparison.OrdinalIgnoreCase);

    private static bool IsForeground(string eventType)
        => string.Equals(eventType, "MOVE_TO_FOREGROUND", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackground(string eventType)
        => string.Equals(eventType, "MOVE_TO_BACKGROUND", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> DerivedQualityFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "open-ended",
        "closed-by-app-switch",
        "zero-duration",
        "one-ms",
        "anomalous_duration",
        "day_overflow"
    };

    private readonly record struct SessionKey(string PackageName, long StartUtcTicks)
    {
        public static SessionKey From(MobileUsageSessionEntity session)
            => new(session.PackageName, session.StartUtc.UtcDateTime.Ticks);
    }

    private sealed record SessionPlan(
        Guid UserId,
        string DeviceId,
        string PackageName,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        IReadOnlyList<string> Flags)
    {
        public SessionKey Key => new(PackageName, StartUtc.UtcDateTime.Ticks);
    }
}
