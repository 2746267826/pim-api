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
        var anomalousDict = anomalousMap.ToDictionary(x => x.DeviceId, x => x.Count);
        var earliestMap = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Earliest = g.Min(x => x.StartUtc) }).ToListAsync(ct);
        var latestMap = await _db.Set<MobileUsageSessionEntity>().Where(s => s.UserId == userId && deviceIds.Contains(s.DeviceId)).GroupBy(s => s.DeviceId).Select(g => new { DeviceId = g.Key, Latest = g.Max(x => x.StartUtc) }).ToListAsync(ct);
        var earlyDict = earliestMap.ToDictionary(x => x.DeviceId, x => (DateTimeOffset?)x.Earliest);
        var lateDict = latestMap.ToDictionary(x => x.DeviceId, x => (DateTimeOffset?)x.Latest);
        var list = new List<DeviceListDto>();
        foreach (var d in devices)
        {
            var sc = sessMap.GetValueOrDefault(d.DeviceId); var ec = evtMap.GetValueOrDefault(d.DeviceId); var lc = locMap.GetValueOrDefault(d.DeviceId); var suc = sumMap.GetValueOrDefault(d.DeviceId);
            var est = (long)(sc * 0.5 + ec * 0.3 + lc * 0.2 + suc * 0.4);
            var stats = new DeviceStats(sc, ec, lc, suc, anomalousDict.GetValueOrDefault(d.DeviceId), earlyDict.GetValueOrDefault(d.DeviceId), lateDict.GetValueOrDefault(d.DeviceId), est);
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
        // 与 MergeAsync 同样的入参形状：null / 空列表都返回 04001（HTTP 400），不要变成 500。
        if (sourceDeviceIds is not { Count: > 0 }) throw new DomainException(04001, "至少需要选择一台源设备");
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
        // is not { Count: > 0 } 同时挡住 null（请求体省略 sourceDeviceIds）与空列表，
        // 否则 null 会在下一行抛 NRE 被兜底成 HTTP 500。
        if (sourceDeviceIds is not { Count: > 0 }) throw new DomainException(04001, "至少需要选择一台源设备");
        if (sourceDeviceIds.Contains(targetDeviceId)) throw new DomainException(04001, "源设备不能包含目标设备");
        // 连接层启用了 EnableRetryOnFailure，NpgsqlRetryingExecutionStrategy 明确拒绝
        // 「用户自己发起的事务」：事务内第一条命令就会抛 InvalidOperationException（issue #230）。
        // 必须把整个事务交给 CreateExecutionStrategy() 返回的策略，作为一个可重试单元执行。
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            token => MergeAttemptAsync(userId, sourceDeviceIds, targetDeviceId, token),
            ct);
    }

    private async Task MergeAttemptAsync(
        Guid userId,
        IReadOnlyList<string> sourceDeviceIds,
        string targetDeviceId,
        CancellationToken ct)
    {
        // 策略可能整体重跑，清掉上一次尝试残留的跟踪实体。
        _db.ChangeTracker.Clear();

        var allIds = sourceDeviceIds.Concat(new[] { targetDeviceId }).Distinct().ToList();
        var devices = await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && allIds.Contains(d.DeviceId)).ToListAsync(ct);
        if (devices.Count != allIds.Count) throw new DomainException(04004, "部分设备不存在或不属于当前用户");

        await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;

        // events / summaries / sync_batches 的唯一索引都以 device_id 开头，而同一个业务键
        // 可能同时存在于多台设备上（重装 App 后用新 device_id 重传同一批记录；生产库实测
        // events 有 72,147 个键 / 127,583 行这类重复）。直接整体改写 device_id 会撞唯一索引，
        // 合并照样 500。这里按 device_id 逐台迁移：先删掉与目标设备已存在的重复行，再整体改写；
        // 前一台迁过去的行会成为后一台的「目标已有行」，因此每台只需与目标设备比较一次。
        // 保留哪一份：目标设备已有的行优先，其次是排序在前的源设备，其余删除。
        var orderedSources = sourceDeviceIds.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
        foreach (var sid in orderedSources)
        {
            await RemoveRowsCollidingWithTargetAsync(userId, sid, targetDeviceId, ct);
            await MoveDeviceRowsAsync(userId, sid, targetDeviceId, ct);
        }

        await MergeAppCatalogAsync(userId, orderedSources, targetDeviceId, ct);
        await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && sourceDeviceIds.Contains(d.DeviceId)).ExecuteDeleteAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    /// <summary>
    /// 删除源设备上与目标设备唯一键重复的行，使接下来的整体改写不会撞唯一索引。
    ///
    /// 覆盖 events / summaries / sync_batches 三张唯一索引含 device_id 的表。
    /// 其余移动端表里，sessions / location_points / timeline_blocks 没有唯一索引，不会冲突；
    /// mobile_usage_aggregates 虽然有同类唯一索引，但全库没有任何写入方、生产实测 0 行，
    /// 本次不纳入（既有的孤儿数据缺口，见 PR 说明）。
    /// </summary>
    private async Task RemoveRowsCollidingWithTargetAsync(
        Guid userId,
        string sourceDeviceId,
        string targetDeviceId,
        CancellationToken ct)
    {
        var events = _db.Set<MobileUsageEventEntity>();
        await events
            .Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId)
            .Where(e => events.Any(t => t.UserId == userId && t.DeviceId == targetDeviceId
                && t.PackageName == e.PackageName
                && t.EventType == e.EventType
                && t.EventTimestampUtc == e.EventTimestampUtc
                && t.ClassName == e.ClassName))
            .ExecuteDeleteAsync(ct);

        var summaries = _db.Set<MobileUsageSummaryEntity>();
        await summaries
            .Where(s => s.UserId == userId && s.DeviceId == sourceDeviceId)
            .Where(s => summaries.Any(t => t.UserId == userId && t.DeviceId == targetDeviceId
                && t.PackageName == s.PackageName
                && t.WindowStartUtc == s.WindowStartUtc
                && t.WindowEndUtc == s.WindowEndUtc
                && t.SourceKind == s.SourceKind))
            .ExecuteDeleteAsync(ct);

        var batches = _db.Set<MobileSyncBatchEntity>();
        await batches
            .Where(b => b.UserId == userId && b.DeviceId == sourceDeviceId)
            .Where(b => batches.Any(t => t.UserId == userId && t.DeviceId == targetDeviceId
                && t.BatchId == b.BatchId))
            .ExecuteDeleteAsync(ct);
    }

    private async Task MoveDeviceRowsAsync(
        Guid userId,
        string sourceDeviceId,
        string targetDeviceId,
        CancellationToken ct)
    {
        await _db.Set<MobileUsageEventEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
        await _db.Set<MobileUsageSessionEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
        await _db.Set<MobileUsageSummaryEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
        await _db.Set<MobileLocationPointEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
        await _db.Set<MobileSyncBatchEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
        await _db.Set<MobileTimelineBlockEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
    }

    /// <summary>
    /// 把源设备的 App 名称库条目并入目标设备（issue #231）。查询侧按 device_id 过滤
    /// <c>mobile_app_catalog</c>，不迁移就会让并入的历史记录解析不到 App 名称与分类。
    ///
    /// 唯一键是 (user_id, device_id, package_name)，目标设备同一包名只能保留一行，
    /// 而生产库里源设备与目标设备的包名大量重复（实测 144 个包名 / 686 行），
    /// 因此逐条改写 device_id 会撞唯一索引。规则：
    /// 每个包名保留「查询侧本来会选中的那一行」（见 <see cref="OrderByCatalogFreshness"/>），
    /// 落败候选直接删除。
    ///
    /// 关键点是不改写 <c>UpdatedAt</c>：它表示「这条 App 元数据是什么时候采集到的」。
    /// 如果合并时把它改成当前时间，目标行就会永远比后续源设备更新，
    /// 多次合并后新采集到的名称/分类会被静默丢弃。
    /// </summary>
    private async Task MergeAppCatalogAsync(
        Guid userId,
        IReadOnlyList<string> sourceDeviceIds,
        string targetDeviceId,
        CancellationToken ct)
    {
        var sourceRows = await _db.Set<MobileAppCatalogEntity>()
            .Where(c => c.UserId == userId && sourceDeviceIds.Contains(c.DeviceId))
            .ToListAsync(ct);
        if (sourceRows.Count == 0) return;

        var catalog = _db.Set<MobileAppCatalogEntity>();
        var targetRows = await catalog
            .Where(c => c.UserId == userId && c.DeviceId == targetDeviceId)
            .ToDictionaryAsync(c => c.PackageName, ct);

        foreach (var group in sourceRows.GroupBy(row => row.PackageName))
        {
            targetRows.TryGetValue(group.Key, out var targetRow);
            // 候选 = 该包名的全部源行（目标设备已有则并入目标行），用与查询侧一致的顺序
            // 挑出「合并前用户看到的那一行」，避免合并本身改变显示结果。
            var winner = OrderByCatalogFreshness(targetRow is null ? group : group.Append(targetRow)).First();

            if (targetRow is null)
            {
                winner.DeviceId = targetDeviceId;
                targetRows[winner.PackageName] = winner;
                foreach (var row in group)
                {
                    if (!ReferenceEquals(row, winner))
                        catalog.Remove(row);
                }
            }
            else
            {
                if (!ReferenceEquals(winner, targetRow))
                {
                    CopyCatalogMetadata(winner, targetRow);
                    // UpdatedAt / CreatedAt 是取舍链的前三级，必须跟着胜者一起搬过来：
                    // 只搬元数据会让目标行在这两级上仍是旧值，未参与合并的设备就可能
                    // 在合并后反超，用户看到的名称/分类随之改变。
                    targetRow.UpdatedAt = winner.UpdatedAt;
                    targetRow.CreatedAt = winner.CreatedAt;
                }
                // 目标设备的条目胜出，该包名的全部源候选都要删除。
                foreach (var row in group)
                    catalog.Remove(row);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 与查询侧 <c>MobileAppClassificationService.LoadLatestMetadataAsync</c> 相同的取舍顺序：
    /// UpdatedAt → LastUpdateTimeUtc → CreatedAt → DeviceId，保证「合并前能解析出的名称/分类」
    /// 在合并后仍然解析得到同一份。
    ///
    /// LastUpdateTimeUtc 可空：PostgreSQL 的 <c>ORDER BY ... DESC</c> 默认 NULLS FIRST，
    /// 这里用 HasValue 升序显式对齐（null 排在前面），否则 LINQ 默认把 null 排到最后，
    /// 会出现「合并前显示源设备的新名称、合并后变成目标设备的旧名称」。
    /// </summary>
    private static IEnumerable<MobileAppCatalogEntity> OrderByCatalogFreshness(
        IEnumerable<MobileAppCatalogEntity> rows)
        => rows
            .OrderByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.LastUpdateTimeUtc.HasValue)
            .ThenByDescending(row => row.LastUpdateTimeUtc)
            .ThenByDescending(row => row.CreatedAt)
            .ThenBy(row => row.DeviceId, StringComparer.Ordinal);

    private static void CopyCatalogMetadata(MobileAppCatalogEntity from, MobileAppCatalogEntity to)
    {
        to.DisplayName = from.DisplayName;
        to.VersionName = from.VersionName;
        to.VersionCode = from.VersionCode;
        to.IsSystemApp = from.IsSystemApp;
        to.Category = from.Category;
        to.InstallerPackage = from.InstallerPackage;
        to.FirstInstallTimeUtc = from.FirstInstallTimeUtc;
        to.LastUpdateTimeUtc = from.LastUpdateTimeUtc;
        to.RawJson = from.RawJson;
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
        // 与 MergeAsync 同理：显式事务必须由执行策略执行（issue #230）。
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(token => DeleteAttemptAsync(userId, deviceId, token), ct);
    }

    private async Task DeleteAttemptAsync(Guid userId, string deviceId, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();

        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");
        var syncing = await _db.Set<MobileSyncBatchEntity>().AnyAsync(b => b.UserId == userId && b.DeviceId == deviceId && b.Status == "syncing", ct);
        if (syncing) throw new DomainException(04002, "设备正在同步，禁止删除");

        await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
        await _db.Set<MobileUsageEventEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await _db.Set<MobileUsageSessionEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await _db.Set<MobileUsageSummaryEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await _db.Set<MobileLocationPointEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await _db.Set<MobileSyncBatchEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await _db.Set<MobileTimelineBlockEntity>().Where(e => e.UserId == userId && e.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        // 设备的 App 名称库条目必须一起删除，否则会留下指向已删除 device_id 的孤儿行（issue #231）。
        await _db.Set<MobileAppCatalogEntity>().Where(c => c.UserId == userId && c.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && d.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
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
