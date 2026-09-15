using Microsoft.EntityFrameworkCore;
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
        CancellationToken ct = default)
    {
        // 反向窗口没有可表达的重建语义：宁可什么都不做，也不能先删后建不回来。
        if (rangeEndUtc < rangeStartUtc)
            return MobileSessionRebuildResult.None;

        var existing = await _db.Set<MobileUsageSessionEntity>()
            .Where(s => s.UserId == userId
                && s.DeviceId == deviceId
                && s.StartUtc <= rangeEndUtc
                && (s.EndUtc == null || s.EndUtc >= rangeStartUtc))
            .ToListAsync(ct);

        // 重建起点取被删会话的最早开始时间：每个被删会话的起点事件都落在 [重建起点, 窗口末端] 内，
        // 因此"删掉的每一条都能原样建回来"。没有会话可删时退化为窗口起点。
        var rebuildStartUtc = existing.Count == 0
            ? rangeStartUtc
            : existing.Min(session => session.StartUtc);

        if (existing.Count > 0)
            _db.Set<MobileUsageSessionEntity>().RemoveRange(existing);

        var events = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && e.DeviceId == deviceId
                && e.EventTimestampUtc >= rebuildStartUtc
                && e.EventTimestampUtc <= rangeEndUtc)
            .OrderBy(e => e.EventTimestampUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        MobileUsageEventEntity? open = null;
        var created = new List<MobileUsageSessionEntity>();
        foreach (var usageEvent in events)
        {
            if (IsForeground(usageEvent.EventType))
            {
                if (open is not null)
                    created.Add(AddSession(open, usageEvent.EventTimestampUtc, "[\"closed-by-app-switch\"]"));

                open = usageEvent;
                continue;
            }

            if (IsBackground(usageEvent.EventType)
                && open is not null
                && string.Equals(open.PackageName, usageEvent.PackageName, StringComparison.Ordinal))
            {
                created.Add(AddSession(open, usageEvent.EventTimestampUtc, "[]"));
                open = null;
            }
        }

        if (open is not null)
            created.Add(AddSession(open, rangeEndUtc, "[\"open-ended\"]"));

        await _db.SaveChangesAsync(ct);
        return new MobileSessionRebuildResult(existing.Count, created.Count);
    }

    private MobileUsageSessionEntity AddSession(MobileUsageEventEntity startEvent, DateTimeOffset endUtc, string qualityFlagsJson)
    {
        // 跨天/0ms/1ms边界：确保 EndUtc 合法且 DurationMs 精确
        if (endUtc < startEvent.EventTimestampUtc)
            endUtc = startEvent.EventTimestampUtc;
        var duration = Math.Max(0, (long)(endUtc - startEvent.EventTimestampUtc).TotalMilliseconds);
        // 0ms 与 1ms 为合法边缘，需保留但标记质量
        var flags = qualityFlagsJson;
        if (duration == 0 && !flags.Contains("zero-duration", StringComparison.OrdinalIgnoreCase))
            flags = flags == "[]" ? "[\"zero-duration\"]" : flags.TrimEnd(']') + ",\"zero-duration\"]";
        else if (duration == 1 && !flags.Contains("one-ms", StringComparison.OrdinalIgnoreCase))
            flags = flags == "[]" ? "[\"one-ms\"]" : flags.TrimEnd(']') + ",\"one-ms\"]";
        // 异常超长会话标记，后续聚合会过滤 (>8h)
        if (duration > 8L * 3600 * 1000 && !flags.Contains("anomalous_duration", StringComparison.OrdinalIgnoreCase))
            flags = flags == "[]" ? "[\"anomalous_duration\"]" : flags.TrimEnd(']') + ",\"anomalous_duration\"]";

        var session = new MobileUsageSessionEntity
        {
            UserId = startEvent.UserId,
            DeviceId = startEvent.DeviceId,
            PackageName = startEvent.PackageName,
            StartUtc = startEvent.EventTimestampUtc,
            EndUtc = endUtc,
            DurationMs = duration,
            QualityFlagsJson = flags,
            CreatedAt = _timeProvider.GetUtcNow()
        };
        _db.Set<MobileUsageSessionEntity>().Add(session);
        return session;
    }

    private static bool IsForeground(string eventType)
        => string.Equals(eventType, "MOVE_TO_FOREGROUND", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackground(string eventType)
        => string.Equals(eventType, "MOVE_TO_BACKGROUND", StringComparison.OrdinalIgnoreCase);
}
