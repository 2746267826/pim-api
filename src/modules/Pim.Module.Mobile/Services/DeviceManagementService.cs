using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class DeviceManagementService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public DeviceManagementService(PimDbContext db, ICurrentUserService currentUser, TimeProvider? timeProvider = null)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<DeviceListDto>> ListDevicesAsync(string? sortBy = null, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var devices = await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId).ToListAsync(ct);
        if (devices.Count == 0) return [];
        var deviceIds = devices.Select(d => d.DeviceId).ToList();
        // batch counts
        var sessCounts = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var evtCounts = await _db.Set<MobileUsageEventEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var locCounts = await _db.Set<MobileLocationPointEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var sumCounts = await _db.Set<MobileUsageSummaryEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var sessMap = sessCounts.ToDictionary(x => x.DeviceId, x => x.Count);
        var evtMap = evtCounts.ToDictionary(x => x.DeviceId, x => x.Count);
        var locMap = locCounts.ToDictionary(x => x.DeviceId, x => x.Count);
        var sumMap = sumCounts.ToDictionary(x => x.DeviceId, x => x.Count);
        // also batch anomalous/earliest/latest
        var anomalousMap = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId) && s.DurationMs > 8L * 60 * 60 * 1000).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var аномDict = anomalousMap.ToDictionary(x => x.DeviceId, x => x.Count);
        var earliestMap = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Earliest = g.Min(x => x.StartUtc) }).ToListAsync(ct);
        var latestMap = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Latest = g.Max(x => x.StartUtc) }).ToListAsync(ct);
        var earlyDict = earliestMap.ToDictionary(x => x.DeviceId, x => (DateTimeOffset?)x.Earliest);
        var lateDict = latestMap.ToDictionary(x => x.DeviceId, x => (DateTimeOffset?)x.Latest);
        var list = new List<DeviceListDto>();
        foreach (var d in devices)
        {
            var sc = sessMap.GetValueOrDefault(d.DeviceId); var ec = evtMap.GetValueOrDefault(d.DeviceId); var lc = locMap.GetValueOrDefault(d.DeviceId); var suc = sumMap.GetValueOrDefault(d.DeviceId);
            var est = (long)(sc * 0.5 + ec * 0.3 + lc * 0.2 + suc * 0.4);
            var stats = new DeviceStats(sc, ec, lc, suc, аномDict.GetValueOrDefault(d.DeviceId), earlyDict.GetValueOrDefault(d.DeviceId), lateDict.GetValueOrDefault(d.DeviceId), est);
            var health = GetHealth(d, stats);
            list.Add(new DeviceListDto(
                d.DeviceId, d.DisplayName, d.Brand, d.Model, d.OsVersion, d.AppVersion,
                d.RegisteredAtUtc, d.LastSeenAtUtc,
                _timeProvider.GetUtcNow() - d.LastSeenAtUtc < TimeSpan.FromMinutes(5),
                stats.SessionCount, stats.EventCount, stats.LocationCount, stats.SummaryCount,
                stats.Earliest, stats.Latest, stats.StorageEstimateKb,
                health.SyncStatus, health.DataQuality, health.StoragePressure));
        }
        var sorted = sortBy == "data" ? list.OrderByDescending(x => x.SessionCount + x.EventCount).ToList()
            : list.OrderByDescending(x => x.LastSeenAtUtc).ToList();
        return sorted;
    }

    public async Task<DeviceDetailDto> GetDetailAsync(string deviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");
        var stats = await GetStatsAsync(userId, deviceId, ct);
        var batches = await _db.Set<MobileSyncBatchEntity>().Where(b => b.UserId == userId && b.DeviceId == deviceId).OrderByDescending(b => b.CreatedAt).Take(10).ToListAsync(ct);
        var healthTimeline = BuildHealthTimeline(device);
        return new DeviceDetailDto(device, stats, batches.Select(b => new DeviceSyncHistoryDto(b.BatchId, b.CreatedAt, b.AcceptedCount, b.Status)).ToList(), healthTimeline);
    }

    public async Task<DeviceDto> RenameAsync(string deviceId, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 50) throw new DomainException(04000, "别名长度需 1-50");
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");
        device.DisplayName = displayName.Trim();
        device.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return new DeviceDto(device.DeviceId, device.DisplayName);
    }

    public async Task<DeviceMergePreviewDto> PreviewMergeAsync(IReadOnlyList<string> sourceDeviceIds, string targetDeviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var allIds = sourceDeviceIds.Concat(new[] { targetDeviceId }).Distinct().ToList();
        var devices = await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && allIds.Contains(d.DeviceId)).ToListAsync(ct);
        if (devices.Count != allIds.Count) throw new DomainException(04004, "部分设备不存在或不属于当前用户");
        // batch counts for preview
        var sessP = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && allIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var evtP = await _db.Set<MobileUsageEventEntity>().Where(s => s.UserId == userId && allIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var locP = await _db.Set<MobileLocationPointEntity>().Where(s => s.UserId == userId && allIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var sumP = await _db.Set<MobileUsageSummaryEntity>().Where(s => s.UserId == userId && allIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var sd = sessP.ToDictionary(x=>x.DeviceId,x=>x.Count); var ed = evtP.ToDictionary(x=>x.DeviceId,x=>x.Count); var ld = locP.ToDictionary(x=>x.DeviceId,x=>x.Count); var sud = sumP.ToDictionary(x=>x.DeviceId,x=>x.Count);
        var preview = new List<DeviceMergeItemDto>();
        long total = 0;
        foreach (var id in allIds)
        {
            var cnt = sd.GetValueOrDefault(id) + ed.GetValueOrDefault(id) + ld.GetValueOrDefault(id) + sud.GetValueOrDefault(id);
            preview.Add(new DeviceMergeItemDto(id, cnt));
            total += cnt;
        }
        return new DeviceMergePreviewDto(preview, total);
    }

    public async Task MergeAsync(IReadOnlyList<string> sourceDeviceIds, string targetDeviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        if (sourceDeviceIds.Contains(targetDeviceId)) throw new DomainException(04001, "源设备不能包含目标设备");
        var allIds = sourceDeviceIds.Concat(new[] { targetDeviceId }).Distinct().ToList();
        var devices = await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && allIds.Contains(d.DeviceId)).ToListAsync(ct);
        if (devices.Count != allIds.Count) throw new DomainException(04004, "部分设备不存在或不属于当前用户");
        var target = devices.Single(d => d.DeviceId == targetDeviceId);
        if (_db.Database.IsRelational())
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var sid in sourceDeviceIds)
            {
                await _db.Set<MobileUsageEventEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileUsageSessionEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileUsageSummaryEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileLocationPointEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileSyncBatchEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileTimelineBlockEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
            }
            await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && sourceDeviceIds.Contains(d.DeviceId)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            foreach (var sid in sourceDeviceIds)
            {
                await _db.Set<MobileUsageEventEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileUsageSessionEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileUsageSummaryEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileLocationPointEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileSyncBatchEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
                await _db.Set<MobileTimelineBlockEntity>().Where(e => e.UserId == userId && e.DeviceId == sid).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
            }
            await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && sourceDeviceIds.Contains(d.DeviceId)).ExecuteDeleteAsync(ct);
        }
    }

    public async Task<DeviceDeletePreviewDto> PreviewDeleteAsync(string deviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");
        var stats = await GetStatsAsync(userId, deviceId, ct);
        return new DeviceDeletePreviewDto(device.DeviceId, device.DisplayName, stats.SessionCount, stats.EventCount, stats.LocationCount, stats.SummaryCount);
    }

    public async Task DeleteAsync(string deviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");
        var syncing = await _db.Set<MobileSyncBatchEntity>().AnyAsync(b => b.UserId == userId && b.DeviceId == deviceId && b.Status == "syncing", ct);
        if (syncing) throw new DomainException(04002, "设备正在同步，禁止删除");
        if (_db.Database.IsRelational())
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Set<MobileUsageEventEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileUsageSessionEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileUsageSummaryEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileLocationPointEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileSyncBatchEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileTimelineBlockEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && d.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await _db.Set<MobileUsageEventEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileUsageSessionEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileUsageSummaryEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileLocationPointEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileSyncBatchEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileTimelineBlockEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
            await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && d.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        }
    }

    public async Task<DeviceExportDto> ExportAsync(string deviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");
        const int exportLimit = 5000;
        var sessions = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && s.DeviceId == deviceId).OrderByDescending(s => s.StartUtc).Take(exportLimit).ToListAsync(ct);
        var events = await _db.Set<MobileUsageEventEntity>().Where(s => s.UserId == userId && s.DeviceId == deviceId).OrderByDescending(s => s.EventTimestampUtc).Take(exportLimit).ToListAsync(ct);
        var locations = await _db.Set<MobileLocationPointEntity>().Where(s => s.UserId == userId && s.DeviceId == deviceId).OrderByDescending(s => s.RecordedAtUtc).Take(exportLimit).ToListAsync(ct);
        var summaries = await _db.Set<MobileUsageSummaryEntity>().Where(s => s.UserId == userId && s.DeviceId == deviceId).OrderByDescending(s => s.WindowStartUtc).Take(exportLimit).ToListAsync(ct);
        var safeName = string.Join("_", device.DisplayName.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = device.DeviceId;
        safeName = safeName.Length > 30 ? safeName[..30] : safeName;
        var fileName = $"pim-export-{safeName}-{_timeProvider.GetUtcNow():yyyyMMdd}.json";
        var truncated = sessions.Count==exportLimit || events.Count==exportLimit || locations.Count==exportLimit || summaries.Count==exportLimit;
        var payload = new { device = device.DeviceId, sessions, events, locations, summaries, truncated };
        var json = JsonSerializer.Serialize(payload);
        return new DeviceExportDto(fileName, json);
    }

    private DeviceHealthDto GetHealth(MobileDeviceEntity device, DeviceStats stats)
    {
        var now = _timeProvider.GetUtcNow();
        var syncStatus = (now - device.LastSeenAtUtc) switch
        {
            var age when age > TimeSpan.FromDays(1) => "disconnected",
            var age when age > TimeSpan.FromHours(1) => "delayed",
            _ => "normal"
        };
        var dataQuality = stats.AnomalousSessionCount > 0 ? "abnormal" : "normal";
        var storagePressure = "normal";
        try
        {
            if (!string.IsNullOrWhiteSpace(device.MetadataJson))
            {
                using var doc = JsonDocument.Parse(device.MetadataJson);
                if (doc.RootElement.TryGetProperty("pendingUpload", out var p) && p.GetInt32() > 0) storagePressure = "pending";
            }
        }
        catch { }
        return new DeviceHealthDto(syncStatus, dataQuality, storagePressure);
    }

    private IReadOnlyList<string> BuildHealthTimeline(MobileDeviceEntity device)
    {
        var now = _timeProvider.GetUtcNow();
        // 基于 LastSeenAtUtc 推断 7 天在线状态：当天有活跃视为在线
        return Enumerable.Range(0, 7).Select(i =>
        {
            var day = now.AddDays(-i).Date;
            var isOnlineDay = device.LastSeenAtUtc.Date == day;
            return $"{day:yyyy-MM-dd}:{(isOnlineDay ? "online" : "offline")}";
        }).ToList();
    }

    private async Task<DeviceStats> GetStatsAsync(Guid userId, string deviceId, CancellationToken ct)
    {
        var sessionCount = await _db.Set<MobileUsageSessionEntity>().CountAsync(s => s.UserId == userId && s.DeviceId == deviceId, ct);
        var eventCount = await _db.Set<MobileUsageEventEntity>().CountAsync(s => s.UserId == userId && s.DeviceId == deviceId, ct);
        var locationCount = await _db.Set<MobileLocationPointEntity>().CountAsync(s => s.UserId == userId && s.DeviceId == deviceId, ct);
        var summaryCount = await _db.Set<MobileUsageSummaryEntity>().CountAsync(s => s.UserId == userId && s.DeviceId == deviceId, ct);
        var anomalous = await _db.Set<MobileUsageSessionEntity>().CountAsync(s => s.UserId == userId && s.DeviceId == deviceId && s.DurationMs > 8L * 60 * 60 * 1000, ct);
        var earliest = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && s.DeviceId == deviceId).OrderBy(s => s.StartUtc).Select(s => (DateTimeOffset?)s.StartUtc).FirstOrDefaultAsync(ct);
        var latest = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && s.DeviceId == deviceId).OrderByDescending(s => s.StartUtc).Select(s => (DateTimeOffset?)s.StartUtc).FirstOrDefaultAsync(ct);
        // 估算: session ~0.5KB, event ~0.3KB, location ~0.2KB (基于平均行大小)
        var estimateKb = (long)(sessionCount * 0.5 + eventCount * 0.3 + locationCount * 0.2 + summaryCount * 0.4);
        return new DeviceStats(sessionCount, eventCount, locationCount, summaryCount, anomalous, earliest, latest, estimateKb);
    }

    private sealed record DeviceStats(int SessionCount, int EventCount, int LocationCount, int SummaryCount, int AnomalousSessionCount, DateTimeOffset? Earliest, DateTimeOffset? Latest, long StorageEstimateKb);
    private sealed record DeviceHealthDto(string SyncStatus, string DataQuality, string StoragePressure);
}

public sealed record DeviceListDto(string DeviceId, string DisplayName, string Brand, string Model, string OsVersion, string AppVersion, DateTimeOffset RegisteredAtUtc, DateTimeOffset LastSeenAtUtc, bool IsOnline, int SessionCount, int EventCount, int LocationCount, int SummaryCount, DateTimeOffset? Earliest, DateTimeOffset? Latest, long StorageEstimateKb, string SyncStatus, string DataQuality, string StoragePressure);
public sealed record DeviceDetailDto(MobileDeviceEntity Device, object Stats, IReadOnlyList<DeviceSyncHistoryDto> SyncHistory, IReadOnlyList<string> HealthTimeline);
public sealed record DeviceSyncHistoryDto(string BatchId, DateTimeOffset CreatedAt, int AcceptedCount, string Status);
public sealed record DeviceDto(string DeviceId, string DisplayName);
public sealed record DeviceMergeItemDto(string DeviceId, long DataCount);
public sealed record DeviceMergePreviewDto(IReadOnlyList<DeviceMergeItemDto> Items, long Total);
public sealed record DeviceDeletePreviewDto(string DeviceId, string DisplayName, int SessionCount, int EventCount, int LocationCount, int SummaryCount);
public sealed record DeviceExportDto(string FileName, string Json);
