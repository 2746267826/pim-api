using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.UnitTests.Calendar;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 真库（PostgreSQL）回归：#273 把远端已删除的日历标记为 remote-missing 之后，
/// 用户仍必须能通过显式请求（重试 / 深度同步）再次访问该日历，且同步成功后回到 active。
///
/// 为什么必须用真库：本次改动给绑定筛选条件加了 <c>OR + Contains</c>，
/// InMemory provider 会直接求值、不暴露 SQL 翻译问题，只有 Npgsql 能证明查询真的能下发。
///
/// 写入全部发生在临时 schema 内，结束时 DROP SCHEMA CASCADE，不碰 public，也不碰镜像数据。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class OutlookRemoteMissingRealDbTests
{
    private static readonly string[] RequiredTables =
    [
        "calendars",
        "events",
        "outlook_connections",
        "outlook_calendar_bindings",
        "outlook_sync_batches",
    ];

    private static NpgsqlConnectionStringBuilder Scoped(string connStr, string schema)
        => new(connStr) { SearchPath = schema };

    private static async Task CreateSchemaAsync(NpgsqlConnection conn, string schema)
    {
        await using (var probe = new NpgsqlCommand(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relname = ANY(@tables)", conn))
        {
            probe.Parameters.AddWithValue("tables", RequiredTables);
            var found = Convert.ToInt64(await probe.ExecuteScalarAsync() ?? 0L);
            Skip.If(found < RequiredTables.Length,
                "RealDb 缺少日历同步表结构（未指向已迁移的库），跳过测试。");
        }

        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", conn))
            await create.ExecuteNonQueryAsync();

        foreach (var table in RequiredTables)
        {
            await using var copy = new NpgsqlCommand(
                $"CREATE TABLE \"{schema}\".\"{table}\" (LIKE public.\"{table}\" INCLUDING ALL)", conn);
            await copy.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(PimDbContext Db, NpgsqlConnection Admin, string Schema)> OpenAsync()
    {
        var connStr = RealDbTestConnection.Require();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        var schema = $"test_pr273_{Guid.NewGuid():N}";
        await CreateSchemaAsync(admin, schema);

        var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(Scoped(connStr, schema).ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options);

        return (db, admin, schema);
    }

    private static async Task DropAsync(NpgsqlConnection admin, string schema)
    {
        NpgsqlConnection.ClearAllPools();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private static OutlookCalendarSyncService BuildService(
        PimDbContext db, ScriptedHttpMessageHandler handler, DateTimeOffset now)
    {
        var graph = new GraphCalendarClient(
            new StubHttpClientFactory(handler),
            new FakeOutlookAccessTokenProvider(),
            new StubTimeProvider { UtcNowValue = now });
        return new OutlookCalendarSyncService(
            db, graph, new StubTimeProvider { UtcNowValue = now },
            NullLogger<OutlookCalendarSyncService>.Instance);
    }

    private static async Task<(Guid ConnectionId, OutlookCalendarBindingEntity Binding)> SeedMissingBindingAsync(
        PimDbContext db)
    {
        var userId = Guid.NewGuid();
        var connection = new OutlookConnectionEntity
        {
            UserId = userId,
            ClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            Status = "connected",
            TokenHealth = "healthy"
        };
        db.Set<OutlookConnectionEntity>().Add(connection);

        var calendar = new CalendarEntity { UserId = userId, Name = "课程表26-27秋", Source = "outlook" };
        db.Set<CalendarEntity>().Add(calendar);

        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = connection.Id,
            PimCalendarId = calendar.Id,
            // ImmutableId shape taken from issue #272 (the calendar deleted on the Outlook side).
            GraphCalendarId = "AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0A4SKIlUSZoEiUqwTah9pkMwAGIKCi1wAA",
            Name = "课程表26-27秋",
            IsSelected = true,
            RemoteState = "remote-missing",
            LastErrorCode = "404",
            LastErrorMessage = "Graph 404"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();
        return (connection.Id, binding);
    }

    [SkippableFact]
    public async Task RealDb_RemoteMissingBinding_IsReachableByExplicitRequestAndRecoversToActive()
    {
        var (db, admin, schema) = await OpenAsync();
        var now = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using var _ = db;
            var (_, binding) = await SeedMissingBindingAsync(db);
            var userId = await db.Set<OutlookConnectionEntity>()
                .Where(c => c.Id == binding.ConnectionId).Select(c => c.UserId).SingleAsync();

            var handler = new ScriptedHttpMessageHandler();
            var service = BuildService(db, handler, now);

            // The calendar exists again on the Graph side.
            handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
            var response = await service.SyncAsync(
                userId,
                new OutlookSyncRequest("normal", CalendarBindingIds: new[] { binding.Id }),
                CancellationToken.None);

            Assert.Equal("completed", response.Status);
            Assert.Single(handler.Requests);
            Assert.Contains("calendarView", handler.Requests[0].RequestUri!.ToString());

            var reloaded = await db.Set<OutlookCalendarBindingEntity>()
                .AsNoTracking()
                .FirstAsync(b => b.Id == binding.Id);
            Assert.Equal("active", reloaded.RemoteState);
            Assert.Null(reloaded.LastErrorCode);
            Assert.Null(reloaded.LastErrorMessage);

            // Now reachable by the ordinary (non-explicit) sync path too.
            handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
            var normal = await service.SyncAsync(userId, new OutlookSyncRequest("normal"), CancellationToken.None);
            Assert.Equal("completed", normal.Status);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            await DropAsync(admin, schema);
            await admin.DisposeAsync();
        }
    }

    /// <summary>
    /// #309 需求 5（真库）：旧策略遗留的 <c>remote-missing</c> 存量，在常规同步中必须被
    /// 「跟随删除」清掉——绑定行消失、日历与全部日程进入回收站、凭证留痕。
    ///
    /// 为什么必须用真库：存量清理走的是 <c>UPDATE calendars/events</c> + <c>DELETE
    /// outlook_calendar_bindings</c>，其中 events 上还有
    /// <c>(outlook_calendar_binding_id, outlook_event_id)</c> 的唯一索引（带 <c>deleted_at IS NULL</c>
    /// 过滤）与到 bindings 的外键。InMemory provider 不校验这些约束，只有 Npgsql 能证明
    /// "先脱钩、再删除绑定"的顺序真的可行。
    /// </summary>
    [SkippableFact]
    public async Task RealDb_PlainSync_MirrorDeletesLegacyRemoteMissingStockIntoRecycleBin()
    {
        var (db, admin, schema) = await OpenAsync();
        var now = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using var _ = db;
            var (_, binding) = await SeedMissingBindingAsync(db);
            var userId = await db.Set<OutlookConnectionEntity>()
                .Where(c => c.Id == binding.ConnectionId).Select(c => c.UserId).SingleAsync();

            var calendarId = binding.PimCalendarId;
            var events = new List<EventEntity>();
            for (var i = 0; i < 3; i++)
            {
                events.Add(new EventEntity
                {
                    CalendarId = calendarId,
                    Uid = $"ghost-{i}@pim",
                    Title = $"幽灵日程 {i}",
                    Source = "outlook",
                    DtStart = now.AddDays(1 + i),
                    DtEnd = now.AddDays(1 + i).AddHours(1),
                    OutlookCalendarBindingId = binding.Id,
                    OutlookConnectionId = binding.ConnectionId,
                    OutlookEventId = $"ghost-event-{i}",
                });
            }
            db.Set<EventEntity>().AddRange(events);
            await db.SaveChangesAsync();

            var handler = new ScriptedHttpMessageHandler();
            var service = BuildService(db, handler, now);

            var response = await service.SyncAsync(userId, new OutlookSyncRequest("normal"), CancellationToken.None);

            Assert.Equal("completed", response.Status);
            Assert.Empty(handler.Requests); // 存量早已确认缺失，清理不需要 Graph 往返

            // 绑定行被删除：条目不再残留在「日历选择」列表中。
            Assert.False(await db.Set<OutlookCalendarBindingEntity>().AnyAsync(b => b.Id == binding.Id));

            // 日历进回收站。
            var deletedCalendar = await db.Set<CalendarEntity>()
                .IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == calendarId);
            Assert.NotNull(deletedCalendar.DeletedAt);
            Assert.Equal("outlook-remote-missing", deletedCalendar.DeletedByOperationKind);
            Assert.NotNull(deletedCalendar.DeletedByOperationId);

            // 全部日程一并进回收站，且与 Outlook 脱钩（需求 6：恢复出来即本地数据）。
            var deletedEvents = await db.Set<EventEntity>()
                .IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.CalendarId == calendarId)
                .ToListAsync();
            Assert.Equal(3, deletedEvents.Count);
            Assert.All(deletedEvents, e => Assert.NotNull(e.DeletedAt));
            Assert.All(deletedEvents, e => Assert.Equal(deletedCalendar.DeletedByOperationId, e.DeletedByOperationId));
            Assert.All(deletedEvents, e => Assert.Null(e.OutlookEventId));
            Assert.All(deletedEvents, e => Assert.Null(e.OutlookCalendarBindingId));
            Assert.All(deletedEvents, e => Assert.Null(e.OutlookConnectionId));

            // 默认过滤器下不可见。
            Assert.False(await db.Set<CalendarEntity>().AnyAsync(c => c.Id == calendarId));
            Assert.Empty(await db.Set<EventEntity>().Where(e => e.CalendarId == calendarId).ToListAsync());

            // 回收站里能看到这 1 个日历 + 3 条日程。
            var recycleBin = await db.Set<CalendarEntity>()
                .IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.DeletedAt != null)
                .ToListAsync();
            Assert.Contains(recycleBin, c => c.Id == calendarId);
        }
        finally
        {
            await DropAsync(admin, schema);
            await admin.DisposeAsync();
        }
    }

    /// <summary>
    /// #309 需求 6（真库）：回收站恢复出来的日历必须是可用的本地日历——绑定已删除、Source 转
    /// manual、日程的 Outlook 标识已清空，且恢复后再同步不会把它重新拉回 Outlook 绑定。
    /// </summary>
    [SkippableFact]
    public async Task RealDb_RestoredMirrorDeletedCalendar_BecomesPlainLocalData()
    {
        var (db, admin, schema) = await OpenAsync();
        var now = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using var _ = db;
            var (_, binding) = await SeedMissingBindingAsync(db);
            var userId = await db.Set<OutlookConnectionEntity>()
                .Where(c => c.Id == binding.ConnectionId).Select(c => c.UserId).SingleAsync();
            var calendarId = binding.PimCalendarId;

            var ghost = new EventEntity
            {
                CalendarId = calendarId,
                Uid = "ghost-0@pim",
                Title = "幽灵日程",
                Source = "outlook",
                DtStart = now.AddDays(1),
                DtEnd = now.AddDays(1).AddHours(1),
                OutlookCalendarBindingId = binding.Id,
                OutlookConnectionId = binding.ConnectionId,
                OutlookEventId = "ghost-event",
            };
            db.Set<EventEntity>().Add(ghost);
            await db.SaveChangesAsync();

            var service = BuildService(db, new ScriptedHttpMessageHandler(), now);
            await service.SyncAsync(userId, new OutlookSyncRequest("normal"), CancellationToken.None);

            // 走真实回收站服务恢复（需要 ICurrentUserService / 审计）。
            var currentUser = new FixedCurrentUser(userId);
            var audit = new CalendarAuditWriter(new NullAuditLogService());
            var recycleBin = new CalendarRecycleBinService(db, currentUser, audit);

            var result = await recycleBin.RestoreAsync(
                "calendar", calendarId, new CalendarRestoreRequest(), CancellationToken.None);

            Assert.Equal(2, result.AffectedCount); // 日历 + 1 条日程

            var restoredCalendar = await db.Set<CalendarEntity>().AsNoTracking()
                .FirstAsync(c => c.Id == calendarId);
            Assert.Null(restoredCalendar.DeletedAt);
            Assert.Equal("manual", restoredCalendar.Source);
            Assert.Null(restoredCalendar.DeletedByOperationId);

            var restoredEvent = await db.Set<EventEntity>().AsNoTracking().FirstAsync(e => e.Id == ghost.Id);
            Assert.Null(restoredEvent.DeletedAt);
            Assert.Null(restoredEvent.OutlookEventId);
            Assert.Null(restoredEvent.OutlookCalendarBindingId);
            Assert.Null(restoredEvent.OutlookConnectionId);

            // 没有绑定行残留在自己身上（恢复后是纯本地数据）。
            Assert.False(await db.Set<OutlookCalendarBindingEntity>()
                .AnyAsync(b => b.PimCalendarId == calendarId));
        }
        finally
        {
            await DropAsync(admin, schema);
            await admin.DisposeAsync();
        }
    }

    private sealed class FixedCurrentUser(Guid userId) : Pim.Infrastructure.Auth.ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class NullAuditLogService : Pim.Core.Operations.IAuditLogService
    {
        public Task<Pim.Core.Operations.AuditLogDto> RecordAsync(
            Pim.Core.Operations.CreateAuditLogRequest request,
            CancellationToken ct = default)
            => Task.FromResult(new Pim.Core.Operations.AuditLogDto(
                Guid.NewGuid(),
                request.UserId,
                request.ActorType,
                request.Action,
                request.ResourceType,
                request.ResourceId,
                request.Source,
                request.Result,
                null,
                DateTimeOffset.UtcNow));
    }
}
