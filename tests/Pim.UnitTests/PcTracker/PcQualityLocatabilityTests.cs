using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-8（pc/quality 的可定位性）：
/// 区分「采集端心跳过期」与「本库数据滞后于查询范围」、给 event 基线判定、列出缺数时段。
/// </summary>
public sealed class PcQualityLocatabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime QueryDate = new(2026, 9, 27);

    [Fact]
    public async Task GetQualityAsync_LaggingSnapshotDatabase_ReportsDatabaseLagNotDeadCollector()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        var rangeEnd = dayStart.AddDays(1);
        // 镜像/滞后快照库的样子：内容一路写到 2026-09-28T12:20Z（含查询日之后的时段），
        // 心跳停在 2026-09-28T12:27Z，而「现在」是 2026-09-30T10:00Z —— 整库停在 45 小时前。
        var horizon = DateTimeOffset.Parse("2026-09-28T12:27:00+00:00");
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddTrackerEvent(db, dayStart.AddHours(23), 1800, "window");
        AddSample(db, horizon.AddMinutes(-7));
        AddHeartbeat(db, horizon);
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        // AC-8.1（情形一）：判据直接写在 details 里，不看代码就能解释「为什么读数是旧的」。
        Assert.Equal("database-lags-now", daemon.Details["staleReason"]);
        Assert.Equal(horizon.AddMinutes(-7).ToString("O"), daemon.Details["contentHorizonUtc"]);
        Assert.Equal(rangeEnd.ToString("O"), daemon.Details["rangeEndUtc"]);
        Assert.Equal("True", daemon.Details["libraryFrozenAtHorizon"]);
        Assert.Contains(result.Issues, i => i.Code == "database-lags-now");
        Assert.DoesNotContain(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
        Assert.NotEqual(PimHealthStatus.Critical, daemon.Status);
    }

    [Fact]
    public async Task GetQualityAsync_RangeOlderThanDatabaseData_ReportsRangeBeyondHorizon()
    {
        await using var db = CreateDb();
        // 查询 2026-09-29（范围末尾 2026-09-29T20:00Z），但本库内容只到 2026-09-28T11:57Z
        // —— 落后超过一天，属于「本库数据滞后于查询范围」。
        var horizon = DateTimeOffset.Parse("2026-09-28T12:27:00+00:00");
        AddTrackerEvent(db, horizon.AddHours(-2), 60, "window");
        AddSample(db, horizon.AddMinutes(-30));
        AddHeartbeat(db, horizon);
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(new DateTime(2026, 9, 29), null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        Assert.Equal("query-range-beyond-database-horizon", daemon.Details["staleReason"]);
        Assert.True(int.Parse(daemon.Details["rangeShortfallMinutes"], CultureInfo.InvariantCulture) >= 24 * 60);
        var issue = Assert.Single(result.Issues, i => i.Code == "range-beyond-database-horizon");
        Assert.Equal(PimHealthStatus.Warning, issue.Severity);
        Assert.DoesNotContain(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
    }

    [Fact]
    public async Task GetQualityAsync_CoveredRangeWithDeadHeartbeat_BlamesCollector()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 本库覆盖到范围末尾（数据一直写到范围结束），但心跳停在 2 小时前 → 采集端停了。
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddTrackerEvent(db, dayStart.AddHours(23.5), 60, "window");
        AddSample(db, dayStart.AddHours(3));
        AddHeartbeat(db, Now.AddHours(-2));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        // AC-8.1（情形二）：同样的「心跳旧」，但判据不同 —— 这是真正的采集端心跳过期。
        Assert.Equal("collector-heartbeat-stale", daemon.Details["staleReason"]);
        Assert.Contains(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
        Assert.Equal(PimHealthStatus.Critical, daemon.Status);
    }

    [Fact]
    public async Task GetQualityAsync_TrackerEventsExposeBaselineAndVerdict()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 前 7 个业务日每天 100 条事件 → 基线；查询日只有 10 条 → 明显偏低。
        for (var day = -7; day <= -1; day++)
        {
            for (var i = 0; i < 100; i++)
                AddTrackerEvent(db, dayStart.AddDays(day).AddMinutes(i * 10), 30, "window");
        }

        for (var i = 0; i < 10; i++)
            AddTrackerEvent(db, dayStart.AddMinutes(i * 10), 30, "window");

        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        // AC-8.2：基线值 + 判定 + 依据字段都在响应里。
        Assert.Equal("100", tracker.Details["baselineEventCount"]);
        Assert.Equal("偏低", tracker.Details["verdict"]);
        Assert.Equal("近 7 个业务日事件数中位数", tracker.Details["baselineMethod"]);
        Assert.Equal("10", tracker.Details["currentDailyEventCount"]);
        Assert.Equal("0.1", tracker.Details["deviationRatio"]);
    }

    [Fact]
    public async Task GetQualityAsync_ReportsInteriorMissingHoursWithTheMomentDataStopped()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 本地 06:00–07:00 与 13:00–14:00 有数据 → 中间本地 07:00–13:00 连续 6 小时无数据。
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddTrackerEvent(db, dayStart.AddHours(9), 3600, "window");
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        // AC-8.3：issues 里给出具体时段，而不是一句「数据不完整」。
        var issue = Assert.Single(result.Issues, i => i.Code == "tracker-events-missing-hours");
        Assert.Contains("2026-09-27 07:00", issue.Message);
        Assert.Contains("2026-09-27 13:00", issue.Message);
        Assert.Equal("6", tracker.Details["missingHourCount"]);
        Assert.Contains("2026-09-27 07:00", tracker.Details["missingHours"]);
        Assert.Contains("2026-09-27 12:00", tracker.Details["missingHours"]);
        Assert.Equal(dayStart.AddHours(3).ToString("O"), tracker.Details["disconnectedFromUtc"]);
    }

    [Fact]
    public async Task GetQualityAsync_KeystatsSamplesCountAsCoverageEvenWithoutTrackerEvents()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");   // 本地 06:00–07:00
        // 本地 08:00–09:00 只有 KeyStats 采样（没有窗口事件）—— 这段时间同样算「有数据」。
        for (var minute = 0; minute < 60; minute += 5)
            AddSample(db, dayStart.AddHours(4).AddMinutes(minute));
        AddTrackerEvent(db, dayStart.AddHours(9), 3600, "window");   // 本地 13:00–14:00
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        // 缺数小时 = 07:00（1 个）+ 09:00–12:00（4 个）；08:00 因为采样而被视为有数据。
        Assert.Equal("5", tracker.Details["missingHourCount"]);
        Assert.Contains("2026-09-27 07:00", tracker.Details["missingHours"]);
        Assert.DoesNotContain("2026-09-27 08:00", tracker.Details["missingHours"]);
        Assert.Equal(dayStart.AddHours(3).ToString("O"), tracker.Details["disconnectedFromUtc"]);
    }

    [Fact]
    public async Task GetQualityAsync_ComponentMessagesDoNotRepeatTheOverviewText()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        AddTrackerEvent(db, dayStart.AddHours(1), 60, "window");
        AddSample(db, dayStart.AddHours(1));
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        // AC-8.4：组件 message 只描述本组件，不复制总览文案。
        Assert.NotEmpty(result.Components);
        Assert.All(result.Components, component =>
        {
            Assert.False(string.IsNullOrWhiteSpace(component.Message));
            Assert.NotEqual(result.Message, component.Message);
            Assert.NotEqual(result.Label, component.Message);
        });
    }

    private static PcTrackerQualityService Service(PimDbContext db)
        => new(db, new StubTimeProvider { UtcNowValue = Now });

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static void AddTrackerEvent(PimDbContext db, DateTimeOffset timestamp, double duration, string eventType)
        => db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "DESKTOP",
            Timestamp = timestamp,
            Duration = duration,
            EventType = eventType,
            AppName = "Code.exe",
            WindowTitle = "Project",
            Date = timestamp.Date,
            RawJson = "{}"
        });

    private static void AddSample(PimDbContext db, DateTimeOffset sampledAt)
        => db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = "DESKTOP",
            SampledAtUtc = sampledAt,
            StatsDate = sampledAt.Date,
            KeyPresses = 10,
            LeftClicks = 2,
            MouseDistance = 100,
            ScrollDistance = 20
        });

    private static void AddHeartbeat(PimDbContext db, DateTimeOffset receivedAt)
        => db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "DESKTOP",
            DaemonKind = "windows",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            LastSuccessfulUploadAt = receivedAt,
            LastAttemptedUploadAt = receivedAt,
            UploadQueueCount = 0,
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = receivedAt
        });
}
