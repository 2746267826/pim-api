using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
/// 工单 03 D2/D3：任务计时（开始 / 暂停 / 再开始 / 结束）+ 累计时长 + 与任务状态的一致性。
///
/// 判定标准：
/// - AC-4：一次「开始 → 暂停 → 再开始 → 结束」落下两条段（paused / completed），
///   累计时长只统计真正在跑的时间（暂停区间不计）。
/// - AC-8：任务进入终态后，不允许留下 <c>ends_at IS NULL</c> 的在跑段。
/// </summary>
public class TaskTimerServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartTimerAsync_CreatesOpenSegmentAndMovesTaskToInProcess()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);

        var segment = await planner.StartTimerAsync(task.Id);

        Assert.Equal(Start, segment.StartsAt);
        Assert.Null(segment.EndsAt);
        Assert.Equal(TaskSegmentStatuses.Active, segment.Status);
        Assert.Equal("timer", segment.Source);

        db.ChangeTracker.Clear();
        var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        // 「开始计时」即任务进入 IN-PROCESS（D3 的自然衔接）
        Assert.Equal(TaskStatuses.InProcess, stored.Status);
        var storedSegment = await db.Set<TaskExecutionSegmentEntity>().SingleAsync();
        Assert.Null(storedSegment.EndsAt);
    }

    [Fact]
    public async Task StartTimerAsync_RejectsASecondRunningSegment()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var planner = CreatePlanner(db, new TestClock(Start));

        await planner.StartTimerAsync(task.Id);
        var error = await Assert.ThrowsAsync<DomainException>(() => planner.StartTimerAsync(task.Id));

        Assert.Equal(02058, error.ErrorCode);
        db.ChangeTracker.Clear();
        Assert.Single(await db.Set<TaskExecutionSegmentEntity>().ToListAsync());
    }

    [Theory]
    [InlineData(TaskStatuses.Completed)]
    [InlineData(TaskStatuses.Cancelled)]
    public async Task StartTimerAsync_RejectsTerminalTask(string status)
    {
        await using var db = CreateDb();
        var task = SeedTask(db, status);
        await db.SaveChangesAsync();
        var planner = CreatePlanner(db, new TestClock(Start));

        var error = await Assert.ThrowsAsync<DomainException>(() => planner.StartTimerAsync(task.Id));

        Assert.Equal(02060, error.ErrorCode);
        Assert.Empty(await db.Set<TaskExecutionSegmentEntity>().ToListAsync());
    }

    [Fact]
    public async Task PauseTimerAsync_ClosesTheOpenSegmentAsPaused()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);

        await planner.StartTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromMinutes(10));
        var paused = await planner.PauseTimerAsync(task.Id);

        Assert.Equal(Start.AddMinutes(10), paused.EndsAt);
        Assert.Equal(TaskSegmentStatuses.Paused, paused.Status);
        db.ChangeTracker.Clear();
        // 暂停不改任务状态：任务仍在进行中，只是当前没有在跑的段
        Assert.Equal(TaskStatuses.InProcess, (await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id)).Status);
    }

    [Fact]
    public async Task StopTimerAsync_ClosesTheOpenSegmentAsCompleted()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);

        await planner.StartTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromMinutes(25));
        var stopped = await planner.StopTimerAsync(task.Id);

        Assert.Equal(Start.AddMinutes(25), stopped.EndsAt);
        Assert.Equal(TaskSegmentStatuses.Completed, stopped.Status);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    public async Task TimerActions_WithoutRunningSegment_AreRejected(string action)
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.InProcess);
        await db.SaveChangesAsync();
        var planner = CreatePlanner(db, new TestClock(Start));

        var error = await Assert.ThrowsAsync<DomainException>(() => action == "pause"
            ? planner.PauseTimerAsync(task.Id)
            : planner.StopTimerAsync(task.Id));

        Assert.Equal(02059, error.ErrorCode);
    }

    [Fact]
    public async Task FullTimerFlow_AccumulatesOnlyTheRunningIntervals()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);

        await planner.StartTimerAsync(task.Id);          // 09:00
        clock.Advance(TimeSpan.FromMinutes(10));          // 09:10 暂停 → 第一条段 = 10 分钟
        await planner.PauseTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromMinutes(5));           // 09:15 再开始（暂停的 5 分钟不计）
        await planner.StartTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromMinutes(10));          // 09:25 结束 → 第二条段 = 10 分钟
        await planner.StopTimerAsync(task.Id);

        db.ChangeTracker.Clear();
        var segments = await db.Set<TaskExecutionSegmentEntity>()
            .OrderBy(s => s.StartsAt)
            .ToListAsync();

        Assert.Equal(2, segments.Count);
        Assert.Equal(TaskSegmentStatuses.Paused, segments[0].Status);
        Assert.Equal(Start.AddMinutes(10), segments[0].EndsAt);
        Assert.Equal(Start.AddMinutes(15), segments[1].StartsAt);
        Assert.Equal(TaskSegmentStatuses.Completed, segments[1].Status);
        Assert.Equal(Start.AddMinutes(25), segments[1].EndsAt);

        var state = await planner.GetTimerStateAsync(task.Id);
        Assert.Equal(20 * 60, state.AccumulatedSeconds);   // 10 + 10，暂停的 5 分钟没算进来
        Assert.Equal(2, state.SegmentCount);
        Assert.Null(state.RunningSegmentId);
        Assert.Equal(TaskStatuses.InProcess, state.TaskStatus);
    }

    [Fact]
    public async Task GetTimerStateAsync_CountsTheRunningSegmentUpToNow()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);

        var running = await planner.StartTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromSeconds(90));

        var state = await planner.GetTimerStateAsync(task.Id);

        Assert.Equal(running.Id, state.RunningSegmentId);
        Assert.Equal(Start, state.RunningSince);
        Assert.Equal(90, state.AccumulatedSeconds);
    }

    [Fact]
    public async Task GetTimerStateAsync_IgnoresOnlyPlannedSegments()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        db.Set<TaskExecutionSegmentEntity>().AddRange(
            NewSegment(task.Id, Start, Start.AddHours(1), TaskSegmentStatuses.Planned),
            NewSegment(task.Id, Start, Start.AddMinutes(30), TaskSegmentStatuses.Completed),
            NewSegment(task.Id, Start, Start.AddMinutes(5), TaskSegmentStatuses.Cancelled));
        await db.SaveChangesAsync();
        var planner = CreatePlanner(db, new TestClock(Start));

        var state = await planner.GetTimerStateAsync(task.Id);

        // 复审 I7：只有 planned（还没做）不计入；cancelled 段是**真实发生过的执行**，
        // 必须计入 —— 否则「跑一段再取消」会把已发生的时长抹掉。
        Assert.Equal(35 * 60, state.AccumulatedSeconds);
        Assert.Equal(3, state.SegmentCount);
    }

    [Fact]
    public async Task CancellingWhileRunning_KeepsTheElapsedTimeAndClosesTheSegment()
    {
        // 复审 I7 的核心反例：跑 10 分钟后取消任务，已发生的 10 分钟不能凭空消失；
        // 而且「直接取消」与「先暂停再取消」必须给出同样的累计值（此前两者不一致）。
        await using var db = CreateDb();
        var direct = SeedTask(db, TaskStatuses.NeedsAction);
        var viaPause = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);
        var calendar = TaskStatusValueSetTests.CreateCalendarService(db, clock);

        await planner.StartTimerAsync(direct.Id);
        await planner.StartTimerAsync(viaPause.Id);
        clock.Advance(TimeSpan.FromMinutes(10));
        await planner.PauseTimerAsync(viaPause.Id);

        await calendar.UpdateTaskAsync(
            direct.Id,
            TaskStatusValueSetTests.UpdateRequest(TaskStatuses.Cancelled),
            CancellationToken.None);
        await calendar.UpdateTaskAsync(
            viaPause.Id,
            TaskStatusValueSetTests.UpdateRequest(TaskStatuses.Cancelled),
            CancellationToken.None);

        var directState = await planner.GetTimerStateAsync(direct.Id);
        var viaPauseState = await planner.GetTimerStateAsync(viaPause.Id);
        Assert.Equal(10 * 60, directState.AccumulatedSeconds);
        Assert.Equal(10 * 60, viaPauseState.AccumulatedSeconds);
        Assert.Null(directState.RunningSegmentId);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<TaskExecutionSegmentEntity>().Where(s => s.EndsAt == null).ToListAsync());
    }

    [Fact]
    public async Task DeleteTaskAsync_ClosesTheRunningSegmentAndKeepsTheElapsedTime()
    {
        // 复审 I6：任务被软删（回收站）时不能把开放段留在库里 —— 否则恢复任务后，
        // 删除期间（可能几天）的墙钟时间会被算进「实际执行时长」。
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);
        var deletes = new CalendarDeleteService(
            db,
            new TaskStatusValueSetTests.FixedCurrentUserService(UserId),
            new CalendarAuditWriter(new AuditLogService(db)),
            clock);

        await planner.StartTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromMinutes(7));
        await deletes.DeleteTaskAsync(task.Id);

        db.ChangeTracker.Clear();
        var segment = await db.Set<TaskExecutionSegmentEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.Equal(Start.AddMinutes(7), segment.EndsAt);
        Assert.Equal(TaskSegmentStatuses.Cancelled, segment.Status);

        // 再等一小时（模拟任务在回收站里待着），恢复后累计值不得增长
        clock.Advance(TimeSpan.FromHours(1));
        var restored = await db.Set<TaskEntity>().IgnoreQueryFilters().SingleAsync(t => t.Id == task.Id);
        restored.DeletedAt = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var state = await planner.GetTimerStateAsync(task.Id);
        Assert.Equal(7 * 60, state.AccumulatedSeconds);
        Assert.Null(state.RunningSegmentId);
    }

    [Fact]
    public async Task DeleteSegmentAsync_RejectsRunningSegment()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);
        var running = await planner.StartTimerAsync(task.Id);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            planner.DeleteSegmentAsync(task.Id, running.Id));

        Assert.Equal(02059, error.ErrorCode);
        db.ChangeTracker.Clear();
        Assert.Null((await db.Set<TaskExecutionSegmentEntity>().SingleAsync()).DeletedAt);
    }

    [Fact]
    public async Task UpdateTaskAsync_ToCompleted_ClosesTheRunningSegment()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);
        var calendar = TaskStatusValueSetTests.CreateCalendarService(db, clock);
        await planner.StartTimerAsync(task.Id);
        clock.Advance(TimeSpan.FromMinutes(40));

        await calendar.UpdateTaskAsync(
            task.Id,
            TaskStatusValueSetTests.UpdateRequest(TaskStatuses.Completed),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var segment = await db.Set<TaskExecutionSegmentEntity>().SingleAsync();
        Assert.Equal(Start.AddMinutes(40), segment.EndsAt);
        Assert.Equal(TaskSegmentStatuses.Completed, segment.Status);
        // AC-8：完成任务后不允许「段还在跑」
        Assert.Empty(await db.Set<TaskExecutionSegmentEntity>().Where(s => s.EndsAt == null).ToListAsync());
        Assert.Equal(40 * 60, (await planner.GetTimerStateAsync(task.Id)).AccumulatedSeconds);
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_ToCancelled_ClosesRunningSegmentsOfTheBatch()
    {
        await using var db = CreateDb();
        var first = SeedTask(db, TaskStatuses.NeedsAction);
        var second = SeedTask(db, TaskStatuses.NeedsAction);
        var untouched = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var clock = new TestClock(Start);
        var planner = CreatePlanner(db, clock);
        var calendar = TaskStatusValueSetTests.CreateCalendarService(db, clock);

        await planner.StartTimerAsync(first.Id);
        await planner.StartTimerAsync(second.Id);
        await planner.StartTimerAsync(untouched.Id);
        clock.Advance(TimeSpan.FromMinutes(7));

        await calendar.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(
            new[] { first.Id, second.Id },
            TaskStatuses.Cancelled,
            null,
            null));

        db.ChangeTracker.Clear();
        var segments = await db.Set<TaskExecutionSegmentEntity>().ToListAsync();
        Assert.Equal(2, segments.Count(s => s.Status == TaskSegmentStatuses.Cancelled));
        Assert.All(
            segments.Where(s => s.TaskId != untouched.Id),
            s => Assert.Equal(Start.AddMinutes(7), s.EndsAt));
        // 不在批次里的任务不受影响，仍在计时
        var stillRunning = Assert.Single(segments.Where(s => s.EndsAt == null));
        Assert.Equal(untouched.Id, stillRunning.TaskId);
    }

    [Fact]
    public async Task AccumulatedSeconds_RoundsEachSegmentSoTheUiSumMatchesTheTotal()
    {
        // 3.6s + 2.6s：逐段向下取整会得到 3+2=5，四舍五入得到 4+3=7。
        // pim-web 的行内时长用同一口径，界面「各行之和」必须等于服务端累计值。
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        db.Set<TaskExecutionSegmentEntity>().AddRange(
            NewSegment(task.Id, Start, Start.AddMilliseconds(3600), TaskSegmentStatuses.Completed),
            NewSegment(task.Id, Start.AddMinutes(1), Start.AddMinutes(1).AddMilliseconds(2600), TaskSegmentStatuses.Completed));
        await db.SaveChangesAsync();
        var planner = CreatePlanner(db, new TestClock(Start));

        var state = await planner.GetTimerStateAsync(task.Id);

        Assert.Equal(7, state.AccumulatedSeconds);
    }

    private static TaskExecutionSegmentEntity NewSegment(
        Guid taskId,
        DateTimeOffset start,
        DateTimeOffset? end,
        string status)
        => new()
        {
            TaskId = taskId,
            UserId = UserId,
            StartsAt = start,
            EndsAt = end,
            Status = status,
            Source = "manual"
        };

    internal static PlanningModelService CreatePlanner(PimDbContext db, TimeProvider clock)
        => new(db, new TaskStatusValueSetTests.FixedCurrentUserService(UserId), null, null, null, clock);

    internal static TaskEntity SeedTask(PimDbContext db, string status)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = $"计时任务 {status}",
            Status = status
        };
        db.Set<TaskEntity>().Add(task);
        return task;
    }

    internal static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-task-timer-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    internal sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
