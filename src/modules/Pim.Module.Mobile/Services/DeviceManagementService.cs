using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Endpoints;
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

        // 逐表范围：与删除预览同源的清单，统计的是**源设备**一侧的行数
        // （这些行会被改写/相加/删除）。目标设备一侧只有三张派生表会被清空重算，
        // 不在这个清单里重复计数。
        var sourceIds = sourceDeviceIds.Distinct().ToList();
        var attributable = await ResolveAttributableDeviceIdsAsync(userId, sourceIds, ct);
        var tables = new List<DeviceTableCountDto>();
        foreach (var table in BuildDeviceScopedPlan(userId, sourceIds, attributable))
        {
            tables.Add(new DeviceTableCountDto(table.Table, await table.Count(ct)));
        }

        return new DeviceMergePreviewDto(preview, total, tables);
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

        // 心跳 / 端点通道（工单 02 C1）：这些表此前完全不在范围内，于是旧设备合并后
        // 仍然从心跳与端点列表里冒出来。daemon_heartbeats 没有 user_id，源与**目标**
        // 两侧都要先判归属：目标 device_id 被别的用户占用时，这个 id 下的心跳行同样归属不明。
        var attributableSources = await ResolveAttributableDeviceIdsAsync(userId, orderedSources, ct);
        var targetAttributable = (await ResolveAttributableDeviceIdsAsync(userId, [targetDeviceId], ct)).Count == 1;
        await MergeDaemonHeartbeatsAsync(attributableSources, targetDeviceId, targetAttributable, ct);
        await MergeEndpointStatusesAsync(userId, orderedSources, targetDeviceId, ct);
        await MoveEndpointNotificationActionsAsync(userId, orderedSources, targetDeviceId, ct);

        await _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && sourceDeviceIds.Contains(d.DeviceId)).ExecuteDeleteAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    /// <summary>
    /// 删除源设备上与目标设备唯一键重复的行，使接下来的整体改写不会撞唯一索引。
    ///
    /// 覆盖 events / summaries / sync_batches / forensic / location_points 五张
    /// 唯一索引含 device_id 的表。其余会被改写的移动端表不会冲突：sessions 没有唯一索引；
    /// dropped_reason_daily 走"相加"合并、app_catalog 走"保留最新一份"，
    /// 两者都在各自的方法里做冲突消解。派生表（timeline_blocks / usage_aggregates /
    /// analytics_materializations）虽然有同类唯一索引，但合并策略是**删除而不是改写**，
    /// 因此不在这里处理。
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

        // 取证事件（阶段一 REQ-1~REQ-4）：幂等键是 (user, device, clientItemKey)，
        // 两台设备可能带上同一个键（重装后重传），直接改写会撞唯一索引。
        // 保留目标设备已有的那一份，与上面三张表同一条规则（AC-6.3 要求不产生孤儿记录）。
        var forensic = _db.Set<MobileForensicEventEntity>();
        await forensic
            .Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId)
            .Where(e => forensic.Any(t => t.UserId == userId && t.DeviceId == targetDeviceId
                && t.ClientItemKey == e.ClientItemKey))
            .ExecuteDeleteAsync(ct);

        // 定位点的天然键是 (user, device, recorded_at_utc, latitude, longitude)
        // （MobileEntityConfigurations 的 IX_..._user_id_device_id_recorded_at_utc_latitude_longitude，
        // #246 为去重客户端重试/批量补传而建）。重装 App 后用新 device_id 补传同一段时间的同一批点，
        // 两台设备就会持有相同天然键；不先消解，整体改写会撞唯一索引让合并在生产上 500。
        // 与其它表同一条规则：保留目标设备已有的那一份。
        var locations = _db.Set<MobileLocationPointEntity>();
        await locations
            .Where(p => p.UserId == userId && p.DeviceId == sourceDeviceId)
            .Where(p => locations.Any(t => t.UserId == userId && t.DeviceId == targetDeviceId
                && t.RecordedAtUtc == p.RecordedAtUtc
                && t.Latitude == p.Latitude
                && t.Longitude == p.Longitude))
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
        // 取证事件（REQ-1~REQ-4）与丢弃原因统计（REQ-9）随设备一起并入：
        // 它们是"这台设备当时活着/死了"的原始证据，丢掉就等于把合并前的死因取证抹掉。
        // 事件按上面的冲突规则去重后整体改写；统计的天然键是 (user, device, 本地日, 原因)，
        // 两台设备同一天同一原因的行在语义上是"两台设备各自丢弃的条数"，因此**相加**而不是覆盖。
        await _db.Set<MobileForensicEventEntity>().Where(e => e.UserId == userId && e.DeviceId == sourceDeviceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetDeviceId), ct);
        await MergeDroppedReasonDailyAsync(userId, sourceDeviceId, targetDeviceId, ct);
        // 派生数据（块 / 聚合 / 物化覆盖）在合并后一律丢弃，等下一次上传重新物化（#247）：
        // 1. 块与聚合分别有 (user, device, ...) 唯一索引，两台设备同桶/同分类的行直接改写
        //    device_id 会撞唯一索引，让合并在生产上 500；
        // 2. 合并后源与目标的数据已经混在一起，旧的派生行（含设备维度）不再可信；
        // 3. 覆盖记录必须与派生行同时失效，否则读路径会把"已被删掉的派生数据"当成新鲜缓存。
        //
        // 这三张表**不是**"没有写入方"的僵尸表：mobile_usage_aggregates / mobile_timeline_blocks
        // 由 MobileAnalyticsMaterializationService.MaterializeAsync 写入（经
        // MobileUsageIngestService 的上传链路触发），mobile_analytics_materializations 记录覆盖窗口，
        // 生产有数据且随上传增长。这里删得起，是因为它们都是**可重算**的派生数据：
        // 读路径（MobileUsageAggregationService/MobileTimelineBlockService）在覆盖记录缺失时
        // 会回退到在线计算 —— 用 mobile_usage_sessions + fallback 汇总
        // （mobile_usage_events 则是 mobile_usage_sessions 的重建来源），因此数字不会变空。
        // 注意"重算"发生在**读路径**上，不依赖下一次上传；详见 PR 里的 AC-2 口径说明。
        foreach (var device in new[] { sourceDeviceId, targetDeviceId })
        {
            await _db.Set<MobileTimelineBlockEntity>()
                .Where(e => e.UserId == userId && e.DeviceId == device).ExecuteDeleteAsync(ct);
            await _db.Set<MobileUsageAggregateEntity>()
                .Where(e => e.UserId == userId && e.DeviceId == device).ExecuteDeleteAsync(ct);
            await _db.Set<MobileAnalyticsMaterializationEntity>()
                .Where(e => e.UserId == userId && e.DeviceId == device).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// 把源设备的「丢弃原因按天统计」并入目标设备（AC-9.2 / AC-6.3）。
    ///
    /// 天然键是 (user, device, 本地日, 原因)。两台设备同一天同一原因的行在语义上是
    /// "两台设备各自丢弃的条数"，因此合并时**相加**；目标设备没有的行直接改写 device_id。
    /// 若不做这一步，源设备的统计行就会变成指向已删除 device_id 的孤儿记录（AC-6.3 反面）。
    /// </summary>
    private async Task MergeDroppedReasonDailyAsync(
        Guid userId,
        string sourceDeviceId,
        string targetDeviceId,
        CancellationToken ct)
    {
        var source = _db.Set<MobileDroppedReasonDailyEntity>();
        var target = _db.Set<MobileDroppedReasonDailyEntity>();

        // 与目标同键的行：条数相加（"两台设备各自丢弃的条数"）。
        // 全部用集合操作完成，不加载被跟踪实体——否则 ExecuteUpdate 改完库之后，
        // 变更跟踪器里仍留着旧 DeviceId 的实例，同一作用域内后续读取会拿到陈旧值。
        var colliding = await source
            .Where(row => row.UserId == userId && row.DeviceId == sourceDeviceId)
            .Where(row => target.Any(other => other.UserId == userId
                && other.DeviceId == targetDeviceId
                && other.LocalDate == row.LocalDate
                && other.Reason == row.Reason))
            .Select(row => new { row.LocalDate, row.Reason, row.Count })
            .ToListAsync(ct);

        foreach (var row in colliding)
        {
            var date = row.LocalDate;
            var reason = row.Reason;
            var count = row.Count;
            await target
                .Where(other => other.UserId == userId
                    && other.DeviceId == targetDeviceId
                    && other.LocalDate == date
                    && other.Reason == reason)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(other => other.Count, other => other.Count + count)
                        .SetProperty(other => other.ReceivedAtUtc, _timeProvider.GetUtcNow()),
                    ct);
        }

        // 目标没有的 (本地日, 原因) 直接整体改写 device_id。
        await source
            .Where(row => row.UserId == userId && row.DeviceId == sourceDeviceId)
            .Where(row => !target.Any(other => other.UserId == userId
                && other.DeviceId == targetDeviceId
                && other.LocalDate == row.LocalDate
                && other.Reason == row.Reason))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.DeviceId, targetDeviceId),
                ct);

        // 处理完冲突后，仍挂在源设备下的行就是已经并入目标的那批，删除以免留下孤儿记录（AC-6.3）。
        await source
            .Where(row => row.UserId == userId && row.DeviceId == sourceDeviceId)
            .ExecuteDeleteAsync(ct);
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
    /// 把源设备的守护进程心跳并入目标设备（工单 02 C1）。
    ///
    /// <para>
    /// <c>daemon_heartbeats</c> **没有 user_id 列**，唯一索引是 <c>(device_id, daemon_kind)</c>
    /// （Stage0 迁移起就是 unique，不是"非唯一"）。当前用户的手机设备与它共用同一套 device_id
    /// 命名空间：<see cref="MobileQualityService"/> 正是用 <c>mobile_devices</c> 里的 device_id
    /// 去查 <c>DaemonKind == "android"</c> 的心跳。因此这里只处理 android 心跳，
    /// 且只处理 <paramref name="attributableSourceDeviceIds"/>（该字符串没有被别的用户占用）。
    /// </para>
    ///
    /// <para>
    /// 冲突消解：目标设备可能已经有同一 <c>daemon_kind</c> 的心跳行，直接改写 device_id 会撞唯一索引。
    /// 语义上心跳是"这台设备现在是什么状态"的快照，因此保留 <c>ReceivedAt</c> 更新的那一条
    /// （字段原样保留，不做字段级合并），另一条删除。同刻时保留目标设备已有的那条，保证操作幂等。
    /// </para>
    ///
    /// <para>
    /// <paramref name="targetAttributable"/> = 目标 device_id 也没有被别的用户占用。
    /// 目标归属不明时必须**两头都不写**：那个 id 下的心跳行既不能删（可能是别人的活动心跳），
    /// 也不能把我们的源行改挂上去（别的用户会因此看到我们的设备状态）。此时本用户源设备的
    /// 心跳行是我们自己的数据且设备即将被删除，直接清掉，避免留下指向已删除设备的孤儿行。
    /// </para>
    /// </summary>
    private async Task MergeDaemonHeartbeatsAsync(
        IReadOnlyList<string> attributableSourceDeviceIds,
        string targetDeviceId,
        bool targetAttributable,
        CancellationToken ct)
    {
        if (attributableSourceDeviceIds.Count == 0) return;

        var sourceRows = await _db.Set<DaemonHeartbeatEntity>()
            .Where(h => h.DaemonKind == MobileDaemonKind.Android
                && attributableSourceDeviceIds.Contains(h.DeviceId))
            .OrderBy(h => h.DeviceId)
            .ToListAsync(ct);
        if (sourceRows.Count == 0) return;

        if (!targetAttributable)
        {
            _db.RemoveRange(sourceRows);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var targetRow = await _db.Set<DaemonHeartbeatEntity>()
            .SingleOrDefaultAsync(h => h.DaemonKind == MobileDaemonKind.Android && h.DeviceId == targetDeviceId, ct);

        var winner = sourceRows
            .OrderByDescending(h => h.ReceivedAt)
            .ThenBy(h => h.DeviceId, StringComparer.Ordinal)
            .First();

        if (targetRow is not null && targetRow.ReceivedAt >= winner.ReceivedAt)
        {
            // 目标设备的心跳更新（或同刻）：源行全部删除，避免留下指向已删除设备的心跳。
            _db.RemoveRange(sourceRows);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // 源设备的心跳更新：先把目标行去掉腾出唯一键，再把胜出的源行改挂目标 device_id。
        // 分两次 SaveChanges 是因为两个受影响的实体在同一张表上撞同一个唯一键，
        // 同批执行的 DELETE/UPDATE 顺序不由我们决定，不能赌。
        if (targetRow is not null)
        {
            _db.Remove(targetRow);
            await _db.SaveChangesAsync(ct);
        }

        _db.RemoveRange(sourceRows.Where(row => !ReferenceEquals(row, winner)));
        winner.DeviceId = targetDeviceId;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 把源设备的端点状态并入目标设备（工单 02 C1）。
    ///
    /// <para>
    /// <c>endpoint_statuses</c> 的唯一键是 <c>(user_id, device_id)</c>，直接改写 device_id
    /// 会在目标设备已有同一端点行时撞唯一索引（同一个教训在同一文件里已经有过注释）。
    /// 这里逐台消解：状态快照按 <c>LastHeartbeatAt</c> 取更新的那一份
    /// （与心跳同一条语义："端点现在怎么样"），<c>OnlineOnlyBlockedCount</c> 是**累计量**，
    /// 取两边之和（两台端点各自被拦截的次数在合并后应当是总和）；<c>CreatedAt</c> 取更早的
    /// （这个端点从更早那一刻起就存在）。
    /// </para>
    /// </summary>
    private async Task MergeEndpointStatusesAsync(
        Guid userId,
        IReadOnlyList<string> sourceDeviceIds,
        string targetDeviceId,
        CancellationToken ct)
    {
        if (sourceDeviceIds.Count == 0) return;

        var sourceRows = await _db.Set<EndpointStatusEntity>()
            .Where(s => s.UserId == userId && sourceDeviceIds.Contains(s.DeviceId))
            .OrderBy(s => s.DeviceId)
            .ToListAsync(ct);
        if (sourceRows.Count == 0) return;

        var targetRow = await _db.Set<EndpointStatusEntity>()
            .SingleOrDefaultAsync(s => s.UserId == userId && s.DeviceId == targetDeviceId, ct);

        foreach (var source in sourceRows)
        {
            if (targetRow is null)
            {
                // 目标设备还没有端点行：整行改挂过去，状态原样保留（不是凭空消失）。
                source.DeviceId = targetDeviceId;
                targetRow = source;
                continue;
            }

            // LastHeartbeatAt 可空（EndpointStatusService.GetOrCreateStateAsync 会建出
            // "还没有收到过心跳"的端点行），而 C# 的可空比较在有 null 时一律返回 false。
            // 不显式处理的话，"目标从未心跳、源有有效心跳"会被判成目标更新，
            // 源行的 Platform/AppVersion/UploadStatus/CollectionCacheCount 与有效心跳时刻全被丢掉。
            var sourceIsFresher = source.LastHeartbeatAt is not null
                && (targetRow.LastHeartbeatAt is null || source.LastHeartbeatAt > targetRow.LastHeartbeatAt);

            if (sourceIsFresher)
            {
                targetRow.Platform = source.Platform;
                targetRow.AppVersion = source.AppVersion;
                targetRow.UploadStatus = source.UploadStatus;
                targetRow.CollectionCacheCount = source.CollectionCacheCount;
                targetRow.LastHeartbeatAt = source.LastHeartbeatAt;
            }

            targetRow.OnlineOnlyBlockedCount += source.OnlineOnlyBlockedCount;
            if (source.CreatedAt < targetRow.CreatedAt) targetRow.CreatedAt = source.CreatedAt;
            if (source.UpdatedAt > targetRow.UpdatedAt) targetRow.UpdatedAt = source.UpdatedAt;
            _db.Remove(source);
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 端点通知动作是"这台端点上发生过什么"的审计轨迹，随设备一起并入目标
    /// （该表没有 <c>(user_id, device_id)</c> 唯一索引 —— PimDbContext 给的是
    /// user_id / device_id / created_at / confirmation_id 四个单列索引，整体改写不会冲突）。
    /// </summary>
    private async Task MoveEndpointNotificationActionsAsync(
        Guid userId,
        IReadOnlyList<string> sourceDeviceIds,
        string targetDeviceId,
        CancellationToken ct)
    {
        if (sourceDeviceIds.Count == 0) return;

        await _db.Set<EndpointNotificationActionEntity>()
            .Where(a => a.UserId == userId && sourceDeviceIds.Contains(a.DeviceId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.DeviceId, targetDeviceId), ct);
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
        // 与 DeleteAttemptAsync 共用同一份范围清单，只是把"删"换成"数"（工单 02 C3）：
        // 因此预览的每张表计数与实际删除数不是"两边各写一遍"，而是同一段代码的两种执行方式。
        var tables = new List<DeviceTableCountDto>();
        var attributable = await ResolveAttributableDeviceIdsAsync(userId, [deviceId], ct);
        foreach (var table in BuildDeviceScopedPlan(userId, [deviceId], attributable))
        {
            tables.Add(new DeviceTableCountDto(table.Table, await table.Count(ct)));
        }

        return new DeviceDeletePreviewDto(
            device.DeviceId, device.DisplayName, stats.SessionCount, stats.EventCount, stats.LocationCount, stats.SummaryCount, tables);
    }

    public async Task<DeviceDeleteResultDto> DeleteAsync(string deviceId, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        // 与 MergeAsync 同理：显式事务必须由执行策略执行（issue #230）。
        var strategy = _db.Database.CreateExecutionStrategy();
        var tables = await strategy.ExecuteAsync(token => DeleteAttemptAsync(userId, deviceId, token), ct);
        return new DeviceDeleteResultDto(deviceId, tables, tables.Sum(row => row.RowCount));
    }

    private async Task<IReadOnlyList<DeviceTableCountDto>> DeleteAttemptAsync(Guid userId, string deviceId, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();

        var device = await _db.Set<MobileDeviceEntity>().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
            ?? throw new DomainException(04004, "设备不存在");

        await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;

        // 只拦截"最近还在处理"的批次：中断的上传会留下 pending 行（#243），
        // 若把它当成永久"正在同步"，设备将永远无法删除。
        // 检查放在事务内、删除之前，尽量收窄"检查完就有新上传开始"的窗口；
        // 时间比较放在内存里做：SQLite（部分测试用）不支持 DateTimeOffset 的服务器端比较。
        var activeSince = _timeProvider.GetUtcNow() - MobileSyncBatchStatus.ActiveWindow;
        var activeBatchCreatedAt = await _db.Set<MobileSyncBatchEntity>()
            .Where(b => b.UserId == userId
                && b.DeviceId == deviceId
                && (b.Status == MobileSyncBatchStatus.Pending
                    || b.Status == "processing"
                    || b.Status == "syncing"))
            .Select(b => b.CreatedAt)
            .ToListAsync(ct);
        if (activeBatchCreatedAt.Any(createdAt => createdAt >= activeSince))
            throw new DomainException(04002, "设备正在同步，禁止删除");

        // daemon_heartbeats 没有 user_id，"按 device_id 删"会把别的用户同名字符串设备的心跳一起删掉；
        // 只有该字符串确实独占时才把它纳入本次删除范围（见 ResolveAttributableDeviceIdsAsync）。
        var attributable = await ResolveAttributableDeviceIdsAsync(userId, [deviceId], ct);
        var deleted = new List<DeviceTableCountDto>();
        foreach (var table in BuildDeviceScopedPlan(userId, [deviceId], attributable))
        {
            deleted.Add(new DeviceTableCountDto(table.Table, await table.Delete(ct)));
        }

        if (tx is not null) await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>
    /// 统一的设备作用域表清单：删除预览与实际删除都由这一份清单驱动，因此两者的
    /// 「表集合」与「每张表的过滤条件」天然一致（工单 02 C3）。数组顺序即删除顺序
    /// （先子表、最后 <c>mobile_devices</c>），预览只是把 <c>Delete</c> 换成 <c>Count</c>。
    /// </summary>
    /// <param name="attributableDeviceIds">
    /// 可以安全归属到该用户的 device_id（见 <see cref="ResolveAttributableDeviceIdsAsync"/>）。
    /// 只有它决定 <c>daemon_heartbeats</c> 是否纳入范围，且是**逐台**判断 ——
    /// 多台源设备里只要有一台的字符串被别的用户占用，那一台的心跳就不动，其余照常处理。
    /// </param>
    private IReadOnlyList<DeviceScopedTablePlan> BuildDeviceScopedPlan(
        Guid userId,
        IReadOnlyList<string> deviceIds,
        IReadOnlyCollection<string> attributableDeviceIds)
    {
        var attributableHeartbeatDeviceIds = deviceIds.Where(attributableDeviceIds.Contains).ToList();
        var events = _db.Set<MobileUsageEventEntity>();
        var sessions = _db.Set<MobileUsageSessionEntity>();
        var summaries = _db.Set<MobileUsageSummaryEntity>();
        var locations = _db.Set<MobileLocationPointEntity>();
        var batches = _db.Set<MobileSyncBatchEntity>();
        var forensic = _db.Set<MobileForensicEventEntity>();
        var droppedReasons = _db.Set<MobileDroppedReasonDailyEntity>();
        var catalog = _db.Set<MobileAppCatalogEntity>();
        var blocks = _db.Set<MobileTimelineBlockEntity>();
        var aggregates = _db.Set<MobileUsageAggregateEntity>();
        var materializations = _db.Set<MobileAnalyticsMaterializationEntity>();
        var endpointStatuses = _db.Set<EndpointStatusEntity>();
        var notificationActions = _db.Set<EndpointNotificationActionEntity>();
        var heartbeats = _db.Set<DaemonHeartbeatEntity>();

        return
        [
            new(DeviceScopedTables.UsageEvents,
                ct => events.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => events.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.UsageSessions,
                ct => sessions.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => sessions.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.UsageSummaries,
                ct => summaries.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => summaries.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.LocationPoints,
                ct => locations.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => locations.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.SyncBatches,
                ct => batches.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => batches.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.ForensicEvents,
                ct => forensic.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => forensic.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.DroppedReasonDaily,
                ct => droppedReasons.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => droppedReasons.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.AppCatalog,
                ct => catalog.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => catalog.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.TimelineBlocks,
                ct => blocks.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => blocks.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.UsageAggregates,
                ct => aggregates.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => aggregates.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.AnalyticsMaterializations,
                ct => materializations.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => materializations.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            // 端点状态与通知动作按 (user_id, device_id) 归属，用户维度已经把它们隔离住，无需额外判断。
            // 注意这里**不**按 platform 再过滤：同一用户在 endpoint_statuses 与 mobile_devices 里
            // 用的是同一个 device_id 字符串，那就是同一个"端点身份"；按 platform 过滤反而会把
            // 平台被记成非 android 的同名端点行留成孤儿（正是工单要消灭的"旧设备冒出来"）。
            new(DeviceScopedTables.EndpointStatuses,
                ct => endpointStatuses.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => endpointStatuses.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.EndpointNotificationActions,
                ct => notificationActions.CountAsync(e => e.UserId == userId && deviceIds.Contains(e.DeviceId), ct),
                ct => notificationActions.Where(e => e.UserId == userId && deviceIds.Contains(e.DeviceId)).ExecuteDeleteAsync(ct)),
            // daemon_heartbeats 是唯一没有 user_id 的设备作用域表：只能在该 device_id 字符串
            // 未被别的用户占用时才动它，否则会跨用户误删/误搬。计数返回 0 表示"本次不会动它"，
            // 与预览和实际删除都一致。
            new(DeviceScopedTables.DaemonHeartbeats,
                ct => heartbeats.CountAsync(
                    h => h.DaemonKind == MobileDaemonKind.Android && attributableHeartbeatDeviceIds.Contains(h.DeviceId), ct),
                ct => heartbeats.Where(
                        h => h.DaemonKind == MobileDaemonKind.Android && attributableHeartbeatDeviceIds.Contains(h.DeviceId))
                    .ExecuteDeleteAsync(ct)),
            new(DeviceScopedTables.Devices,
                ct => _db.Set<MobileDeviceEntity>().CountAsync(d => d.UserId == userId && deviceIds.Contains(d.DeviceId), ct),
                ct => _db.Set<MobileDeviceEntity>().Where(d => d.UserId == userId && deviceIds.Contains(d.DeviceId)).ExecuteDeleteAsync(ct)),
        ];
    }

    /// <summary>
    /// 筛出「可以安全归属到该用户」的 device_id：该字符串没有被**别的用户**在
    /// <c>mobile_devices</c> 里占用。
    ///
    /// 为什么必须这么做：<c>daemon_heartbeats</c> 没有 user_id 列（<see cref="DaemonHeartbeatEntity"/>），
    /// 而 <c>mobile_devices</c> 只保证 device_id「按用户唯一」
    /// （<c>(user_id, device_id)</c> 唯一索引，见 MobileEntityConfigurations）。
    /// 两个用户完全可能持有同一个字符串 device_id；此时那台设备的心跳行究竟属于谁无法判定，
    /// 只能整体不动 —— 否则合并会把别的用户的心跳改到本用户的目标设备上（跨用户数据污染），
    /// 而"旧 device_id 是否为 0 行"这类断言永远抓不到它（反而会变绿）。
    ///
    /// <c>IgnoreQueryFilters()</c> 是必须的：<c>PimDbContext</c> 给所有 <c>IUserOwnedEntity</c>
    /// 追加了 <c>CurrentUserId == null || e.UserId == CurrentUserId</c> 的全局过滤器，
    /// 在真实请求上下文里它会把"别的用户的行"直接滤掉，于是这里永远查不到冲突、判断恒为「独占」。
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAttributableDeviceIdsAsync(
        Guid userId,
        IReadOnlyList<string> deviceIds,
        CancellationToken ct)
    {
        var distinct = deviceIds.Distinct().ToList();
        if (distinct.Count == 0) return distinct;

        var sharedWithOtherUsers = await _db.Set<MobileDeviceEntity>()
            .IgnoreQueryFilters()
            .Where(d => d.UserId != userId && distinct.Contains(d.DeviceId))
            .Select(d => d.DeviceId)
            .Distinct()
            .ToListAsync(ct);

        return distinct.Where(id => !sharedWithOtherUsers.Contains(id)).ToList();
    }

    private sealed record DeviceScopedTablePlan(
        string Table,
        Func<CancellationToken, Task<int>> Count,
        Func<CancellationToken, Task<int>> Delete);

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
/// <summary>
/// 合并预览。<paramref name="Tables"/> 是本次合并按统一设备作用域清单统计到的**源设备**行数
/// （与删除预览同源，保证"预览即范围"）；派生表在合并时对源与目标两侧都会清空，
/// 因此目标设备那一侧的派生行不在这里计数（见实现说明）。
/// </summary>
public sealed record DeviceMergePreviewDto(
    IReadOnlyList<DeviceMergeItemDto> Items,
    long Total,
    IReadOnlyList<DeviceTableCountDto> Tables);

/// <summary>
/// 删除预览。<paramref name="Tables"/> 覆盖删除会触及的**全部**表，且与 DELETE 的实现共用
/// 同一份清单，因此"预览数字 == 实际删除数"是结构性的，不是靠两边各写一遍维持的。
/// </summary>
public sealed record DeviceDeletePreviewDto(
    string DeviceId,
    string DisplayName,
    int SessionCount,
    int EventCount,
    int LocationCount,
    int SummaryCount,
    IReadOnlyList<DeviceTableCountDto> Tables);

/// <summary>
/// DELETE 的实际删除结果（逐表计数）。观测方式就是本次调用的返回值本身：
/// 它由真正执行删除的那一次 <c>ExecuteDelete</c> 的受影响行数汇总而来，
/// 不接受调用方传值，也不事后重算。
/// </summary>
public sealed record DeviceDeleteResultDto(
    string DeviceId,
    IReadOnlyList<DeviceTableCountDto> Tables,
    int TotalRows);

/// <summary>逐表行数（表名用数据库表名，便于直接对库核对）。</summary>
public sealed record DeviceTableCountDto(string Table, int RowCount);
public sealed record DeviceExportDto(string FileName, string Json);

/// <summary>
/// 设备作用域表的数据库表名（合并 / 删除 / 两个预览共用同一份命名，避免口径漂移）。
///
/// <para>
/// 这里收录的是**手机设备命名空间**（<c>mobile_devices.device_id</c>）下的全部表，
/// 外加两张按 <c>(user_id, device_id)</c> 归属的端点表，以及 <c>daemon_heartbeats</c>
/// （无 <c>user_id</c>，处理前必须逐台判归属）。
/// </para>
///
/// <para>
/// 明确**不在**本清单的两类表（原因见 PR 说明，均已核实）：
/// 1) PC 域 <c>pc_*</c>（<c>pc_tracker_health</c> / <c>pc_tracker_events</c> / <c>pc_aw_events</c> /
/// <c>pc_aw_buckets</c> / <c>pc_keystats_daily</c> / <c>pc_keystats_samples</c> /
/// <c>pc_browser_site_daily</c> / <c>pc_browser_site_tick</c> / <c>pc_activity_classifications</c>）：
/// 这些表**没有任何用户归属列**，PC 的 device_id 由 PC 守护进程上报、与手机设备不同源；
/// 按 device_id 处理等于跨用户改写。
/// 2) <c>app_usage</c>：实体与代码已随 <c>Pim.Module.Stats</c> 一并删除（三个客户端仓的**源码**零调用；
/// 发布产物只 publish <c>Pim.Api.csproj</c>，不含该 DLL，因此路由只在引用它的测试宿主里被实例化过）；
/// **代码不再读写**这张表。存量部署的库里可能仍保留该表（刻意不写删除迁移，避免误删运维数据），
/// 这不影响运行，如需清理由运维自行处理。
/// </para>
/// </summary>
internal static class DeviceScopedTables{
    public const string Devices = "mobile_devices";
    public const string UsageEvents = "mobile_usage_events";
    public const string UsageSessions = "mobile_usage_sessions";
    public const string UsageSummaries = "mobile_usage_summaries";
    public const string LocationPoints = "mobile_location_points";
    public const string SyncBatches = "mobile_sync_batches";
    public const string ForensicEvents = "mobile_forensic_events";
    public const string DroppedReasonDaily = "mobile_dropped_reason_daily";
    public const string AppCatalog = "mobile_app_catalog";
    public const string TimelineBlocks = "mobile_timeline_blocks";
    public const string UsageAggregates = "mobile_usage_aggregates";
    public const string AnalyticsMaterializations = "mobile_analytics_materializations";
    public const string EndpointStatuses = "endpoint_statuses";
    public const string EndpointNotificationActions = "endpoint_notification_actions";
    public const string DaemonHeartbeats = "daemon_heartbeats";
}

/// <summary>
/// <c>daemon_heartbeats.daemon_kind</c> 的取值。手机设备上报的心跳用 <c>android</c>，
/// 与 <see cref="MobileQualityService"/> 查设备存活时用的过滤条件同一口径。
/// </summary>
internal static class MobileDaemonKind
{
    public const string Android = "android";
}
