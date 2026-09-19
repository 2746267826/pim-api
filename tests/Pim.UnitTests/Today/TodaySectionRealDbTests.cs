using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Api.Today;
using Pim.Core.Today;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Today;

/// <summary>
/// 真库回归（Trait DataSource=RealDb）：InMemory 不执行 Npgsql 对 timestamp with time zone 的
/// offset-0 校验，issue #313（本地时区 +08:00 偏移的 DateTimeOffset 作为查询参数被 Npgsql 拒绝，
/// 「今日」页 calendar.schedule / calendar.habits / calendar.availability / calendar.ai_placeholders
/// 四个区块恒 section_unavailable）只有真库才能复现。
///
/// 无 PostgreSQL（或未设置 <c>PIM_TEST_CONN</c>）时用 Skip 显式跳过，报告里显示 Skipped。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class TodaySectionRealDbTests
{
    [SkippableFact]
    public async Task CalendarScheduleProvider_BuildsOnPostgres_WithShanghaiDayWindow()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;
        var calendarService = new CalendarService(
            db,
            new FixedCurrentUserService(database.UserId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));
        var calendar = await calendarService.CreateCalendarAsync(
            new CreateCalendarRequest("Work", "#3B82F6"),
            CancellationToken.None);
        // 2026-05-25 00:30 / 2026-05-26 00:30（Asia/Shanghai）：前者落在当日窗口内，后者在窗口外。
        var includedStart = new DateTimeOffset(2026, 5, 24, 16, 30, 0, TimeSpan.Zero);
        var excludedStart = new DateTimeOffset(2026, 5, 25, 16, 30, 0, TimeSpan.Zero);
        var included = await calendarService.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "Early today", null, null, includedStart, includedStart.AddMinutes(30), null),
            CancellationToken.None);
        var excluded = await calendarService.CreateEventAsync(
            new CreateEventRequest(calendar.Id, "Next day", null, null, excludedStart, excludedStart.AddMinutes(30), null),
            CancellationToken.None);
        var provider = new CalendarScheduleTodaySectionProvider(calendarService);

        var section = await provider.BuildAsync(Query(), CancellationToken.None);

        Assert.NotEqual(TodaySectionStatuses.Unavailable, section.Status);
        Assert.Null(section.Error);
        var data = Assert.IsType<CalendarScheduleTodayData>(section.Data);
        Assert.Contains(data.Events, e => e.Id == included.Id);
        Assert.DoesNotContain(data.Events, e => e.Id == excluded.Id);
    }

    [SkippableFact]
    public async Task CalendarLayerProviders_BuildOnPostgres_WithUtcWindow()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;
        var planningService = new PlanningModelService(db, new FixedCurrentUserService(database.UserId));

        var habits = await new CalendarHabitsTodaySectionProvider(planningService).BuildAsync(Query(), CancellationToken.None);
        var availability = await new CalendarAvailabilityTodaySectionProvider(planningService).BuildAsync(Query(), CancellationToken.None);
        var aiPlaceholders = await new CalendarAiPlaceholdersTodaySectionProvider(planningService).BuildAsync(Query(), CancellationToken.None);

        Assert.Equal(TodaySectionStatuses.Empty, habits.Status);
        Assert.Null(habits.Error);
        Assert.Equal(TodaySectionStatuses.Empty, availability.Status);
        Assert.Null(availability.Error);
        Assert.Equal(TodaySectionStatuses.Empty, aiPlaceholders.Status);
        Assert.Null(aiPlaceholders.Error);
    }

    private static TodayQuery Query() => new(new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 25));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    /// <summary>一次性数据库：EnsureCreated 只有在库内无表时才会建表，因此不能复用共享库。</summary>
    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _database;
        private readonly NpgsqlConnection _admin;

        private TempDatabase(string database, NpgsqlConnection admin, PimDbContext db, Guid userId)
        {
            _database = database;
            _admin = admin;
            Db = db;
            UserId = userId;
        }

        public PimDbContext Db { get; }

        public Guid UserId { get; }

        /// <summary>无 PostgreSQL（未设置 PIM_TEST_CONN 或连不上）时抛 SkipException 跳过。</summary>
        public static async Task<TempDatabase> CreateAsync()
        {
            var connStr = RealDbTestConnection.Require();
            PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
            var admin = new NpgsqlConnection(connStr);
            await admin.OpenAsync();

            var database = $"test_today_section_{Guid.NewGuid():N}";
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
                await create.ExecuteNonQueryAsync();

            var connectionString = new NpgsqlConnectionStringBuilder(connStr) { Database = database }.ConnectionString;
            var builder = new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(connectionString);
            var db = new PimDbContext(builder.Options);
            await db.Database.EnsureCreatedAsync();
            return new TempDatabase(database, admin, db, Guid.NewGuid());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", _admin))
                await drop.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }
    }
}
