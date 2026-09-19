using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Today;
using Pim.Core.Operations;
using Pim.Core.Today;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.Today;

public class TodaySectionProviderTests
{
    [Fact]
    public async Task CalendarScheduleProvider_ReturnsEventsAndScheduledTasks()
    {
        var (db, userId) = CreateDb();
        var calendarService = CreateCalendarService(db, userId);
        var calendar = await calendarService.CreateCalendarAsync(
            new CreateCalendarRequest("Work", "#3B82F6"),
            CancellationToken.None);
        var start = new DateTimeOffset(2026, 5, 25, 9, 0, 0, TimeSpan.Zero);
        var createdEvent = await calendarService.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "Standup", null, null, start, start.AddMinutes(30), null),
            CancellationToken.None);
        var createdTask = await calendarService.CreateTaskAsync(
            new CreateTaskRequest(calendar.Id, "Scheduled task", null, 0, null, null, null, start.AddHours(1)),
            CancellationToken.None);
        var provider = new CalendarScheduleTodaySectionProvider(calendarService);

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal("calendar.schedule", section.Id);
        Assert.Equal(TodaySectionStatuses.Normal, section.Status);
        var data = Assert.IsType<CalendarScheduleTodayData>(section.Data);
        Assert.Contains(data.Events, e => e.Id == createdEvent.Id);
        Assert.Contains(data.ScheduledTasks, t => t.Id == createdTask.Id);
    }

    [Fact]
    public async Task CalendarScheduleProvider_UsesShanghaiDayWindow()
    {
        var (db, userId) = CreateDb();
        var calendarService = CreateCalendarService(db, userId);
        var calendar = await calendarService.CreateCalendarAsync(
            new CreateCalendarRequest("Work", "#3B82F6"),
            CancellationToken.None);
        // 2026-05-25 00:30 / 2026-05-26 00:30（Asia/Shanghai），与测试机器时区无关。
        var earlyToday = new DateTimeOffset(2026, 5, 24, 16, 30, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2026, 5, 25, 16, 30, 0, TimeSpan.Zero);
        var includedEvent = await calendarService.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "Early today", null, null, earlyToday, earlyToday.AddMinutes(30), null),
            CancellationToken.None);
        var excludedEvent = await calendarService.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "Next day", null, null, nextDay, nextDay.AddMinutes(30), null),
            CancellationToken.None);
        var provider = new CalendarScheduleTodaySectionProvider(calendarService);

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        var data = Assert.IsType<CalendarScheduleTodayData>(section.Data);
        Assert.Contains(data.Events, e => e.Id == includedEvent.Id);
        Assert.DoesNotContain(data.Events, e => e.Id == excludedEvent.Id);
    }

    [Fact]
    public async Task CalendarScheduleProvider_NormalizesDayWindowToUtc()
    {
        var (db, userId) = CreateDb();
        var calendarService = new WindowCapturingCalendarService(db, userId);
        var provider = new CalendarScheduleTodaySectionProvider(calendarService);

        await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(1, calendarService.CallCount);
        // Npgsql 仅接受 offset 0（UTC）的 DateTimeOffset 参数（issue #313）。
        Assert.Equal(TimeSpan.Zero, calendarService.CapturedStart.Offset);
        Assert.Equal(TimeSpan.Zero, calendarService.CapturedEnd.Offset);
        // 2026-05-25 的上海当日窗口：[2026-05-24 16:00Z, 2026-05-25 16:00Z)。
        Assert.Equal(new DateTimeOffset(2026, 5, 24, 16, 0, 0, TimeSpan.Zero), calendarService.CapturedStart);
        Assert.Equal(new DateTimeOffset(2026, 5, 25, 16, 0, 0, TimeSpan.Zero), calendarService.CapturedEnd);
    }

    [Fact]
    public void CalendarHabitsLayerQuery_NormalizesDayWindowToUtc()
    {
        var query = new TodayQuery(new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 25));

        var layerQuery = CalendarHabitsTodaySectionProvider.LayerQuery(query, "habits");

        Assert.Equal(["habits"], layerQuery.Layers);
        Assert.Equal(TimeSpan.Zero, layerQuery.Start.Offset);
        Assert.Equal(TimeSpan.Zero, layerQuery.End.Offset);
        Assert.Equal(new DateTimeOffset(2026, 5, 24, 16, 0, 0, TimeSpan.Zero), layerQuery.Start);
        Assert.Equal(new DateTimeOffset(2026, 5, 25, 16, 0, 0, TimeSpan.Zero), layerQuery.End);
    }

    [Fact]
    public async Task CalendarScheduleProvider_ExcludesCompletedScheduledTasks()
    {
        var (db, userId) = CreateDb();
        var calendarService = CreateCalendarService(db, userId);
        var start = new DateTimeOffset(2026, 5, 25, 9, 0, 0, TimeSpan.Zero);
        var createdTask = await calendarService.CreateTaskAsync(
            new CreateTaskRequest(null, "Done scheduled task", null, 0, null, null, null, start),
            CancellationToken.None);
        await calendarService.UpdateTaskAsync(
            createdTask.Id,
            new UpdateTaskRequest(
                createdTask.CalendarId,
                createdTask.Title,
                createdTask.Description,
                createdTask.Priority,
                createdTask.EstimatedDuration,
                createdTask.MinimumSegment,
                createdTask.Due,
                createdTask.DtStart,
                "COMPLETED"),
            CancellationToken.None);
        var provider = new CalendarScheduleTodaySectionProvider(calendarService);

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(TodaySectionStatuses.Empty, section.Status);
        var data = Assert.IsType<CalendarScheduleTodayData>(section.Data);
        Assert.Empty(data.ScheduledTasks);
    }

    [Fact]
    public async Task CalendarTasksProvider_ReturnsWarning_WhenOverdueTasksExist()
    {
        var (db, userId) = CreateDb();
        var calendarService = CreateCalendarService(db, userId);
        await calendarService.CreateTaskAsync(
            new CreateTaskRequest(null, "Overdue task", null, 0, null, null, new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero), null),
            CancellationToken.None);
        var provider = new CalendarTasksTodaySectionProvider(calendarService);

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(TodaySectionStatuses.Warning, section.Status);
        var data = Assert.IsType<CalendarTasksTodayData>(section.Data);
        Assert.Single(data.OverdueTasks);
        Assert.Equal(1, data.IncompleteCount);
    }

    [Fact]
    public async Task PcQualityProvider_UsesQualityService()
    {
        var (db, _) = CreateDb(registerPc: true);
        var provider = new PcQualityTodaySectionProvider(new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero) }));

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal("pc.quality", section.Id);
        var data = Assert.IsType<PcQualityTodayData>(section.Data);
        Assert.True(data.IssueCount >= 1);
        Assert.Contains(section.Links, link => link.Href == "/pc-tracker");
    }

    [Fact]
    public async Task PcActivityProvider_ReturnsEmpty_WhenNoPcDataExists()
    {
        var (db, _) = CreateDb(registerPc: true);
        var provider = new PcActivityTodaySectionProvider(CreatePcTrackerService(db));

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(TodaySectionStatuses.Empty, section.Status);
        Assert.IsType<PcActivityTodayData>(section.Data);
    }

    [Fact]
    public async Task OperationsHealthProvider_ReturnsHealthSummary()
    {
        var status = new FakeSystemStatusService(PimHealthStatus.Warning);
        var provider = new OperationsHealthTodaySectionProvider(status);

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(TodaySectionStatuses.Warning, section.Status);
        var data = Assert.IsType<OperationsHealthTodayData>(section.Data);
        Assert.Equal(PimHealthStatus.Warning, data.Summary.Status);
        Assert.Contains(section.Links, link => link.Href == "/status");
    }

    [Fact]
    public async Task ClassificationSuggestionsProvider_ReturnsWarning_WhenPendingSuggestionsExist()
    {
        var (db, _) = CreateDb(registerPc: true);
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.NewGuid(),
            ClusterKey = "app:unknown",
            SampleCount = 1,
            TotalDurationSeconds = 120,
            Status = "pending"
        });
        await db.SaveChangesAsync();
        var provider = new ClassificationSuggestionsTodaySectionProvider(
            new ActivitySuggestionService(db, new AppSignatureService(db)));

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(TodaySectionStatuses.Warning, section.Status);
        var data = Assert.IsType<ClassificationSuggestionsTodayData>(section.Data);
        Assert.Equal(1, data.PendingCount);
    }

    [Fact]
    public async Task CalendarLayerProviders_BuildWithPlanningService()
    {
        var (db, userId) = CreateDb();
        var planningService = new PlanningModelService(db, new FixedCurrentUserService(userId));
        var query = Query();

        var habits = await new CalendarHabitsTodaySectionProvider(planningService).BuildAsync(query, CancellationToken.None);
        var availability = await new CalendarAvailabilityTodaySectionProvider(planningService).BuildAsync(query, CancellationToken.None);
        var aiPlaceholders = await new CalendarAiPlaceholdersTodaySectionProvider(planningService).BuildAsync(query, CancellationToken.None);

        Assert.Equal("calendar.habits", habits.Id);
        Assert.Equal(TodaySectionStatuses.Empty, habits.Status);
        Assert.Equal("calendar.availability", availability.Id);
        Assert.Equal(TodaySectionStatuses.Empty, availability.Status);
        Assert.Equal("calendar.ai_placeholders", aiPlaceholders.Id);
        Assert.Equal(TodaySectionStatuses.Empty, aiPlaceholders.Status);
    }

    private static TodayQuery Query() => new(new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 25));

    private static (PimDbContext Db, Guid UserId) CreateDb(bool registerPc = false)
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        if (registerPc)
        {
            PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        }

        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return (new PimDbContext(options), userId);
    }

    private static CalendarService CreateCalendarService(PimDbContext db, Guid userId)
        => new(
            db,
            new FixedCurrentUserService(userId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private sealed class WindowCapturingCalendarService(PimDbContext db, Guid userId) : CalendarService(
        db,
        new FixedCurrentUserService(userId),
        new RecurrenceService(NullLogger<RecurrenceService>.Instance))
    {
        public int CallCount { get; private set; }
        public DateTimeOffset CapturedStart { get; private set; }
        public DateTimeOffset CapturedEnd { get; private set; }

        public override Task<List<EventResponse>> GetEventsAsync(
            DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            CallCount++;
            CapturedStart = start;
            CapturedEnd = end;
            return Task.FromResult(new List<EventResponse>());
        }
    }

    private static PcTrackerService CreatePcTrackerService(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeSystemStatusService(PimHealthStatus status) : ISystemStatusService
    {
        public Task<SystemStatusSummaryDto> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(BuildSummary());

        public Task<SystemStatusDetailDto> GetDetailAsync(CancellationToken ct = default)
            => Task.FromResult(new SystemStatusDetailDto(BuildSummary(), [], []));

        private SystemStatusSummaryDto BuildSummary()
            => new(status, status.ToString(), "Fake status.", DateTimeOffset.UtcNow);
    }
}
