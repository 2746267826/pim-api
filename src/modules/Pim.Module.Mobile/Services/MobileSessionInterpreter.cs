using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileSessionInterpreter
{
    private readonly PimDbContext _db;

    public MobileSessionInterpreter(PimDbContext db)
    {
        _db = db;
    }

    public async Task RebuildSessionsAsync(
        Guid userId,
        string deviceId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default)
    {
        var existing = await _db.Set<MobileUsageSessionEntity>()
            .Where(s => s.UserId == userId
                && s.DeviceId == deviceId
                && s.StartUtc < rangeEndUtc
                && (s.EndUtc == null || s.EndUtc > rangeStartUtc))
            .ToListAsync(ct);
        _db.Set<MobileUsageSessionEntity>().RemoveRange(existing);

        var events = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && e.DeviceId == deviceId
                && e.EventTimestampUtc >= rangeStartUtc
                && e.EventTimestampUtc <= rangeEndUtc)
            .OrderBy(e => e.EventTimestampUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        MobileUsageEventEntity? open = null;
        foreach (var usageEvent in events)
        {
            if (IsForeground(usageEvent.EventType))
            {
                if (open is not null)
                    AddSession(open, usageEvent.EventTimestampUtc, "[\"closed-by-app-switch\"]");

                open = usageEvent;
                continue;
            }

            if (IsBackground(usageEvent.EventType)
                && open is not null
                && string.Equals(open.PackageName, usageEvent.PackageName, StringComparison.Ordinal))
            {
                AddSession(open, usageEvent.EventTimestampUtc, "[]");
                open = null;
            }
        }

        if (open is not null)
            AddSession(open, rangeEndUtc, "[\"open-ended\"]");

        await _db.SaveChangesAsync(ct);
    }

    private void AddSession(MobileUsageEventEntity startEvent, DateTimeOffset endUtc, string qualityFlagsJson)
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

        _db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = startEvent.UserId,
            DeviceId = startEvent.DeviceId,
            PackageName = startEvent.PackageName,
            StartUtc = startEvent.EventTimestampUtc,
            EndUtc = endUtc,
            DurationMs = duration,
            QualityFlagsJson = flags,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static bool IsForeground(string eventType)
        => string.Equals(eventType, "MOVE_TO_FOREGROUND", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackground(string eventType)
        => string.Equals(eventType, "MOVE_TO_BACKGROUND", StringComparison.OrdinalIgnoreCase);
}
