using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// issue #351：习惯创建后必须可**编辑 / 归档 / 删除**，且各展示面同步。
///
/// <para>
/// 缺陷形态：后端只有「列表 / 创建 / 登记 occurrence」三个端点，对已创建的习惯做
/// 常规管理操作（GET/PUT/DELETE /habits/{id}、archive）全部 404；前端「归档」页签
/// 按 <c>status=archived</c> 过滤却没有任何端点能把习惯置为 archived，永远为空。
/// </para>
///
/// <para>
/// 本用例在服务层锁定全链路语义（创建 → 编辑 → 归档 → 删除），并覆盖边界与权限。
/// </para>
/// </summary>
public class HabitManagementTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ================= 编辑 =================

    [Fact]
    public async Task UpdateHabit_ChangesTitleDescriptionAndCadence()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("晨间复盘", "原始描述", "Daily"), CancellationToken.None);

        var updated = await service.UpdateHabitAsync(
            created.Id,
            new UpdateHabitRequest("晨间复盘（改）", "新描述", "Weekly"),
            CancellationToken.None);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("晨间复盘（改）", updated.Title);
        Assert.Equal(Pim.Core.Planning.HabitCadence.Weekly, updated.Cadence);

        var stored = await db.Set<HabitRoutineEntity>().AsNoTracking().SingleAsync(h => h.Id == created.Id);
        Assert.Equal("晨间复盘（改）", stored.Title);
        Assert.Equal("新描述", stored.Description);
        Assert.Equal("Weekly", stored.Cadence);
    }

    [Fact]
    public async Task UpdateHabit_PartialRequest_LeavesOmittedFieldsUnchanged()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("原标题", "原描述", "Weekly"), CancellationToken.None);

        // 只改标题：描述与 cadence 必须保持不变（PATCH 语义，避免静默清空）。
        await service.UpdateHabitAsync(
            created.Id, new UpdateHabitRequest(Title: "新标题"), CancellationToken.None);

        var stored = await db.Set<HabitRoutineEntity>().AsNoTracking().SingleAsync(h => h.Id == created.Id);
        Assert.Equal("新标题", stored.Title);
        Assert.Equal("原描述", stored.Description);
        Assert.Equal("Weekly", stored.Cadence);
    }

    [Fact]
    public async Task UpdateHabit_RejectsBlankTitle()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("原标题", null, "Daily"), CancellationToken.None);

        await Assert.ThrowsAsync<DomainException>(() => service.UpdateHabitAsync(
            created.Id, new UpdateHabitRequest(Title: "   "), CancellationToken.None));

        var stored = await db.Set<HabitRoutineEntity>().AsNoTracking().SingleAsync(h => h.Id == created.Id);
        Assert.Equal("原标题", stored.Title);
    }

    /// <summary>编辑后的标题必须同步到日历 habits 图层（展示面同步）。</summary>
    [Fact]
    public async Task UpdateHabit_PropagatesToCalendarLayer()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("旧标题", null, "Daily"), CancellationToken.None);

        var start = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        await service.CreateHabitOccurrenceAsync(
            created.Id,
            new CreateHabitOccurrenceRequest(start, start.AddMinutes(30)),
            CancellationToken.None);

        await service.UpdateHabitAsync(
            created.Id, new UpdateHabitRequest(Title: "新标题"), CancellationToken.None);

        var layers = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(start.AddHours(-1), start.AddHours(1), ["habits"]),
            CancellationToken.None);

        Assert.Contains(layers.Items, item => item.Title == "新标题");
        Assert.DoesNotContain(layers.Items, item => item.Title == "旧标题");
    }

    // ================= 归档 =================

    [Fact]
    public async Task ArchiveHabit_DisappearsFromActiveListAndAppearsInArchiveFilter()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("待归档", null, "Daily"), CancellationToken.None);

        var archived = await service.ArchiveHabitAsync(created.Id, CancellationToken.None);
        Assert.Equal("Archived", archived.Status);

        // 前端「执行中」页签按 status != archived 过滤，归档后必须消失。
        var all = await service.ListHabitsAsync(CancellationToken.None);
        var active = all.Where(h => !string.Equals(h.Status, "Archived", StringComparison.OrdinalIgnoreCase)).ToList();
        var archivedTab = all.Where(h => string.Equals(h.Status, "Archived", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.DoesNotContain(active, h => h.Id == created.Id);
        Assert.Contains(archivedTab, h => h.Id == created.Id);
    }

    [Fact]
    public async Task ArchiveHabit_RemovesFromCalendarLayer()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("待归档图层", null, "Daily"), CancellationToken.None);

        var start = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        await service.CreateHabitOccurrenceAsync(
            created.Id,
            new CreateHabitOccurrenceRequest(start, start.AddMinutes(30)),
            CancellationToken.None);

        await service.ArchiveHabitAsync(created.Id, CancellationToken.None);

        var layers = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(start.AddHours(-1), start.AddHours(1), ["habits"]),
            CancellationToken.None);

        Assert.DoesNotContain(layers.Items, item => item.Title == "待归档图层");
    }

    /// <summary>
    /// 归档口径必须与前端一致（忽略大小写）：前端用 <c>status.toLowerCase() === 'archived'</c>，
    /// 若后端图层过滤区分大小写，会出现「列表已归档、日历仍显示」的矛盾。
    /// </summary>
    [Fact]
    public async Task ArchivedHabit_WithDifferentCasing_StillHiddenFromCalendarLayer()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("大小写归档", null, "Daily"), CancellationToken.None);

        var start = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        await service.CreateHabitOccurrenceAsync(
            created.Id,
            new CreateHabitOccurrenceRequest(start, start.AddMinutes(30)),
            CancellationToken.None);

        // 模拟历史/外部写入的小写归档态。
        var entity = await db.Set<HabitRoutineEntity>().SingleAsync(h => h.Id == created.Id);
        entity.Status = "archived";
        await db.SaveChangesAsync();

        var layers = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(start.AddHours(-1), start.AddHours(1), ["habits"]),
            CancellationToken.None);

        Assert.DoesNotContain(layers.Items, item => item.Title == "大小写归档");
    }

    // ================= 删除 =================

    [Fact]
    public async Task DeleteHabit_SoftDeletesAndHidesFromAllSurfaces()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("待删除", null, "Daily"), CancellationToken.None);

        var start = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        await service.CreateHabitOccurrenceAsync(
            created.Id,
            new CreateHabitOccurrenceRequest(start, start.AddMinutes(30)),
            CancellationToken.None);

        await service.DeleteHabitAsync(created.Id, CancellationToken.None);

        // 列表不再出现
        var listed = await service.ListHabitsAsync(CancellationToken.None);
        Assert.DoesNotContain(listed, h => h.Id == created.Id);

        // 日历 habits 图层不再出现
        var layers = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(start.AddHours(-1), start.AddHours(1), ["habits"]),
            CancellationToken.None);
        Assert.DoesNotContain(layers.Items, item => item.Title == "待删除");

        // 遵循仓库软删除惯例：行仍在（可审计），但 deleted_at 已置位。
        var raw = await db.Set<HabitRoutineEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(h => h.Id == created.Id);
        Assert.NotNull(raw.DeletedAt);
    }

    /// <summary>删除习惯时其历史 occurrence 必须一并软删除，不留下孤儿数据。</summary>
    [Fact]
    public async Task DeleteHabit_SoftDeletesItsOccurrences_NoOrphans()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("带发生记录", null, "Daily"), CancellationToken.None);

        var start = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        await service.CreateHabitOccurrenceAsync(
            created.Id,
            new CreateHabitOccurrenceRequest(start, start.AddMinutes(30)),
            CancellationToken.None);

        await service.DeleteHabitAsync(created.Id, CancellationToken.None);

        var orphanCount = await db.Set<HabitOccurrenceEntity>()
            .IgnoreQueryFilters()
            .CountAsync(o => o.HabitRoutineId == created.Id && o.DeletedAt == null);
        Assert.Equal(0, orphanCount);
    }

    [Fact]
    public async Task DeleteHabit_Twice_IsIdempotent()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("重复删除", null, "Daily"), CancellationToken.None);

        await service.DeleteHabitAsync(created.Id, CancellationToken.None);
        // 第二次删除：已删除的习惯视为不存在（幂等，不抛 500）。
        await Assert.ThrowsAsync<DomainException>(
            () => service.DeleteHabitAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteHabit_AfterArchive_StillWorks()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("先归档再删", null, "Daily"), CancellationToken.None);

        await service.ArchiveHabitAsync(created.Id, CancellationToken.None);
        await service.DeleteHabitAsync(created.Id, CancellationToken.None);

        var listed = await service.ListHabitsAsync(CancellationToken.None);
        Assert.DoesNotContain(listed, h => h.Id == created.Id);
    }

    // ================= 权限与作用域 =================

    [Fact]
    public async Task UpdateHabit_OtherUsersHabit_ThrowsNotFound()
    {
        await using var db = CreateDb();
        var ownerService = CreatePlanningService(db);
        var created = await ownerService.CreateHabitAsync(
            new CreateHabitRequest("他人习惯", null, "Daily"), CancellationToken.None);

        var intruder = CreatePlanningService(db, OtherUserId);

        await Assert.ThrowsAsync<DomainException>(
            () => intruder.UpdateHabitAsync(created.Id, new UpdateHabitRequest(Title: "篡改"), CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveHabit_OtherUsersHabit_ThrowsNotFound()
    {
        await using var db = CreateDb();
        var ownerService = CreatePlanningService(db);
        var created = await ownerService.CreateHabitAsync(
            new CreateHabitRequest("他人习惯2", null, "Daily"), CancellationToken.None);

        var intruder = CreatePlanningService(db, OtherUserId);

        await Assert.ThrowsAsync<DomainException>(
            () => intruder.ArchiveHabitAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteHabit_OtherUsersHabit_ThrowsNotFoundAndLeavesItIntact()
    {
        await using var db = CreateDb();
        var ownerService = CreatePlanningService(db);
        var created = await ownerService.CreateHabitAsync(
            new CreateHabitRequest("他人习惯3", null, "Daily"), CancellationToken.None);

        var intruder = CreatePlanningService(db, OtherUserId);

        await Assert.ThrowsAsync<DomainException>(
            () => intruder.DeleteHabitAsync(created.Id, CancellationToken.None));

        var stored = await db.Set<HabitRoutineEntity>().AsNoTracking().SingleAsync(h => h.Id == created.Id);
        Assert.Null(stored.DeletedAt);
    }

    [Fact]
    public async Task ListHabits_OnlyReturnsOwnHabits()
    {
        await using var db = CreateDb();
        var mine = CreatePlanningService(db, UserId);
        var theirs = CreatePlanningService(db, OtherUserId);

        await mine.CreateHabitAsync(new CreateHabitRequest("我的习惯", null, "Daily"), CancellationToken.None);
        await theirs.CreateHabitAsync(new CreateHabitRequest("别人的习惯", null, "Daily"), CancellationToken.None);

        var listed = await mine.ListHabitsAsync(CancellationToken.None);

        Assert.Contains(listed, h => h.Title == "我的习惯");
        Assert.DoesNotContain(listed, h => h.Title == "别人的习惯");
    }

    // ================= 边界 =================

    [Fact]
    public async Task UpdateHabit_UnknownId_ThrowsDomainException()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);

        await Assert.ThrowsAsync<DomainException>(() => service.UpdateHabitAsync(
            Guid.NewGuid(), new UpdateHabitRequest(Title: "不存在"), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateHabit_DeletedHabit_ThrowsDomainException()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("已删除", null, "Daily"), CancellationToken.None);
        await service.DeleteHabitAsync(created.Id, CancellationToken.None);

        await Assert.ThrowsAsync<DomainException>(() => service.UpdateHabitAsync(
            created.Id, new UpdateHabitRequest(Title: "改已删除"), CancellationToken.None));
    }

    [Fact]
    public async Task GetHabit_ReturnsOwnHabit_AndHidesDeleted()
    {
        await using var db = CreateDb();
        var service = CreatePlanningService(db);
        var created = await service.CreateHabitAsync(
            new CreateHabitRequest("可查询", "描述", "Monthly"), CancellationToken.None);

        var fetched = await service.GetHabitAsync(created.Id, CancellationToken.None);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("可查询", fetched.Title);

        await service.DeleteHabitAsync(created.Id, CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(
            () => service.GetHabitAsync(created.Id, CancellationToken.None));
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new PimDbContext(options);
    }

    private static PlanningModelService CreatePlanningService(PimDbContext db, Guid? userId = null)
        => new(db, new FixedCurrentUserService(userId ?? UserId));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
