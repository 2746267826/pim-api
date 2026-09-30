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
    public async Task GetQualityAsync_LaggingSnapshotDatabase_KeepsHeartbeatRedAndExplainsTheLag()
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
        Assert.Equal("database-or-collector-frozen", daemon.Details["staleCause"]);
        Assert.Equal(horizon.AddMinutes(-7).ToString("O"), daemon.Details["contentHorizonUtc"]);
        Assert.Equal(rangeEnd.ToString("O"), daemon.Details["rangeEndUtc"]);
        Assert.Equal("True", daemon.Details["libraryFrozenAtHorizon"]);
        Assert.Contains(result.Issues, i => i.Code == "database-or-collector-frozen");
        // 心跳判据不被降级（WO「明确不做」第 6 条）：红灯照旧。
        Assert.Equal("collector-heartbeat-stale", daemon.Details["staleReason"]);
        Assert.Contains(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
        Assert.Equal(PimHealthStatus.Critical, daemon.Status);
    }

    [Fact]
    public async Task GetQualityAsync_CoveredRangeWithDeadHeartbeat_BlamesCollector()
    {
        await using var db = CreateDb();
        // 本库内容还在更新（8 分钟前刚有事件），但心跳停在 2 小时前 → 采集端心跳通道的问题。
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddSample(db, Now.AddMinutes(-8));
        AddHeartbeat(db, Now.AddHours(-2));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        // AC-8.1（情形二）：同样的「读数旧」，但判据指向采集端心跳本身。
        Assert.Equal("collector-heartbeat-stale", daemon.Details["staleCause"]);
        Assert.Equal("collector-heartbeat-stale", daemon.Details["staleReason"]);
        Assert.Equal("False", daemon.Details["libraryFrozenAtHorizon"]);
        Assert.Contains(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
        Assert.Equal(PimHealthStatus.Critical, daemon.Status);
    }

    [Fact]
    public async Task GetQualityAsync_ContentOlderThanHeartbeat_IsNotBlamedOnTheHeartbeatChannel()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 内容停在 07:00Z，心跳 30 分钟前还在跳（内容比心跳**旧**）——
        // 这不满足 collector-heartbeat-stale 的定义（「本库内容另有更新的数据，只有心跳停」）。
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddSample(db, Now.AddHours(-3));
        AddHeartbeat(db, Now.AddMinutes(-30));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        // 成因栏只描述本库/心跳的时间关系，不参杂心跳年龄；心跳过期由 staleReason / heartbeatStaleAt 单独表达。
        Assert.Equal("none", daemon.Details["staleCause"]);
        Assert.Equal("True", daemon.Details["heartbeatStaleAt"]);
        Assert.Equal("collector-heartbeat-stale", daemon.Details["staleReason"]);
    }

    [Fact]
    public async Task GetQualityAsync_ContentAndHeartbeatStoppedTogetherRecently_ReportsNoCause()
    {
        await using var db = CreateDb();
        // 内容与心跳一起停在 2 小时前（相差 20 分钟、整体滞后不到 24 小时）：不构成「一起停很久」，
        // 也没有「只有心跳停」的证据 → 成因栏为 none。
        var horizon = Now.AddHours(-2);
        AddSample(db, horizon.AddMinutes(-20));
        AddHeartbeat(db, horizon);
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        Assert.Equal("none", daemon.Details["staleCause"]);
        Assert.Equal("False", daemon.Details["libraryFrozenAtHorizon"]);
        Assert.Equal("collector-heartbeat-stale", daemon.Details["staleReason"]);
    }

    [Fact]
    public async Task GetQualityAsync_LiveDatabaseWithFreshHeartbeat_IsHealthyAndSaysSo()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 对照情形：同一个查询日，但本库是「活的」——内容与心跳都新鲜。
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddTrackerEvent(db, Now.AddMinutes(-2), 60, "window");
        AddSample(db, Now.AddMinutes(-1));
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        // AC-8.1（情形二）：两种情形的 overallStatus 差异可由 details 判据解释。
        Assert.Equal("none", daemon.Details["staleCause"]);
        Assert.Equal("none", daemon.Details["staleReason"]);
        Assert.Equal("False", daemon.Details["libraryFrozenAtHorizon"]);
        Assert.DoesNotContain(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
        Assert.DoesNotContain(result.Issues, i => i.Code == "database-lags-now");
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
        // 心跳与内容一起停住，且查询范围还超出内容 → 成因码取更具体的那个（措辞同时给出两种可能）。
        Assert.Equal("content-and-heartbeat-frozen", daemon.Details["staleCause"]);
        Assert.True(int.Parse(daemon.Details["rangeShortfallMinutes"], CultureInfo.InvariantCulture) >= 24 * 60);
        var issue = Assert.Single(result.Issues, i => i.Code == "content-and-heartbeat-frozen");
        Assert.Equal(PimHealthStatus.Warning, issue.Severity);
        // 心跳判据同样保留（心跳确实过期了）。
        Assert.Contains(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
    }

    [Fact]
    public async Task GetQualityAsync_ContentHorizonIsTheLatestEventEnd_NotTheLatestStart()
    {
        await using var db = CreateDb();
        // 内容地平线的口径是 max(Timestamp + Duration)，不是「起点最新的那一条 + 它自己的时长」：
        // 一条更早开始、但持续到 02:00Z 的长事件，比 01:30Z 开始的短事件结束得更晚。
        var longEvent = DateTimeOffset.Parse("2026-09-28T00:00:00+00:00");
        AddTrackerEvent(db, longEvent, 7200, "window");
        AddTrackerEvent(db, DateTimeOffset.Parse("2026-09-28T01:30:00+00:00"), 300, "window");
        AddSample(db, DateTimeOffset.Parse("2026-09-28T01:00:00+00:00"));
        AddHeartbeat(db, DateTimeOffset.Parse("2026-09-28T01:50:00+00:00"));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        // AC-8.1：判据字段必须写出本库真实的内容终点，否则「读数为什么旧」会被解释成错误的时刻。
        Assert.Equal(longEvent.AddSeconds(7200).ToString("O"), daemon.Details["contentHorizonUtc"]);
        Assert.Equal("True", daemon.Details["libraryFrozenAtHorizon"]);
        Assert.Equal("database-or-collector-frozen", daemon.Details["staleCause"]);
        Assert.Contains(result.Issues, i => i.Code == "database-or-collector-frozen");
    }

    [Fact]
    public async Task GetQualityAsync_ContentHorizonCoversAwEvents_NotJustTrackerEvents()
    {
        await using var db = CreateDb();
        // 同一口径要覆盖 AW 事件表：只按「最新起点」推断同样会低估内容终点。
        var longEvent = DateTimeOffset.Parse("2026-09-28T00:00:00+00:00");
        AddAwEvent(db, longEvent, 7200);
        AddAwEvent(db, DateTimeOffset.Parse("2026-09-28T01:30:00+00:00"), 300);
        AddSample(db, DateTimeOffset.Parse("2026-09-28T00:30:00+00:00"));
        AddHeartbeat(db, DateTimeOffset.Parse("2026-09-28T01:50:00+00:00"));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        Assert.Equal(longEvent.AddSeconds(7200).ToString("O"), daemon.Details["contentHorizonUtc"]);
        Assert.Equal("True", daemon.Details["libraryFrozenAtHorizon"]);
    }

    [Fact]
    public async Task GetQualityAsync_EventCrossingTheRangeStartStillCountsAsCoverage()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 起点在业务日之前、伸进范围内的事件（真实数据里确实存在这类跨边界记录）：
        // 19:50Z–20:10Z 覆盖了范围开头 10 分钟。取数阶段若只按「起点落在范围内」过滤，
        // 这段数据会被整条丢掉，范围第一小时被当成没有数据。
        AddTrackerEvent(db, dayStart.AddMinutes(-10), 1200, "window");
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        // AC-8.3 / AC-8.4：本库这段范围有数据，就不能报成「整段没有数据」。
        Assert.Equal("False", tracker.Details["coverageEmpty"]);
        Assert.Equal(dayStart.AddMinutes(10).ToString("O"), tracker.Details["lastDataAtUtc"]);
        Assert.DoesNotContain(result.Issues, i => i.Code == "missing-tracker-events");
        // 尾部空白仍然如实上报：最后一条数据 20:10Z 之后到范围末尾。
        Assert.Equal(dayStart.AddMinutes(10).ToString("O"), tracker.Details["trailingGapFromUtc"]);
        Assert.Equal("1430", tracker.Details["trailingGapMinutes"]);
    }

    [Fact]
    public async Task GetQualityAsync_TodaysUnfinishedRangeDoesNotReportFutureHoursAsMissing()
    {
        await using var db = CreateDb();
        // 查询当天（业务日末尾 2026-09-30T20:00Z 还在未来），最后一条数据在 5 分钟前：
        // 「还没到的小时」不是缺数，缺数问题项与 missingHours 都不该写到未来。
        var today = new DateTime(2026, 9, 30);
        AddTrackerEvent(db, Now.AddMinutes(-5), 60, "window");
        AddSample(db, Now.AddMinutes(-5));
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(today, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        Assert.DoesNotContain(result.Issues, i => i.Code == "tracker-events-missing-hours");
        Assert.Equal("0", tracker.Details["missingHourCount"]);
        Assert.Equal(string.Empty, tracker.Details["missingHours"]);
        // 缺数判定与 trailingGapMinutes 用同一个有效范围末尾（= 现在，而不是还在未来的业务日末尾）。
        Assert.Equal(Now.ToString("O"), tracker.Details["coverageEndUtc"]);
        Assert.Equal("4", tracker.Details["trailingGapMinutes"]);
        Assert.Equal("False", tracker.Details["coverageEmpty"]);
    }

    [Fact]
    public async Task GetQualityAsync_UnrepresentableEventDuration_DoesNotFailTheEndpoint()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 脏数据：时长 1e12 秒的事件，结束时刻超出 DateTimeOffset 能表示的范围。
        // 这不该让整个体检接口 500 —— 结束时刻截到可表示的最大值。
        AddTrackerEvent(db, dayStart.AddHours(2), 1e12, "window");
        AddSample(db, Now.AddMinutes(-1));
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        Assert.Equal(DateTimeOffset.MaxValue.ToString("O"), daemon.Details["contentHorizonUtc"]);
    }

    [Fact]
    public async Task GetQualityAsync_WithoutHeartbeat_StillExplainsWhetherTheLibraryHasContent()
    {
        await using var db = CreateDb();
        // 没有心跳时也要能读出「本库有没有内容」：空库 → no-content。
        await db.SaveChangesAsync();

        var empty = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);
        var emptyDaemon = Assert.Single(empty.Components, c => c.Key == "daemon-upload");
        Assert.Equal("no-content", emptyDaemon.Details["staleCause"]);

        // 有内容但缺心跳 → heartbeat-missing，且内容地平线照旧给出（没有心跳不等于没有数据）。
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        AddTrackerEvent(db, dayStart.AddHours(2), 3600, "window");
        AddSample(db, dayStart.AddHours(2));
        await db.SaveChangesAsync();

        var withContent = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);
        var daemon = Assert.Single(withContent.Components, c => c.Key == "daemon-upload");
        Assert.Equal("missing", daemon.Details["heartbeat"]);
        Assert.Equal("heartbeat-missing", daemon.Details["staleCause"]);
        Assert.Equal(dayStart.AddHours(3).ToString("O"), daemon.Details["contentHorizonUtc"]);
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
        // 本地 13:00 起连续覆盖到业务日结束：本用例只验证「中间空洞」，不引入尾部空白。
        for (var hour = 9; hour < 24; hour++)
            AddTrackerEvent(db, dayStart.AddHours(hour), 3600, "window");
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
        // 本地 13:00 起连续覆盖到业务日结束，避免尾部空白干扰本用例。
        for (var hour = 9; hour < 24; hour++)
            AddTrackerEvent(db, dayStart.AddHours(hour), 3600, "window");
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
    public async Task GetQualityAsync_ReportsTheTrailingBreakWhenDataStopsMidDay()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        // 本地 06:00–11:00 有数据，之后直到业务日结束再没有任何数据 → 尾部断档 17 小时。
        for (var hour = 2; hour <= 6; hour++)
            AddTrackerEvent(db, dayStart.AddHours(hour), 3600, "window");
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        // AC-8.3 / REQ-8：「从哪个时刻起断开」要能直接读出来。
        var issue = Assert.Single(result.Issues, i => i.Code == "tracker-events-missing-hours");
        Assert.Contains("2026-09-27 11:00", issue.Message);
        Assert.Equal("17", tracker.Details["missingHourCount"]);
        Assert.Equal(dayStart.AddHours(7).ToString("O"), tracker.Details["disconnectedFromUtc"]);
        Assert.Equal(dayStart.AddHours(7).ToString("O"), tracker.Details["trailingGapFromUtc"]);
        Assert.Equal("1020", tracker.Details["trailingGapMinutes"]);
        Assert.Equal("False", tracker.Details["coverageEmpty"]);
    }

    [Fact]
    public async Task GetQualityAsync_RangeWithNoDataAtAllReportsTheWholeRangeAsMissing()
    {
        await using var db = CreateDb();
        // 整段查询范围没有任何事件与采样：不应被读成「没有尾部空白」。
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        Assert.Equal("True", tracker.Details["coverageEmpty"]);
        Assert.Equal("1440", tracker.Details["trailingGapMinutes"]);
        Assert.Contains(result.Issues, i => i.Code == "missing-tracker-events");
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

    private static void AddAwEvent(PimDbContext db, DateTimeOffset timestamp, double duration)
        => db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "DESKTOP",
            Timestamp = timestamp,
            Duration = duration,
            EventType = "window",
            AppName = "Code",
            BucketId = "aw-watcher-window_DESKTOP",
            BucketType = "currentwindow",
            DataJson = "{}"
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
