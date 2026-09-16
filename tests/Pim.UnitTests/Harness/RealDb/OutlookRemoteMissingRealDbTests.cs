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

    [SkippableFact]
    public async Task RealDb_PlainSync_SkipsRemoteMissingButKeepsLocalData()
    {
        var (db, admin, schema) = await OpenAsync();
        var now = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using var _ = db;
            var (_, binding) = await SeedMissingBindingAsync(db);
            var userId = await db.Set<OutlookConnectionEntity>()
                .Where(c => c.Id == binding.ConnectionId).Select(c => c.UserId).SingleAsync();

            // The ghost calendar still owns local events - the issue demands they survive.
            var ghost = new EventEntity
            {
                CalendarId = binding.PimCalendarId,
                Title = "幽灵日程",
                Source = "outlook",
                DtStart = now.AddDays(1),
                DtEnd = now.AddDays(1).AddHours(1)
            };
            db.Set<EventEntity>().Add(ghost);
            await db.SaveChangesAsync();

            var handler = new ScriptedHttpMessageHandler();
            var service = BuildService(db, handler, now);

            var response = await service.SyncAsync(userId, new OutlookSyncRequest("normal"), CancellationToken.None);

            Assert.Equal("completed", response.Status);
            Assert.Empty(handler.Requests); // the gone calendar is no longer retried

            var kept = await db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == ghost.Id);
            Assert.NotNull(kept);
            Assert.Null(kept!.DeletedAt);
        }
        finally
        {
            await DropAsync(admin, schema);
            await admin.DisposeAsync();
        }
    }
}
