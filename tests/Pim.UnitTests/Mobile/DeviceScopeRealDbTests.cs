using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Endpoints;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 工单 02 的真库验收（AC-1 / AC-2 / AC-3）。无 PostgreSQL（或未设置 <c>PIM_TEST_CONN</c>）时
/// 显式 <c>Skip</c>，报告里显示 Skipped。
///
/// <para>
/// 为什么必须走真库：
/// </para>
/// <list type="number">
/// <item><description>
/// 合并/删除只走 <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>，InMemory 无法翻译；
/// </description></item>
/// <item><description>
/// 「整体改写会不会撞唯一索引」只有在真实唯一索引下才有意义；
/// </description></item>
/// <item><description>
/// AC-2 的数字来自按 <c>DateTimeOffset</c> 排序/过滤的读路径，SQLite 不支持。
/// </description></item>
/// </list>
///
/// <para>
/// 每个用例在 <c>PIM_TEST_CONN</c> 指向的服务器上建一个一次性库（<c>EnsureCreated</c> 建出
/// 生产同款 schema，含唯一索引），结束后 <c>DROP DATABASE ... WITH (FORCE)</c>。
/// 不接触任何已有库；<c>PIM_TEST_CONN</c> 的账号需要有 CREATEDB 权限。
/// </para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class DeviceScopeRealDbTests
{
    private const string TargetDeviceId = "android-realdb-target";
    private const string SourceDeviceId = "android-realdb-source";
    private static readonly Guid OtherUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T02:00:00Z");

    /// <summary>查询窗口取一个本地整日（业务日 04:00 起），与物化窗口的对齐口径一致。</summary>
    private static readonly DateTimeOffset WindowStart = Pim.Core.Common.BusinessDay.GetStartUtc(new DateOnly(2026, 9, 10));
    private static readonly DateTimeOffset WindowEnd = Pim.Core.Common.BusinessDay.GetEndUtc(new DateOnly(2026, 9, 10));

    /// <summary>物化快照里的假数字：一眼可辨，且低于小时桶 3600s 的封顶值。</summary>
    private const long StaleSnapshotSeconds = 3456;
    private const long TargetSessionSeconds = 600;
    private const long SourceSessionSeconds = 900;

    // ---------------------------------------------------------------- AC-2：合并后数字不缺失

    /// <summary>
    /// AC-2 的可判定形式：合并前用**用户级**（不传 deviceId）查询得到的汇总数字，
    /// 与合并后用**新设备**查询同一窗口得到的数字必须完全一致 —— 这就是
    /// "用户在新设备下能看到的手机使用数据与合并前一致"。
    ///
    /// <para>
    /// 为什么这样写才成立：device_id 在移动端原始表里只是归属字段，合并只改写归属，
    /// 因此「全部设备」与「新设备」在合并后覆盖同一批原始行；两条查询都在线计算
    /// （用户级查询因设备数 &gt; 1 不读物化表；合并把两侧物化数据都删了，新设备这条也拿不到覆盖记录）。
    /// 反过来，直接比对 <c>mobile_usage_aggregates</c> 的行必然对不上（合并就把它们删了），
    /// 所以断言不看派生表本身，只看用户能读到的数字。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task MergeAsync_KeepsTheNumbersTheUserCouldSeeBeforeTheMerge()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var db = scope.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceId, Now.AddDays(-60));
        SeedSession(db, MobileTestHelpers.UserId, TargetDeviceId, "com.target.app", WindowStart.AddHours(1), TargetSessionSeconds);
        SeedSession(db, MobileTestHelpers.UserId, SourceDeviceId, "com.source.app", WindowStart.AddHours(3), SourceSessionSeconds);
        // 故意给目标设备造一份"看起来新鲜"的物化数据：合并前读路径能命中它，
        // 合并后必须失效并回退在线计算 —— 否则数字会停在合并前的旧快照上。
        SeedMaterializedCoverageAndAggregates(db, TargetDeviceId, StaleSnapshotSeconds);
        await db.SaveChangesAsync();

        var aggregation = CreateAggregationService(db);
        var before = await aggregation.GetOverviewAsync(
            new MobileAnalyticsQueryRequest(WindowStart, WindowEnd), CancellationToken.None);
        Assert.Equal(TargetSessionSeconds + SourceSessionSeconds, before.TotalForegroundSeconds);
        Assert.Equal(2, before.AppCount);

        var service = new DeviceManagementService(
            db, MobileTestHelpers.CurrentUser(MobileTestHelpers.UserId), MobileTestHelpers.Time(Now));
        await service.MergeAsync([SourceDeviceId], TargetDeviceId, CancellationToken.None);

        var after = await aggregation.GetOverviewAsync(
            new MobileAnalyticsQueryRequest(WindowStart, WindowEnd, DeviceId: TargetDeviceId), CancellationToken.None);

        Assert.Equal(before.TotalForegroundSeconds, after.TotalForegroundSeconds);
        Assert.Equal(before.AppCount, after.AppCount);

        // 派生表两侧都被清掉（策略是"删掉、由读路径在线重算"）。
        Assert.Empty(await db.Set<MobileUsageAggregateEntity>().ToListAsync());
        Assert.Empty(await db.Set<MobileAnalyticsMaterializationEntity>().ToListAsync());
        Assert.Empty(await db.Set<MobileTimelineBlockEntity>().ToListAsync());
    }

    /// <summary>
    /// C2 第 2 点：合并删掉派生数据后，用户不会看到空白 —— 读数路径会回退到用原始行在线计算，
    /// 而且拿到的是**合并后的正确数字**，不是被删掉的旧快照。
    ///
    /// <para>
    /// 用 <c>mobile_analytics_materializations</c> 的覆盖记录 + 一条假聚合行做"探针"：
    /// 合并前 heatmap 读到的就是探针值（证明物化命中确实生效），合并后探针必须消失、
    /// 数字变成两台设备在线计算的合计。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task MergeAsync_InvalidatesTheMaterializedSnapshotSoReadsRecomputeFromRawRows()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var db = scope.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceId, Now.AddDays(-60));
        SeedSession(db, MobileTestHelpers.UserId, TargetDeviceId, "com.target.app", WindowStart.AddHours(1), TargetSessionSeconds);
        SeedSession(db, MobileTestHelpers.UserId, SourceDeviceId, "com.source.app", WindowStart.AddHours(3), SourceSessionSeconds);
        SeedMaterializedCoverageAndAggregates(db, TargetDeviceId, StaleSnapshotSeconds);
        await db.SaveChangesAsync();

        var aggregation = CreateAggregationService(db);
        var deviceScoped = new MobileAnalyticsQueryRequest(WindowStart, WindowEnd, DeviceId: TargetDeviceId);

        var heatmapBefore = await aggregation.GetHeatmapAsync(deviceScoped, CancellationToken.None);
        Assert.Contains(heatmapBefore, bucket => bucket.ForegroundSeconds == StaleSnapshotSeconds);

        var service = new DeviceManagementService(
            db, MobileTestHelpers.CurrentUser(MobileTestHelpers.UserId), MobileTestHelpers.Time(Now));
        await service.MergeAsync([SourceDeviceId], TargetDeviceId, CancellationToken.None);

        var heatmapAfter = await aggregation.GetHeatmapAsync(deviceScoped, CancellationToken.None);
        Assert.DoesNotContain(heatmapAfter, bucket => bucket.ForegroundSeconds == StaleSnapshotSeconds);
        Assert.Equal(TargetSessionSeconds + SourceSessionSeconds, heatmapAfter.Sum(bucket => bucket.ForegroundSeconds));

        // 顺手确认块通道也一样能读（不再依赖被删掉的物化覆盖）。
        var blocks = await new MobileTimelineBlockService(
                db, MobileTestHelpers.CurrentUser(MobileTestHelpers.UserId), MobileTestHelpers.Time(Now))
            .GetBlocksAsync(deviceScoped, CancellationToken.None);
        Assert.True(blocks.Items.Count > 0, "合并后时间轴块应能由原始行在线重算出来");
    }

    // ---------------------------------------------------------------- AC-3：预览 == 实际删除

    /// <summary>
    /// AC-3：<c>delete-preview</c> 的逐表计数 == <c>DELETE</c> 实际删除数。
    /// 「实际删除数」的观测方式是三条独立证据的三方一致：
    /// 1) DELETE 接口返回值（来自真正执行的 <c>ExecuteDelete</c> 受影响行数）；
    /// 2) 删除前用原生 SQL 对同一批库表计数；
    /// 3) 删除后再数一次，必须为 0。
    /// </summary>
    [SkippableFact]
    public async Task DeletePreview_PerTableCountsEqualTheActualDeletedCountsAndTheTablesEndUpEmpty()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var db = scope.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        db.Set<MobileUsageEventEntity>().AddRange(
            UsageEvent(TargetDeviceId, "com.a", Now.AddDays(-1)),
            UsageEvent(TargetDeviceId, "com.b", Now.AddDays(-1)));
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, PackageName = "com.a",
            StartUtc = Now.AddDays(-1), EndUtc = Now.AddDays(-1).AddMinutes(5), DurationMs = 300_000,
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, PackageName = "com.a",
            WindowStartUtc = Now.AddDays(-1), WindowEndUtc = Now.AddDays(-1).AddMinutes(15),
            TotalTimeVisibleMs = 300_000, LastTimeUsedUtc = Now.AddDays(-1), SourceKind = "usage-summary",
        });
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, RecordedAtUtc = Now.AddDays(-1),
            Latitude = 31.2304m, Longitude = 121.4737m, HorizontalAccuracyMeters = 12.5m,
            Provider = "fused", Source = "android",
        });
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, BatchId = "batch-1",
            WindowStartUtc = Now.AddDays(-1), WindowEndUtc = Now.AddDays(-1).AddMinutes(15),
            AcceptedCount = 1, Status = "completed", CompletedAtUtc = Now.AddDays(-1),
        });
        db.Set<MobileForensicEventEntity>().Add(new MobileForensicEventEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, EventType = "heartbeat",
            ClientItemKey = "hb-1", OccurredAtUtc = Now.AddDays(-1),
        });
        db.Set<MobileDroppedReasonDailyEntity>().Add(new MobileDroppedReasonDailyEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, LocalDate = "2026-09-12",
            Reason = "low-accuracy", Count = 3, ReceivedAtUtc = Now,
        });
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, PackageName = "com.a",
            DisplayName = "A", Category = "tools", CreatedAt = Now.AddDays(-1), UpdatedAt = Now.AddDays(-1),
        });
        db.Set<MobileTimelineBlockEntity>().Add(new MobileTimelineBlockEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId,
            StartUtc = WindowStart, EndUtc = WindowStart.AddMinutes(15),
            LocalDate = "2026-09-10", ForegroundSeconds = 60, SessionCount = 1, AppCount = 1,
        });
        db.Set<MobileUsageAggregateEntity>().Add(new MobileUsageAggregateEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId,
            Granularity = MobileAnalyticsDefaults.HourGranularity, Timezone = MobileAnalyticsDefaults.DefaultTimezone,
            BucketStartUtc = WindowStart, BucketEndUtc = WindowStart.AddHours(1),
            PackageName = "com.a", DisplayName = "A", LifeCategory = MobileLifeCategories.Uncategorized,
            ForegroundSeconds = 60, CreatedAt = Now, UpdatedAt = Now,
        });
        db.Set<MobileAnalyticsMaterializationEntity>().Add(new MobileAnalyticsMaterializationEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId,
            Timezone = MobileAnalyticsDefaults.DefaultTimezone, CoveredFromUtc = WindowStart,
            CoveredToUtc = WindowEnd, GeneratedAt = Now, CreatedAt = Now, UpdatedAt = Now,
        });
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity
        {
            DeviceId = TargetDeviceId, DaemonKind = MobileDaemonKind.Android, Version = "v1",
            ServerUrl = "http://127.0.0.1:5858", StatusJson = "{}", ReceivedAt = Now.AddMinutes(-1),
        });
        db.Set<EndpointStatusEntity>().Add(new EndpointStatusEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, Platform = "android",
            UploadStatus = "Healthy", LastHeartbeatAt = Now.AddMinutes(-1),
            CreatedAt = Now.AddDays(-1), UpdatedAt = Now.AddMinutes(-1),
        });
        db.Set<EndpointNotificationActionEntity>().Add(new EndpointNotificationActionEntity
        {
            UserId = MobileTestHelpers.UserId, DeviceId = TargetDeviceId, Action = "open-settings",
            RiskLevel = "Low", Result = "Executed", CreatedAt = Now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var service = new DeviceManagementService(
            db, MobileTestHelpers.CurrentUser(MobileTestHelpers.UserId), MobileTestHelpers.Time(Now));

        var preview = await service.PreviewDeleteAsync(TargetDeviceId, CancellationToken.None);
        var observedBefore = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in preview.Tables)
            observedBefore[row.Table] = await RawCountAsync(scope.Admin, row.Table, MobileTestHelpers.UserId, TargetDeviceId);

        var result = await service.DeleteAsync(TargetDeviceId, CancellationToken.None);

        // 1) 预览与实际的表集合、逐表计数、总数三方一致。
        Assert.Equal(preview.Tables.Select(t => t.Table), result.Tables.Select(t => t.Table));
        foreach (var previewRow in preview.Tables)
        {
            Assert.Equal(previewRow.RowCount, result.Tables.Single(t => t.Table == previewRow.Table).RowCount);
            Assert.Equal(observedBefore[previewRow.Table], previewRow.RowCount);
        }
        Assert.Equal(result.Tables.Sum(t => t.RowCount), result.TotalRows);

        // 2) 至少要有几处非 0，否则"全 0 相等"是假绿。
        Assert.Equal(2, preview.Tables.Single(t => t.Table == DeviceScopedTables.UsageEvents).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.DaemonHeartbeats).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.EndpointStatuses).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.EndpointNotificationActions).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.Devices).RowCount);

        // 3) 删除后逐表再数一次，全部为 0（不留"只有心跳没有设备"这类悬空记录）。
        foreach (var row in result.Tables)
            Assert.Equal(0, await RawCountAsync(scope.Admin, row.Table, MobileTestHelpers.UserId, TargetDeviceId));
    }

    // ---------------------------------------------------------------- AC-1：跨用户

    /// <summary>
    /// AC-1 的跨用户断言（真库版，按用户隔离的全局过滤器处于生产打开状态）：
    /// 另一个用户在 <c>mobile_devices</c> 里持有与源设备同名的 device_id 字符串。
    /// 直接按 device_id 处理 <c>daemon_heartbeats</c> 会把它的行一起搬走/删掉 ——
    /// 本用例证明合并与删除都不会碰它。
    ///
    /// <para>
    /// 用生产形状的 DbContext（带 <c>ICurrentUserService</c>）跑，因为
    /// <c>PimDbContext</c> 的隔离过滤器正是"跨用户检查"在生产里会被静默削弱的地方：
    /// 少了 <c>IgnoreQueryFilters()</c>，冲突查询永远返回空、判断恒为"独占"，
    /// 于是污染发生而"旧 device_id 0 行"的断言照样变绿。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task MergeAndDelete_NeverTouchAnotherUsersRowsWhenTheDeviceIdStringIsShared()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var db = scope.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceId, Now.AddDays(-60));
        db.Set<MobileUsageEventEntity>().Add(UsageEvent(SourceDeviceId, "com.ours", Now.AddDays(-60)));
        SeedDevice(db, OtherUserId, SourceDeviceId, Now.AddDays(-10));
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = OtherUserId, DeviceId = SourceDeviceId, PackageName = "com.other.user",
            EventType = "foreground", ClassName = string.Empty, EventTimestampUtc = Now.AddDays(-10),
            SourceWindowStartUtc = Now.AddDays(-10), SourceWindowEndUtc = Now.AddDays(-10).AddMinutes(1),
            CollectedAtUtc = Now.AddDays(-10).AddMinutes(1),
        });
        // 唯一索引 (device_id, daemon_kind) 没有 user_id：两个用户同名时全局只有这一行。
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity
        {
            DeviceId = SourceDeviceId, DaemonKind = MobileDaemonKind.Android, Version = "shared",
            ServerUrl = "http://127.0.0.1:5858", StatusJson = "{}", ReceivedAt = Now.AddMinutes(-3),
        });
        await db.SaveChangesAsync();

        var service = new DeviceManagementService(
            db, MobileTestHelpers.CurrentUser(MobileTestHelpers.UserId), MobileTestHelpers.Time(Now));
        await service.MergeAsync([SourceDeviceId], TargetDeviceId, CancellationToken.None);

        // 别的用户：设备行、事件、心跳行全部原样。
        Assert.Equal(1, await db.Set<MobileDeviceEntity>().IgnoreQueryFilters()
            .CountAsync(d => d.UserId == OtherUserId && d.DeviceId == SourceDeviceId));
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().IgnoreQueryFilters()
            .CountAsync(e => e.UserId == OtherUserId && e.DeviceId == SourceDeviceId));
        var heartbeat = Assert.Single(await db.Set<DaemonHeartbeatEntity>().ToListAsync());
        Assert.Equal(SourceDeviceId, heartbeat.DeviceId);
        Assert.Equal("shared", heartbeat.Version);

        // 本用户的数据照常并了过去（证明合并确实执行了，上面不是"什么都没发生"的假绿）。
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>()
            .CountAsync(e => e.DeviceId == TargetDeviceId && e.PackageName == "com.ours"));

        // 删除目标设备同样不能碰别的用户的同名行。
        await service.DeleteAsync(TargetDeviceId, CancellationToken.None);

        Assert.Equal(1, await db.Set<MobileDeviceEntity>().IgnoreQueryFilters()
            .CountAsync(d => d.UserId == OtherUserId && d.DeviceId == SourceDeviceId));
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().IgnoreQueryFilters()
            .CountAsync(e => e.UserId == OtherUserId && e.DeviceId == SourceDeviceId));
        Assert.Single(await db.Set<DaemonHeartbeatEntity>().ToListAsync());
    }

    // ---------------------------------------------------------------- helpers

    private static MobileUsageAggregationService CreateAggregationService(PimDbContext db)
    {
        var currentUser = MobileTestHelpers.CurrentUser(MobileTestHelpers.UserId);
        var time = MobileTestHelpers.Time(Now);
        return new MobileUsageAggregationService(
            db,
            currentUser,
            new MobileAnalyticsQueryService(time),
            new MobileUsageGoalService(db, currentUser, time),
            time);
    }

    /// <summary>
    /// 造一份"与请求窗口逐刻相等、时区一致、且没有失效标记"的物化覆盖 + 一条可辨识的聚合行，
    /// 让读数路径命中物化表（这是 <c>MobileAnalyticsMaterializationGate</c> 的命中前提）。
    /// </summary>
    private static void SeedMaterializedCoverageAndAggregates(PimDbContext db, string deviceId, long foregroundSeconds)
    {
        var timezone = MobileAnalyticsDefaults.DefaultTimezone;
        db.Set<MobileAnalyticsMaterializationEntity>().Add(new MobileAnalyticsMaterializationEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            Timezone = timezone,
            CoveredFromUtc = WindowStart,
            CoveredToUtc = WindowEnd,
            GeneratedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Set<MobileUsageAggregateEntity>().Add(new MobileUsageAggregateEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            Granularity = MobileAnalyticsDefaults.HourGranularity,
            Timezone = timezone,
            BucketStartUtc = WindowStart.AddHours(1),
            BucketEndUtc = WindowStart.AddHours(2),
            PackageName = "com.target.app",
            DisplayName = "Target",
            LifeCategory = MobileLifeCategories.Uncategorized,
            ForegroundSeconds = foregroundSeconds,
            IsSystemNoise = false,
            IsStale = false,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
    }

    /// <summary>
    /// 直接对库表计数（不经过 EF 查询管道）。
    /// <c>daemon_heartbeats</c> 没有 user_id 列 —— 这正是它在删除范围里需要"归属判断"的原因。
    /// </summary>
    private static Task<int> RawCountAsync(NpgsqlConnection connection, string table, Guid userId, string deviceId)
        => table == DeviceScopedTables.DaemonHeartbeats
            ? CountAsync(connection, $"SELECT COUNT(*) FROM \"{table}\" WHERE device_id = @deviceId", deviceId)
            : CountAsync(
                connection,
                $"SELECT COUNT(*) FROM \"{table}\" WHERE user_id = @userId AND device_id = @deviceId",
                deviceId,
                userId);

    private static async Task<int> CountAsync(
        NpgsqlConnection connection, string sql, string deviceId, Guid? userId = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("deviceId", deviceId);
        if (userId is not null) command.Parameters.AddWithValue("userId", userId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static void SeedDevice(PimDbContext db, Guid userId, string deviceId, DateTimeOffset lastSeen)
        => db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            DeviceHash = $"hash-{userId}-{deviceId}",
            DisplayName = "OPPO PLG110",
            Brand = "OPPO",
            Model = "PLG110",
            OsVersion = "15",
            AppVersion = "2026.09.504",
            RegisteredAtUtc = lastSeen,
            LastSeenAtUtc = lastSeen,
        });

    private static void SeedSession(
        PimDbContext db, Guid userId, string deviceId, string packageName, DateTimeOffset start, long seconds)
        => db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            PackageName = packageName,
            StartUtc = start,
            EndUtc = start.AddSeconds(seconds),
            DurationMs = seconds * 1000,
            QualityFlagsJson = "[]",
        });

    private static MobileUsageEventEntity UsageEvent(string deviceId, string packageName, DateTimeOffset timestamp)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = packageName,
            EventType = "foreground",
            ClassName = string.Empty,
            EventTimestampUtc = timestamp,
            SourceWindowStartUtc = timestamp,
            SourceWindowEndUtc = timestamp.AddMinutes(1),
            CollectedAtUtc = timestamp.AddMinutes(1),
        };
}

/// <summary>
/// 一次性真库：在 <c>PIM_TEST_CONN</c> 指向的服务器上建一个新库，用 <c>EnsureCreated</c>
/// 建出生产同款 schema（含唯一索引），结束时删库。
///
/// <para>
/// <c>Db</c> 用生产形状构造（带 <c>ICurrentUserService</c>，因此按用户隔离的全局过滤器生效）；
/// <c>Admin</c> 是连到这个一次性库上的原生连接，供独立计数/清理用。
/// </para>
/// </summary>
internal sealed class RealDbScope : IAsyncDisposable
{
    private readonly string _database;
    private readonly NpgsqlConnection _serverConnection;

    private RealDbScope(PimDbContext db, NpgsqlConnection admin, NpgsqlConnection serverConnection, string database)
    {
        Db = db;
        Admin = admin;
        _serverConnection = serverConnection;
        _database = database;
    }

    public PimDbContext Db { get; }

    /// <summary>指向一次性库的原生连接（表名不带 schema 限定，走默认 search_path）。</summary>
    public NpgsqlConnection Admin { get; }

    public static async Task<RealDbScope> CreateAsync(Guid actingUserId)
    {
        var connStr = RealDbTestConnection.Require();
        MobileTestHelpers.RegisterMobileModule();

        var serverConnection = new NpgsqlConnection(connStr);
        await serverConnection.OpenAsync();
        var database = $"test_device_scope_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", serverConnection))
            await create.ExecuteNonQueryAsync();

        var databaseConn = new NpgsqlConnectionStringBuilder(connStr) { Database = database }.ConnectionString;
        var admin = new NpgsqlConnection(databaseConn);
        await admin.OpenAsync();

        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(databaseConn, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        var db = new PimDbContext(options, MobileTestHelpers.CurrentUser(actingUserId));
        await db.Database.EnsureCreatedAsync();

        return new RealDbScope(db, admin, serverConnection, database);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Admin.DisposeAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", _serverConnection))
            await drop.ExecuteNonQueryAsync();
        await _serverConnection.DisposeAsync();
    }
}
