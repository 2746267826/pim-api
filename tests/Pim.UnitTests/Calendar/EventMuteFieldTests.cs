using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// 工单 04「日程期间静音」：事件三态字段 <c>is_muted</c> 与日历本默认 <c>default_muted</c> 的行为。
///
/// <para>
/// 已确认语义（维护者 2026-10-14 定，见工单 §七）：<c>is_muted</c> 三态 ——
/// <c>true</c> 静音 / <c>false</c> 不静音 / <c>null</c> 未指定（生效值取所属日历本的 <c>default_muted</c>）。
/// 因此「用户手工改过的单条日程」永远不会被同步覆盖：同步链路不写这一列，未指定的日程继续跟随日历本默认。
/// </para>
///
/// <para>
/// 批量设定（日历本设置）一次做两件事：写日历本默认 + 把该本下既有（未删除）日程统一写成显式值。
/// </para>
/// </summary>
public sealed class EventMuteFieldTests
{
    private static readonly Guid UserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid OtherUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddde");

    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    // ---------- E1.1 / E1.2：创建、更新、查询全链路 ----------

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateEventAsync_RoundTripsTriStateIsMuted(bool? isMuted)
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "三态日程", null, null, Start, End, null,
                IsMuted: isMuted),
            default);

        Assert.Equal(isMuted, response.IsMuted);
        var stored = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(isMuted, stored.IsMuted);
    }

    [Fact]
    public async Task UpdateEventAsync_IsMuted_IsFullReplace_SoUnspecifiedCanBeRestored()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var created = await service.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "日程", null, null, Start, End, null, IsMuted: true),
            default);
        Assert.True(created.IsMuted);

        // 改回「未指定」——必须能回到 null，否则三态退化成一次性二态
        var cleared = await service.UpdateEventAsync(
            created.Id,
            new UpdateEventRequest(calendar.Id, "日程", null, null, Start, End, null, IsMuted: null),
            default);
        Assert.Null(cleared.IsMuted);

        var disabled = await service.UpdateEventAsync(
            created.Id,
            new UpdateEventRequest(calendar.Id, "日程", null, null, Start, End, null, IsMuted: false),
            default);
        Assert.False(disabled.IsMuted);

        var stored = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.False(stored.IsMuted);
    }

    [Fact]
    public async Task EventQueries_ExposeIsMuted_OnListAndPagedPaths()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "静音", null, null, Start, End, null, IsMuted: true),
            default);
        await service.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "跟随默认", null, null, Start.AddHours(1), End.AddHours(1), null),
            default);

        var list = await service.GetEventsAsync(Start.AddHours(-1), End.AddHours(2), default);
        Assert.True(list.Single(e => e.Title == "静音").IsMuted);
        Assert.Null(list.Single(e => e.Title == "跟随默认").IsMuted);

        var paged = await service.GetEventsPagedAsync(null, calendar.Id, Start.AddHours(-1), End.AddHours(2), 1, 50, default);
        Assert.True(paged.Items.Single(e => e.Title == "静音").IsMuted);
        Assert.Null(paged.Items.Single(e => e.Title == "跟随默认").IsMuted);
    }

    // ---------- E1.3：日历本默认 + 批量设定 ----------

    [Fact]
    public async Task GetCalendarsAsync_ExposesDefaultMuted()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        calendar.DefaultMuted = true;
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var calendars = await service.GetCalendarsAsync(null, default);

        Assert.True(Assert.Single(calendars).DefaultMuted);
    }

    [Fact]
    public async Task SetCalendarMutePolicyAsync_SetsCalendarDefault_AndAppliesToExistingEvents()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var follower = await service.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "未指定", null, null, Start, End, null), default);
        var explicitOff = await service.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "手工关", null, null, Start.AddHours(1), End.AddHours(1), null, IsMuted: false),
            default);

        var result = await service.SetCalendarMutePolicyAsync(calendar.Id, true, default);

        Assert.Equal(calendar.Id, result.CalendarId);
        Assert.True(result.DefaultMuted);
        Assert.Equal(2, result.AffectedEventCount);

        var calendars = await service.GetCalendarsAsync(null, default);
        Assert.True(Assert.Single(calendars).DefaultMuted);

        var events = await db.Set<EventEntity>().AsNoTracking().ToListAsync();
        Assert.True(events.Single(e => e.Id == follower.Id).IsMuted);
        Assert.True(events.Single(e => e.Id == explicitOff.Id).IsMuted);
    }

    [Fact]
    public async Task SetCalendarMutePolicyAsync_CanTurnMuteOff()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        calendar.DefaultMuted = true;
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var muted = await service.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "静音", null, null, Start, End, null, IsMuted: true), default);

        var result = await service.SetCalendarMutePolicyAsync(calendar.Id, false, default);

        Assert.False(result.DefaultMuted);
        Assert.Equal(1, result.AffectedEventCount);
        Assert.False((await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == muted.Id)).IsMuted);
    }

    [Fact]
    public async Task SetCalendarMutePolicyAsync_OnlyTouchesOwnLiveEventsOfThatCalendar()
    {
        await using var db = CreateDb();
        var target = SeedCalendar(db, UserId, "目标本");
        var sibling = SeedCalendar(db, UserId, "别的本");
        var foreign = SeedCalendar(db, OtherUserId, "别人的本");
        // 兄弟本/他本先设为 true：这样"批量没动它们"才是真断言
        //（若两个默认值都是 false，断言恒真、不构成防线 —— 独立 review 指出）
        sibling.DefaultMuted = true;
        foreign.DefaultMuted = true;
        await db.SaveChangesAsync();

        var inTarget = await AddEventAsync(db, target.Id, "目标日程", isMuted: false);
        var inSibling = await AddEventAsync(db, sibling.Id, "同用户别的本", isMuted: false);
        var inForeign = await AddEventAsync(db, foreign.Id, "别人的日程", isMuted: false);
        var deleted = await AddEventAsync(db, target.Id, "已删除", isMuted: false);
        var deletedEntity = await db.Set<EventEntity>().SingleAsync(e => e.Id == deleted);
        deletedEntity.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.SetCalendarMutePolicyAsync(target.Id, true, default);

        Assert.Equal(1, result.AffectedEventCount);
        // 用 IgnoreQueryFilters 才能看到软删除的那条（生产查询默认过滤掉它）
        var events = await db.Set<EventEntity>().IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(e => e.Id);
        Assert.True(events[inTarget].IsMuted);
        Assert.False(events[inSibling].IsMuted);
        Assert.False(events[inForeign].IsMuted);
        Assert.False(events[deleted].IsMuted);
        Assert.True((await db.Set<CalendarEntity>().AsNoTracking().SingleAsync(c => c.Id == sibling.Id)).DefaultMuted);
        Assert.True((await db.Set<CalendarEntity>().AsNoTracking().SingleAsync(c => c.Id == foreign.Id)).DefaultMuted);

        // 别人的日历本不可被当前用户设置（与 /calendars 其它写操作一致：不存在即 02002）
        await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(() =>
            CreateService(db, OtherUserId).SetCalendarMutePolicyAsync(target.Id, true, default));
    }

    [Fact]
    public async Task SetCalendarMutePolicyAsync_RejectsMissingIsMuted_WithoutChangingAnything()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        calendar.DefaultMuted = true;
        await db.SaveChangesAsync();
        var existing = await AddEventAsync(db, calendar.Id, "既有日程", isMuted: true);
        var service = CreateService(db);

        // 端点绑定后 IsMuted 为 null；一个 {} 请求体绝不能等于「统一设为不静音」
        var ex = await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(() =>
            service.SetCalendarMutePolicyAsync(calendar.Id, null, default));
        Assert.Equal(02009, ex.ErrorCode);

        Assert.True((await db.Set<CalendarEntity>().AsNoTracking().SingleAsync()).DefaultMuted);
        Assert.True((await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == existing)).IsMuted);
    }

    [Fact]
    public async Task SetCalendarMutePolicyAsync_RefreshesUpdatedAtOfAffectedEvents()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var eventId = await AddEventAsync(db, calendar.Id, "日程", isMuted: false);
        var stale = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entity = await db.Set<EventEntity>().SingleAsync(e => e.Id == eventId);
        entity.UpdatedAt = stale;
        await db.SaveChangesAsync();

        var fixedNow = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var service = new CalendarService(
            db, new FixedCurrentUserService(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance),
            new EventAttachmentService(db), new FixedTimeProvider(fixedNow), null);

        await service.SetCalendarMutePolicyAsync(calendar.Id, true, default);

        var updated = await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == eventId);
        Assert.True(updated.IsMuted);
        Assert.Equal(fixedNow, updated.UpdatedAt);
        Assert.Equal(fixedNow, (await db.Set<CalendarEntity>().AsNoTracking().SingleAsync()).UpdatedAt);
    }

    [Fact]
    public async Task SetCalendarMutePolicyAsync_AuditFailure_DoesNotFailTheRequest_ButDataIsApplied()
    {
        // 独立 review 二轮 Important：审计写在业务提交之后，若审计异常向上抛，
        // 调用方会看到「操作失败」而库里其实已经改了。这里做故障注入：
        // 审计服务恒抛异常 → 请求必须成功返回，且业务数据已生效。
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var eventId = await AddEventAsync(db, calendar.Id, "日程", isMuted: false);

        var service = new CalendarService(
            db,
            new FixedCurrentUserService(UserId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance),
            new EventAttachmentService(db),
            new FixedTimeProvider(DateTimeOffset.UtcNow),
            new CalendarAuditWriter(new ThrowingAuditLogService()),
            NullLogger<CalendarService>.Instance);

        var result = await service.SetCalendarMutePolicyAsync(calendar.Id, true, default);

        Assert.True(result.DefaultMuted);
        Assert.Equal(1, result.AffectedEventCount);
        Assert.True((await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == eventId)).IsMuted);
        Assert.True((await db.Set<CalendarEntity>().AsNoTracking().SingleAsync()).DefaultMuted);
    }

    [Fact]
    public async Task EventQueries_CalendarView_RecurringOccurrencesCarryIsMuted()
    {
        // 独立 review 指出：日历视图走的是 MapExpanded（重复日程展开）而不是 Map，
        // 之前只有单条路径被覆盖。这条钉住展开路径也带 isMuted。
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, UserId, "课程表");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "每周静音", null, null, Start, End, "FREQ=DAILY;COUNT=3",
                IsMuted: true),
            default);

        var expanded = await service.GetEventsAsync(Start.AddHours(-1), Start.AddDays(4), default);

        Assert.Equal(3, expanded.Count);
        Assert.All(expanded, e => Assert.True(e.IsMuted));
        Assert.All(expanded, e => Assert.Equal("每周静音", e.Title));
    }

    // ---------- helpers ----------

    private static async Task<Guid> AddEventAsync(PimDbContext db, Guid calendarId, string title, bool? isMuted)
    {
        var entity = new EventEntity
        {
            CalendarId = calendarId,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            DtStart = Start,
            DtEnd = End,
            IsMuted = isMuted,
        };
        db.Set<EventEntity>().Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"event-mute-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateService(PimDbContext db, Guid? userId = null)
        => new(db, new FixedCurrentUserService(userId ?? UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private static CalendarEntity SeedCalendar(PimDbContext db, Guid userId, string name)
    {
        var calendar = new CalendarEntity { UserId = userId, Name = name, Kind = "calendar" };
        db.Set<CalendarEntity>().Add(calendar);
        return calendar;
    }

    /// <summary>恒抛异常的审计服务：用于验证「审计失败不得让已完成的操作对外报错」。</summary>
    private sealed class ThrowingAuditLogService : IAuditLogService
    {
        public Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("audit store unavailable");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
