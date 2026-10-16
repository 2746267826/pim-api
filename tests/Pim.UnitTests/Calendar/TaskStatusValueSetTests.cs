using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// 工单 03 D1：任务状态（<c>tasks.status</c>）收敛为受控值域
/// <c>NEEDS-ACTION / IN-PROCESS / COMPLETED / CANCELLED</c>。
///
/// 判定标准（AC-1）：集合外的值 → <see cref="DomainException"/> 02057（HTTP 400），
/// 且**没有任何字段被写库**；集合内的值必须原样落库。
/// </summary>
public class TaskStatusValueSetTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Theory]
    [InlineData("NEEDS-ACTION")]
    [InlineData("IN-PROCESS")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public async Task CreateTaskAsync_PersistsEveryAllowedStatus(string status)
    {
        await using var db = CreateDb();
        var service = CreateCalendarService(db);

        var created = await service.CreateTaskAsync(
            new CreateTaskRequest(null, "受控状态任务", null, 5, null, null, null, null, Status: status),
            CancellationToken.None);

        Assert.Equal(status, created.Status);
        db.ChangeTracker.Clear();
        var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == created.Id);
        Assert.Equal(status, stored.Status);
    }

    [Fact]
    public async Task CreateTaskAsync_DefaultsToNeedsActionWhenStatusOmitted()
    {
        await using var db = CreateDb();
        var service = CreateCalendarService(db);

        var created = await service.CreateTaskAsync(
            new CreateTaskRequest(null, "未指定状态", null, 5, null, null, null, null),
            CancellationToken.None);

        Assert.Equal(TaskStatuses.NeedsAction, created.Status);
    }

    [Theory]
    [InlineData("DOING")]
    [InlineData("in-process")]
    [InlineData("needs-action")]
    [InlineData("")]
    public async Task CreateTaskAsync_RejectsStatusOutsideValueSet(string status)
    {
        await using var db = CreateDb();
        var service = CreateCalendarService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateTaskAsync(
                new CreateTaskRequest(null, "非法状态", null, 5, null, null, null, null, Status: status),
                CancellationToken.None));

        Assert.Equal(CalendarService.InvalidTaskStatusErrorCode, error.ErrorCode);
        // 报错要能自助修复：消息里必须列出允许值。
        Assert.Contains(TaskStatuses.NeedsAction, error.Message);
        Assert.Contains(TaskStatuses.InProcess, error.Message);
        Assert.Empty(await db.Set<TaskEntity>().ToListAsync());
    }

    [Fact]
    public async Task UpdateTaskAsync_AcceptsInProcess()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var updated = await service.UpdateTaskAsync(task.Id, UpdateRequest(TaskStatuses.InProcess), CancellationToken.None);

        Assert.Equal(TaskStatuses.InProcess, updated.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(TaskStatuses.InProcess, (await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id)).Status);
    }

    [Fact]
    public async Task UpdateTaskAsync_RejectsStatusOutsideValueSetWithoutWritingAnyField()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var request = UpdateRequest("DOING") with { Title = "标题被改动了" };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateTaskAsync(task.Id, request, CancellationToken.None));

        Assert.Equal(CalendarService.InvalidTaskStatusErrorCode, error.ErrorCode);

        // 复审 M1：不能只看「没落库」—— 还要确认**跟踪中的实体本身没有被改写**，
        // 否则「先改字段、后抛异常」这种实现也能骗过数据库读回断言。
        var tracked = db.ChangeTracker.Entries<TaskEntity>().Single(e => e.Entity.Id == task.Id);
        Assert.Equal("原始标题", tracked.Entity.Title);
        Assert.Equal(TaskStatuses.NeedsAction, tracked.Entity.Status);
        Assert.Equal(EntityState.Unchanged, tracked.State);

        db.ChangeTracker.Clear();
        var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        // 非法状态是「整条请求拒绝」，不是「只忽略 status 字段」
        Assert.Equal("原始标题", stored.Title);
        Assert.Equal(TaskStatuses.NeedsAction, stored.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task BatchUpdateTasksAsync_RejectsInvalidStatusEvenWhenNothingWouldBeUpdated(string rawIds)
    {
        // 复审 I3：白名单校验必须在**早期成功返回之前** ——
        // 否则 {"ids":[],"status":"DOING"} 会返回 200「没有更新任务」而不是 02057，
        // 「三个入口非法值一律 400」就不成立。
        await using var db = CreateDb();
        var service = CreateCalendarService(db);
        var ids = rawIds.Length == 0 ? new List<Guid>() : new List<Guid> { Guid.Parse(rawIds) };

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(ids, "DOING", null, null)));

        Assert.Equal(CalendarService.InvalidTaskStatusErrorCode, error.ErrorCode);
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_RejectsStatusOutsideValueSetWithoutWritingAnything()
    {
        await using var db = CreateDb();
        var first = SeedTask(db, TaskStatuses.NeedsAction);
        var second = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(
                new[] { first.Id, second.Id },
                "DOING",
                9,
                null)));

        Assert.Equal(CalendarService.InvalidTaskStatusErrorCode, error.ErrorCode);
        db.ChangeTracker.Clear();
        foreach (var id in new[] { first.Id, second.Id })
        {
            var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == id);
            Assert.Equal(TaskStatuses.NeedsAction, stored.Status);
            Assert.Equal(1, stored.Priority);
        }
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_AcceptsAllowedStatus()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.NeedsAction);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        await service.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(
            new[] { task.Id },
            TaskStatuses.InProcess,
            null,
            null));

        db.ChangeTracker.Clear();
        Assert.Equal(TaskStatuses.InProcess, (await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id)).Status);
    }

    [Fact]
    public async Task UpdateTaskAsync_LeavingCompletedClearsCompletedAt()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, TaskStatuses.Completed);
        task.CompletedAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var updated = await service.UpdateTaskAsync(task.Id, UpdateRequest(TaskStatuses.InProcess), CancellationToken.None);

        Assert.Equal(TaskStatuses.InProcess, updated.Status);
        db.ChangeTracker.Clear();
        var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        // 与批量入口口径一致（此前单条入口会把陈旧的完成时刻留在库里）
        Assert.Null(stored.CompletedAt);
    }

    internal static UpdateTaskRequest UpdateRequest(string status)
        => new(null, "原始标题", null, 1, null, null, null, null, Status: status);

    internal static TaskEntity SeedTask(PimDbContext db, string status)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = "原始标题",
            Priority = 1,
            Status = status
        };
        db.Set<TaskEntity>().Add(task);
        return task;
    }

    internal static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-task-status-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    internal static CalendarService CreateCalendarService(PimDbContext db, TimeProvider? timeProvider = null)
        => new(
            db,
            new FixedCurrentUserService(UserId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance),
            new EventAttachmentService(db),
            timeProvider);

    internal sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
