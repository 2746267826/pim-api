using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// 工单 01 工作项 A：报告必须**按各自周期真取数**并给出**结论**，而不是全量账本。
///
/// <para>
/// 周期口径（本工单开工前经维护者确认）：Asia/Shanghai <b>业务日</b>，半开区间
/// <c>[D 04:00, D+1 04:00)</c>；周报 = 周一 04:00 → 下周一 04:00；月报 = 当月 1 日 04:00 → 次月 1 日 04:00。
/// 这与工单 A2.1 写的「周一 00:00~周日 24:00」不同 —— 维护者确认以仓库既有 BusinessDay/D-1 口径为准，
/// AC-A2 的边界样本相应改用 04:00 边界（见 <see cref="WeeklyReport_UsesBusinessDayBoundaryAt0400"/>）。
/// </para>
///
/// <para>
/// 期望值在测试里独立换算（直接写 +08:00 的 UTC 时刻），不复用被测代码的换算工具，
/// 避免"用实现证明实现"。
/// </para>
/// </summary>
public class ReportPeriodScopeTests
{
    private static readonly Guid UserId = Guid.Parse("90909090-9090-9090-9090-090909090909");

    /// <summary>无数据模块的显式标记（A2.4：不填 0、不省略）。</summary>
    private const string NoData = "本周期无数据";

    private static readonly string[] NoDataLiteral = [NoData];

    /// <summary>把 Asia/Shanghai（+08:00，无夏令时）本地时刻换算成 UTC 绝对时刻。</summary>
    private static DateTimeOffset Sh(int year, int month, int day, int hour, int minute = 0, int second = 0)
        => new DateTimeOffset(new DateTime(year, month, day, hour, minute, second), TimeSpan.FromHours(8)).ToUniversalTime();

    // ---- AC-A1：同一天日报 vs 周报数字必须不同；不同周的周报数字必须不同 ----

    [Fact]
    public async Task DailyReport_CountsOnlyItsOwnBusinessDay()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedTask(db, calendar, "T-0805", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 11), TimeSpan.FromMinutes(120));
        SeedTask(db, calendar, "T-0806", Sh(2026, 8, 6, 9), Sh(2026, 8, 6, 12), TimeSpan.FromMinutes(180));
        SeedEvent(db, calendar, Sh(2026, 8, 5, 10), Sh(2026, 8, 5, 12));
        SeedEvent(db, calendar, Sh(2026, 8, 6, 10), Sh(2026, 8, 6, 11));
        await db.SaveChangesAsync();

        var today = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));
        var tomorrow = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 6));

        Assert.Equal(1, today.GetProperty("tasks").GetInt32());
        Assert.Equal(120, today.GetProperty("plannedEstimatedMinutes").GetInt32());
        Assert.Equal(120, today.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(1, today.GetProperty("calendarActiveDays").GetInt32());

        Assert.Equal(1, tomorrow.GetProperty("tasks").GetInt32());
        Assert.Equal(180, tomorrow.GetProperty("plannedEstimatedMinutes").GetInt32());
        Assert.Equal(60, tomorrow.GetProperty("calendarOccupiedMinutes").GetInt32());
    }

    [Fact]
    public async Task DailyAndWeeklyReports_ForTheSameDate_ProduceDifferentNumbers()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedEvent(db, calendar, Sh(2026, 8, 3, 9), Sh(2026, 8, 3, 10));   // 周一，本业务日之外
        SeedEvent(db, calendar, Sh(2026, 8, 5, 10), Sh(2026, 8, 5, 12));  // 周三（请求日）
        SeedEvent(db, calendar, Sh(2026, 8, 9, 10), Sh(2026, 8, 9, 11));  // 周日，仍在本周
        await db.SaveChangesAsync();

        var daily = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));
        var weekly = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 5));

        Assert.Equal(120, daily.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(1, daily.GetProperty("calendarActiveDays").GetInt32());

        Assert.Equal(240, weekly.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(3, weekly.GetProperty("calendarActiveDays").GetInt32());

        // 核心断言：旧的实现在这里三者完全相同
        Assert.NotEqual(
            daily.GetProperty("calendarOccupiedMinutes").GetInt32(),
            weekly.GetProperty("calendarOccupiedMinutes").GetInt32());
    }

    [Fact]
    public async Task WeeklyReports_ForDifferentWeeks_ProduceDifferentNumbers()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedEvent(db, calendar, Sh(2026, 8, 5, 10), Sh(2026, 8, 5, 12));   // 第 32 周 = 120 分钟
        SeedEvent(db, calendar, Sh(2026, 8, 12, 10), Sh(2026, 8, 12, 11)); // 第 33 周 = 60 分钟
        await db.SaveChangesAsync();

        var week32 = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 5));
        var week33 = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 12));

        Assert.Equal(120, week32.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(60, week33.GetProperty("calendarOccupiedMinutes").GetInt32());
    }

    [Fact]
    public async Task MonthlyReport_CoversTheWholeBusinessMonth()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedEvent(db, calendar, Sh(2026, 7, 31, 23, 0), Sh(2026, 8, 1, 0, 0));  // 7 月业务日
        SeedEvent(db, calendar, Sh(2026, 8, 5, 10), Sh(2026, 8, 5, 12));        // 8 月业务日
        SeedEvent(db, calendar, Sh(2026, 8, 31, 23, 0), Sh(2026, 9, 1, 0, 0));  // 8 月最后一个业务日
        await db.SaveChangesAsync();

        var august = await MetricsAsync(db, "Monthly", new DateOnly(2026, 8, 5));

        Assert.Equal(180, august.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(2, august.GetProperty("calendarActiveDays").GetInt32());
    }

    // ---- AC-A2：周期边界（本工单确认的 04:00 业务日边界） ----

    [Fact]
    public async Task WeeklyReport_UsesBusinessDayBoundaryAt0400()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 周一 03:59:59 → 仍属上一周（业务日 = 上周日）
        SeedEvent(db, calendar, Sh(2026, 8, 3, 3, 59, 59), Sh(2026, 8, 3, 4, 30, 0));
        // 周一 04:00:01 → 新一周的第一条
        SeedEvent(db, calendar, Sh(2026, 8, 3, 4, 0, 1), Sh(2026, 8, 3, 5, 0, 0));
        // 周日 23:59:59 → 本周最后一条
        SeedEvent(db, calendar, Sh(2026, 8, 9, 23, 59, 59), Sh(2026, 8, 10, 0, 30, 0));
        // 下周一 04:00:01 → 下一周
        SeedEvent(db, calendar, Sh(2026, 8, 10, 4, 0, 1), Sh(2026, 8, 10, 5, 0, 0));
        await db.SaveChangesAsync();

        var thisWeek = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 5));
        var previousWeek = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 2));
        var nextWeek = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 10));

        // 本周 = [周一 04:00, 下周一 04:00)：只有 04:00:01 与周日 23:59:59 两条
        Assert.Equal(2, thisWeek.GetProperty("calendarActiveDays").GetInt32());
        // 上一周拿到周一 03:59:59 那条
        Assert.Equal(1, previousWeek.GetProperty("calendarActiveDays").GetInt32());
        // 下一周拿到下周一 04:00:01 那条
        Assert.Equal(1, nextWeek.GetProperty("calendarActiveDays").GetInt32());

        // 周一 04:00 前后分属不同周（AC-A2 的核心判据）
        Assert.Equal(2, thisWeek.GetProperty("calendarActiveDays").GetInt32());
        Assert.Equal(
            Sh(2026, 8, 3, 4, 0, 1),
            await FirstEventStartInReportAsync(db, "Weekly", new DateOnly(2026, 8, 5)));
    }

    [Fact]
    public async Task DailyReport_WindowIsWrittenIntoTheReportContent()
    {
        await using var db = CreateDb();
        await SeedCalendarAsync(db);
        await db.SaveChangesAsync();

        var report = await GenerateAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Contains("2026-08-05 04:00", report.ContentMarkdown);
        Assert.Contains("2026-08-06 04:00", report.ContentMarkdown);
    }

    // ---- AC-A4：取不到数据的模块必须显式标注，不填 0、不省略 ----

    [Fact]
    public async Task ReportWithoutExecutionSegments_MarksActualExecutionAsNoData()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedTask(db, calendar, "T-planned", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 11), TimeSpan.FromMinutes(120));
        await db.SaveChangesAsync();

        var report = await GenerateAsync(db, "Daily", new DateOnly(2026, 8, 5));
        var metrics = JsonDocument.Parse(report.MetricsJson).RootElement;

        // 有计时段落的表当前为空（工单 03 才接写入方）：必须显式无数据，而不是 0
        Assert.Equal(NoData, metrics.GetProperty("actualExecutedMinutes").GetString());
        Assert.Contains("## 计划 vs 实际", report.ContentMarkdown);
        Assert.Contains(NoData, report.ContentMarkdown);
    }

    [Fact]
    public async Task ReportForEmptyPeriod_MarksEveryModuleWithoutData()
    {
        await using var db = CreateDb();
        await SeedCalendarAsync(db);
        await db.SaveChangesAsync();

        var report = await GenerateAsync(db, "Daily", new DateOnly(2026, 8, 5));
        var metrics = JsonDocument.Parse(report.MetricsJson).RootElement;

        Assert.Equal(NoData, metrics.GetProperty("plannedEstimatedMinutes").GetString());
        Assert.Equal(NoData, metrics.GetProperty("actualExecutedMinutes").GetString());
        Assert.Equal(NoData, metrics.GetProperty("mobileUsageEvents").GetString());
        Assert.Equal(NoData, metrics.GetProperty("pcEvents").GetString());
        // 日历占用与任务进展同样不能给一个看不出真假的 0（§A2.4）
        Assert.Equal(NoData, metrics.GetProperty("calendarOccupiedMinutes").GetString());
        Assert.Equal(NoData, metrics.GetProperty("calendarActiveDays").GetString());
        Assert.Equal(NoData, metrics.GetProperty("tasksCreated").GetString());
        Assert.Equal(NoData, metrics.GetProperty("tasksCompleted").GetString());
        Assert.Equal(NoData, metrics.GetProperty("tasksOverdue").GetString());
        Assert.Contains("本周期无数据：该周期内没有任何任务、日程、计时段落或采集数据。", report.ContentMarkdown);
        Assert.Contains("## 收集质量", report.ContentMarkdown);
    }

    [Fact]
    public async Task ReportForPeriodWithFacts_KeepsGenuineZeroesAsNumbers()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 周期内有事实（一条日程），但当天没新增/完成/逾期任务 → 这些 0 是真实统计值，不是无数据
        SeedEvent(db, calendar, Sh(2026, 8, 5, 10), Sh(2026, 8, 5, 11));
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Equal(60, metrics.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(1, metrics.GetProperty("calendarActiveDays").GetInt32());
        Assert.Equal(0, metrics.GetProperty("tasksCreated").GetInt32());
        Assert.Equal(0, metrics.GetProperty("tasksCompleted").GetInt32());
        Assert.Equal(0, metrics.GetProperty("tasksOverdue").GetInt32());
    }

    // ---- 跨周期边界的排期窗口（复审 Important：起点在周期之前、排期伸进周期） ----

    [Fact]
    public async Task ScheduledWindow_CrossingThePeriodStart_IsClippedAndCountedInBothPeriods()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 业务日 2026-08-05 起点 = 04:00+08：该任务 03:00 → 05:00，跨边界，两天各占 1 小时
        SeedTask(db, calendar, "T-cross", Sh(2026, 8, 5, 3), Sh(2026, 8, 5, 5), TimeSpan.FromMinutes(120));
        await db.SaveChangesAsync();

        var day5 = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));
        var day4 = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 4));

        Assert.Equal(1, day5.GetProperty("tasks").GetInt32());
        Assert.Equal(60, day5.GetProperty("plannedScheduledMinutes").GetInt32());
        Assert.Equal(1, day4.GetProperty("tasks").GetInt32());
        Assert.Equal(60, day4.GetProperty("plannedScheduledMinutes").GetInt32());
    }

    [Fact]
    public async Task TaskWithoutPlannedEnd_IsAPointEvent_AndDoesNotLeakIntoLaterPeriods()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 只排了起点、没有计划结束：它只属于 2026-08-05 这一个业务日
        var start = Sh(2026, 8, 5, 9);
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "point", Title = "point",
            DtStart = start, PlannedEnd = null, EstimatedDuration = TimeSpan.FromMinutes(120),
            CreatedAt = start
        });
        await db.SaveChangesAsync();

        var sameDay = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));
        var sameWeek = await MetricsAsync(db, "Weekly", new DateOnly(2026, 8, 5));
        var muchLater = await MetricsAsync(db, "Daily", new DateOnly(2027, 1, 1));
        var nextMonth = await MetricsAsync(db, "Monthly", new DateOnly(2026, 9, 1));

        Assert.Equal(1, sameDay.GetProperty("tasks").GetInt32());
        Assert.Equal(1, sameWeek.GetProperty("tasks").GetInt32());
        // 没有计划结束的任务不该在之后任何周期里重复出现
        Assert.Equal(NoData, muchLater.GetProperty("tasksCreated").GetString());
        Assert.Equal(0, nextMonth.GetProperty("tasks").GetInt32());
    }

    // ---- 逾期口径（复审 Important：到期后才完成也属于"未按期完成"） ----

    [Fact]
    public async Task Overdue_CountsTasksCompletedLate_ButNotThoseCompletedOnTime()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 到期后一小时才完成 → 逾期
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "late-done", Title = "late-done",
            Status = "COMPLETED", Due = Sh(2026, 8, 5, 18), CompletedAt = Sh(2026, 8, 5, 19),
            CreatedAt = Sh(2026, 8, 1, 9)
        });
        // 到期前完成 → 不逾期
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "on-time", Title = "on-time",
            Status = "COMPLETED", Due = Sh(2026, 8, 5, 18), CompletedAt = Sh(2026, 8, 5, 17),
            CreatedAt = Sh(2026, 8, 1, 9)
        });
        // 已取消 → 不计逾期
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "cancelled", Title = "cancelled",
            Status = "CANCELLED", Due = Sh(2026, 8, 5, 18), CreatedAt = Sh(2026, 8, 1, 9)
        });
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Equal(1, metrics.GetProperty("tasksOverdue").GetInt32());
        // 周期内完成两次（一次按期、一次晚完成）——两个都是真实的完成次数
        Assert.Equal(2, metrics.GetProperty("tasksCompleted").GetInt32());
    }

    // ---- AC-A5：metricsJson 必须与 reports-page.tsx 既有解析方式兼容 ----

    [Fact]
    public async Task MetricsJson_IsFlatJsonParsableLikeTheReportsPage()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedTask(db, calendar, "T-1", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 10), TimeSpan.FromMinutes(60), status: "COMPLETED");
        SeedEvent(db, calendar, Sh(2026, 8, 5, 10), Sh(2026, 8, 5, 11));
        await db.SaveChangesAsync();

        var report = await GenerateAsync(db, "Daily", new DateOnly(2026, 8, 5));

        // reports-page.tsx: JSON.parse(selected.metricsJson) as Record<string, number>
        using var document = JsonDocument.Parse(report.MetricsJson);
        var metrics = document.RootElement;
        Assert.Equal(JsonValueKind.Object, metrics.ValueKind);

        // 漏斗 / 仪表盘读的就是这两个键，必须是数字（旧前端 Number(metrics?.tasks ?? 0)）
        Assert.Equal(JsonValueKind.Number, metrics.GetProperty("tasks").ValueKind);
        Assert.Equal(JsonValueKind.Number, metrics.GetProperty("completedTasks").ValueKind);
        Assert.Equal(1, metrics.GetProperty("tasks").GetInt32());
        Assert.Equal(1, metrics.GetProperty("completedTasks").GetInt32());

        // 报告各模块的结论必须都出现在这个扁平映射里（A2.2：模块不能缺项）
        foreach (var key in new[]
        {
            "tasks", "completedTasks",
            "plannedEstimatedMinutes", "plannedScheduledMinutes", "actualExecutedMinutes",
            "calendarOccupiedMinutes", "calendarActiveDays",
            "tasksCreated", "tasksCompleted", "tasksOverdue",
            "mobileUsageEvents", "mobileLocationPoints", "pcEvents", "pcMissingHours"
        })
        {
            Assert.True(metrics.TryGetProperty(key, out _), $"metricsJson 缺少模块字段 {key}");
        }

        // 其余键：数字，或"本周期无数据"这一显式标记 —— 不出现嵌套对象（String(v) 会渲染成 [object Object]）
        foreach (var property in metrics.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                continue;
            }

            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            Assert.Contains(property.Value.GetString(), NoDataLiteral);
        }
    }

    // ---- AC-A6：Kind=Project 的项目报告同样按周期取数 ----

    [Fact]
    public async Task ProjectReport_ScopesToTheProjectTaskSpan()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        var project = new DomainProjectEntity { UserId = UserId, Name = "P" };
        db.Set<DomainProjectEntity>().Add(project);
        SeedTask(db, calendar, "P-1", Sh(2026, 8, 4, 9), Sh(2026, 8, 4, 11), TimeSpan.FromMinutes(120), projectId: project.Id);
        SeedTask(db, calendar, "P-2", Sh(2026, 8, 6, 9), Sh(2026, 8, 6, 12), TimeSpan.FromMinutes(180), projectId: project.Id);
        // 项目外任务与项目外日程：都不该进入项目报告
        SeedTask(db, calendar, "X-1", Sh(2026, 8, 20, 9), Sh(2026, 8, 20, 10), TimeSpan.FromMinutes(60));
        SeedEvent(db, calendar, Sh(2026, 8, 4, 14), Sh(2026, 8, 4, 15));   // 项目跨度内
        SeedEvent(db, calendar, Sh(2026, 8, 20, 14), Sh(2026, 8, 20, 17)); // 跨度外
        await db.SaveChangesAsync();

        var report = await GenerateAsync(db, "Project", new DateOnly(2026, 8, 5), project.Id);
        var metrics = JsonDocument.Parse(report.MetricsJson).RootElement;

        Assert.Equal(2, metrics.GetProperty("tasks").GetInt32());
        Assert.Equal(300, metrics.GetProperty("plannedEstimatedMinutes").GetInt32());
        Assert.Equal(60, metrics.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(project.Id, report.ProjectId);
    }

    [Fact]
    public async Task ProjectReport_WithoutProjectId_FailsInsteadOfFakingNumbers()
    {
        await using var db = CreateDb();
        await SeedCalendarAsync(db);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() => service.GenerateAsync(
            new GenerateReportRequest("Project", new DateOnly(2026, 8, 5), null), CancellationToken.None));
    }

    [Fact]
    public async Task ProjectReport_ForUnknownProject_FailsInsteadOfFakingNumbers()
    {
        await using var db = CreateDb();
        await SeedCalendarAsync(db);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() => service.GenerateAsync(
            new GenerateReportRequest("Project", new DateOnly(2026, 8, 5), Guid.NewGuid()), CancellationToken.None));
    }

    // ---- AC-A3：真的落库，且 GET 取回同一份内容 ----

    [Fact]
    public async Task GeneratedReport_IsPersistedAndReadBackIdentically()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        SeedTask(db, calendar, "T-1", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 10), TimeSpan.FromMinutes(60));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var generated = await service.GenerateAsync(
            new GenerateReportRequest("Daily", new DateOnly(2026, 8, 5), null), CancellationToken.None);

        Assert.Equal(1, await db.Set<ReportArtifactEntity>().CountAsync());
        var stored = await db.Set<ReportArtifactEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(generated.ContentMarkdown, stored.ContentMarkdown);
        Assert.Equal(generated.MetricsJson, stored.MetricsJson);

        var fetched = await service.GetAsync(generated.Id, CancellationToken.None);
        Assert.Equal(generated.ContentMarkdown, fetched.ContentMarkdown);
        Assert.Equal(generated.MetricsJson, fetched.MetricsJson);
    }

    // ---- 计划 / 日历占用 / 任务进展的口径 ----

    [Fact]
    public async Task PlanVersusActual_UsesEstimateScheduledWindowAndExecutionSegments()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        var task = SeedTask(db, calendar, "T-1", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 11), TimeSpan.FromMinutes(120));
        db.Set<TaskExecutionSegmentEntity>().Add(new TaskExecutionSegmentEntity
        {
            TaskId = task.Id,
            UserId = UserId,
            StartsAt = Sh(2026, 8, 5, 9),
            EndsAt = Sh(2026, 8, 5, 10),
            Status = "active"
        });
        // 未开始的计划段落不计入「实际执行」，但要计入「已排入日程」
        db.Set<TaskExecutionSegmentEntity>().Add(new TaskExecutionSegmentEntity
        {
            TaskId = task.Id,
            UserId = UserId,
            StartsAt = Sh(2026, 8, 5, 11),
            EndsAt = Sh(2026, 8, 5, 12),
            Status = "planned"
        });
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Equal(120, metrics.GetProperty("plannedEstimatedMinutes").GetInt32());
        Assert.Equal(120, metrics.GetProperty("plannedScheduledMinutes").GetInt32());
        Assert.Equal(60, metrics.GetProperty("actualExecutedMinutes").GetInt32());
    }

    [Fact]
    public async Task ActualExecutionTime_KeepsWorkBeforeCancellationAndIgnoresDeletedSegments()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        var task = SeedTask(db, calendar, "T-1", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 11), TimeSpan.FromMinutes(120));
        db.Set<TaskExecutionSegmentEntity>().Add(new TaskExecutionSegmentEntity
        {
            TaskId = task.Id, UserId = UserId,
            StartsAt = Sh(2026, 8, 5, 9), EndsAt = Sh(2026, 8, 5, 10), Status = "completed"
        });
        db.Set<TaskExecutionSegmentEntity>().Add(new TaskExecutionSegmentEntity
        {
            TaskId = task.Id, UserId = UserId,
            StartsAt = Sh(2026, 8, 5, 10), EndsAt = Sh(2026, 8, 5, 11), Status = "cancelled"
        });
        db.Set<TaskExecutionSegmentEntity>().Add(new TaskExecutionSegmentEntity
        {
            TaskId = task.Id, UserId = UserId,
            StartsAt = Sh(2026, 8, 5, 11), EndsAt = Sh(2026, 8, 5, 12), Status = "completed",
            DeletedAt = Sh(2026, 8, 6, 0)
        });
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Equal(120, metrics.GetProperty("actualExecutedMinutes").GetInt32());
    }

    [Fact]
    public async Task CalendarOccupancy_ClipsToTheWindowAndSkipsCancelledEvents()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 起点在周期之前、伸进周期：只算重叠部分
        SeedEvent(db, calendar, Sh(2026, 8, 4, 22), Sh(2026, 8, 5, 6));
        SeedEvent(db, calendar, Sh(2026, 8, 5, 12), Sh(2026, 8, 5, 13), status: "CANCELLED");
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Equal(120, metrics.GetProperty("calendarOccupiedMinutes").GetInt32());
        Assert.Equal(1, metrics.GetProperty("calendarActiveDays").GetInt32());
    }

    [Fact]
    public async Task TaskProgress_CountsCreatedCompletedAndOverdueWithinThePeriod()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        // 本周期内新增
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "created", Title = "created",
            DtStart = Sh(2026, 8, 5, 9), PlannedEnd = Sh(2026, 8, 5, 10),
            CreatedAt = Sh(2026, 8, 5, 8)
        });
        // 本周期内完成
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "done", Title = "done",
            DtStart = Sh(2026, 8, 5, 9), PlannedEnd = Sh(2026, 8, 5, 10),
            Status = "COMPLETED", CompletedAt = Sh(2026, 8, 5, 11)
        });
        // 本周期内到期但没做完
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "late", Title = "late",
            DtStart = Sh(2026, 8, 5, 9), PlannedEnd = Sh(2026, 8, 5, 10),
            Due = Sh(2026, 8, 5, 18)
        });
        // 上周期到期、已做完：不计逾期
        db.Set<TaskEntity>().Add(new TaskEntity
        {
            UserId = UserId, CalendarId = calendar.Id, Uid = "old", Title = "old",
            Due = Sh(2026, 8, 1, 18), Status = "COMPLETED", CompletedAt = Sh(2026, 8, 1, 19)
        });
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync(db, "Daily", new DateOnly(2026, 8, 5));

        Assert.Equal(1, metrics.GetProperty("tasksCreated").GetInt32());
        Assert.Equal(1, metrics.GetProperty("tasksCompleted").GetInt32());
        Assert.Equal(1, metrics.GetProperty("tasksOverdue").GetInt32());
    }

    // ---- helpers ----

    [Theory]
    [InlineData(false, false, false, 10)]
    [InlineData(true, true, false, 120)]
    [InlineData(true, false, true, 10)]
    public async Task ProjectReport_IncludesExecutionOutsideTaskPlanningFields(
        bool scheduled, bool open, bool beforePlan, int expectedMinutes)
    {
        await using var db = CreateDb();
        var project = new DomainProjectEntity { UserId = UserId, Name = "Execution span" };
        var task = new TaskEntity
        {
            UserId = UserId, DomainProjectId = project.Id, Uid = "timed-project-task", Title = "Timed task",
            CreatedAt = Sh(2026, 8, 5, 8),
            DtStart = scheduled ? Sh(2026, 8, 5, beforePlan ? 10 : 9) : null,
            PlannedEnd = scheduled ? Sh(2026, 8, 5, beforePlan ? 11 : 10) : null
        };
        var segment = new TaskExecutionSegmentEntity
        {
            TaskId = task.Id, UserId = UserId, StartsAt = Sh(2026, 8, 5, 9),
            EndsAt = open ? null : Sh(2026, 8, 5, 9, 10), Status = open ? "active" : "completed"
        };
        // An unstarted plan must not stretch the project period.
        var planned = new TaskExecutionSegmentEntity
        {
            TaskId = task.Id, UserId = UserId, StartsAt = Sh(2026, 9, 1, 9),
            EndsAt = Sh(2026, 9, 1, 18), Status = "planned"
        };
        db.AddRange(project, task, segment, planned);
        await db.SaveChangesAsync();
        var clock = new FixedTimeProvider(Sh(2026, 8, 5, 11));
        var report = await CreateService(db, clock).GenerateAsync(
            new GenerateReportRequest("Project", new DateOnly(2026, 8, 5), project.Id));
        using var metrics = JsonDocument.Parse(report.MetricsJson);

        Assert.Equal(expectedMinutes, metrics.RootElement.GetProperty("actualExecutedMinutes").GetInt32());
        Assert.Equal(expectedMinutes * 60L, PlanningModelService.AccumulatedSeconds([segment], clock.GetUtcNow()));
        Assert.Equal(1, metrics.RootElement.GetProperty("tasksCreated").GetInt32());
        Assert.DoesNotContain("2026-09-01", report.ContentMarkdown);
    }

    /// <summary>
    /// A4（工单 03 复审）：正在计时的**开放段**（ends_at 为 null）必须按**注入时钟**计入「实际执行」，
    /// 而不是墙钟「现在」。否则同一条在跑的段会因为今天是不是报告日而算出完全不同的数字
    /// （例如 09:00 开始、现在已是数周之后 → 会被算成 19 小时，而不是真的 30 分钟）。
    /// </summary>
    [Fact]
    public async Task OpenSegment_IsCountedUpToTheInjectedClock()
    {
        await using var db = CreateDb();
        var calendar = await SeedCalendarAsync(db);
        var task = SeedTask(db, calendar, "T-open", Sh(2026, 8, 5, 9), Sh(2026, 8, 5, 11), TimeSpan.FromMinutes(120));
        db.Set<TaskExecutionSegmentEntity>().Add(new TaskExecutionSegmentEntity
        {
            TaskId = task.Id,
            UserId = UserId,
            StartsAt = Sh(2026, 8, 5, 9),
            EndsAt = null,                      // 正在计时（工单 03 的开放段）
            Status = "active"
        });
        await db.SaveChangesAsync();

        var clock = new FixedTimeProvider(Sh(2026, 8, 5, 9, 30));
        var report = await CreateService(db, clock).GenerateAsync(
            new GenerateReportRequest("Daily", new DateOnly(2026, 8, 5), null),
            CancellationToken.None);
        var metrics = JsonDocument.Parse(report.MetricsJson).RootElement;

        // 固定时钟 09:30 − 段起点 09:00 = 30 分钟
        Assert.Equal(30, metrics.GetProperty("actualExecutedMinutes").GetInt32());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static async Task<JsonElement> MetricsAsync(PimDbContext db, string kind, DateOnly date, Guid? projectId = null)
    {
        var report = await GenerateAsync(db, kind, date, projectId);
        return JsonDocument.Parse(report.MetricsJson).RootElement.Clone();
    }

    private static async Task<ReportArtifactDto> GenerateAsync(PimDbContext db, string kind, DateOnly date, Guid? projectId = null)
        => await CreateService(db).GenerateAsync(new GenerateReportRequest(kind, date, projectId), CancellationToken.None);

    /// <summary>报告窗口内第一条日程的起点（用来钉住边界落在哪一侧）。</summary>
    private static async Task<DateTimeOffset> FirstEventStartInReportAsync(PimDbContext db, string kind, DateOnly date)
    {
        // 直接按口径算窗口，而不是读实现：日报/周报/月报都对齐业务日 04:00
        var (start, _) = kind switch
        {
            "Weekly" => (Sh(2026, 8, 3, 4), Sh(2026, 8, 10, 4)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        _ = date;
        return await db.Set<EventEntity>()
            .Where(e => e.DtStart >= start)
            .OrderBy(e => e.DtStart)
            .Select(e => e.DtStart)
            .FirstAsync();
    }

    private static ReportService CreateService(PimDbContext db)
        => CreateService(db, TimeProvider.System);

    private static ReportService CreateService(PimDbContext db, TimeProvider timeProvider)
        => new(db, new FixedCurrentUserService(UserId), new OperationConfirmationService(db), null, timeProvider);

    private static async Task<CalendarEntity> SeedCalendarAsync(PimDbContext db)
    {
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    private static TaskEntity SeedTask(
        PimDbContext db,
        CalendarEntity calendar,
        string uid,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan estimate,
        Guid? projectId = null,
        string status = "NEEDS-ACTION")
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            CalendarId = calendar.Id,
            DomainProjectId = projectId,
            Uid = uid,
            Title = uid,
            DtStart = start,
            PlannedEnd = end,
            EstimatedDuration = estimate,
            Status = status,
            CreatedAt = start.AddDays(-1)
        };
        db.Set<TaskEntity>().Add(task);
        return task;
    }

    private static void SeedEvent(PimDbContext db, CalendarEntity calendar, DateTimeOffset start, DateTimeOffset end, string status = "CONFIRMED")
        => db.Set<EventEntity>().Add(new EventEntity
        {
            CalendarId = calendar.Id,
            Uid = Guid.NewGuid().ToString("N"),
            Title = "event",
            DtStart = start,
            DtEnd = end,
            Status = status
        });

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReportArtifactEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"report-period-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
