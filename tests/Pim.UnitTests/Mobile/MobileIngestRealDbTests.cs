using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 真库回归（Trait DataSource=RealDb）：InMemory 既不翻译 LINQ 到 SQL、也不执行唯一索引与
/// numeric 精度语义，因此下面这些只在 Postgres 上才成立的行为必须有真库用例兜住：
///
/// 1. 上传链路（含"窗口内是否存在待延长的开放会话"）能被 Npgsql 正常翻译
///    —— 增量修复期间这里踩过 `jsonb ~~ unknown`（42883）的真库报错；
/// 2. 定位点幂等依赖 `numeric(10,7)` 落库后的四舍五入与内存值一致；
/// 3. 定位点唯一索引在真库上确实存在并拦得住重复写入（顺序写入即可验证约束本身）。
///
/// 无 PostgreSQL（或未设置 <c>PIM_TEST_CONN</c>）时用 <c>Skip.If</c> 显式跳过，报告里显示 Skipped。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class MobileIngestRealDbTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
    private const string DeviceId = "real-db-device";

    [SkippableFact]
    public async Task IngestAsync_OnPostgres_RebuildsOnlyWhenTheEventSetOrWindowChanges()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;
        var service = CreateIngest(db);

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var foreground = new MobileUsageEventDto(
            "com.example.messages",
            "MOVE_TO_FOREGROUND",
            start.AddMinutes(5),
            "MainActivity",
            start.AddMinutes(6),
            "{}",
            "item-fg");
        var first = new MobileUsageEventsUploadRequest(
            DeviceId,
            "real-batch-1",
            start,
            start.AddMinutes(15),
            [],
            [foreground],
            []);

        var firstResult = await service.IngestAsync(first, CancellationToken.None);
        Assert.Equal(1, firstResult.AcceptedCount);
        var session = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.DeviceId == DeviceId);
        Assert.Equal(start.AddMinutes(15), session.EndUtc);
        Assert.Contains("open-ended", session.QualityFlagsJson);

        // 补偿批：同一窗口、同一事件（全部重复）-> 不重建，会话行不变
        var compensation = first with { ClientBatchId = "real-batch-2" };
        var compensationResult = await service.IngestAsync(compensation, CancellationToken.None);
        Assert.Equal(0, compensationResult.AcceptedCount);
        Assert.Equal(1, compensationResult.SkippedCount);
        var afterCompensation = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.DeviceId == DeviceId);
        Assert.Equal(session.Id, afterCompensation.Id);
        Assert.Equal(start.AddMinutes(15), afterCompensation.EndUtc);

        // 窗口变宽：事件仍是重复，但开放会话必须重新封口
        var widened = compensation with { ClientBatchId = "real-batch-3", SourceWindowEndUtc = start.AddHours(4) };
        await service.IngestAsync(widened, CancellationToken.None);
        var widened_session = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.DeviceId == DeviceId);
        Assert.Equal(start.AddHours(4), widened_session.EndUtc);
    }

    [SkippableFact]
    public async Task SubmitAsync_OnPostgres_IsIdempotentDespiteNumericRounding()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;
        var service = new MobileLocationService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

        // 带足够多有效位的坐标：落库会按 numeric(10,7) 四舍五入，幂等匹配必须仍然命中同一行。
        var request = new MobileLocationPointRequest(
            DeviceId,
            DateTimeOffset.Parse("2026-07-06T08:00:00.123456Z"),
            31.2304161234567,
            121.4737017654321,
            12.345678,
            "gps",
            "manual",
            4.2,
            6.0,
            1.1,
            0.5,
            90,
            1.5,
            false,
            "{}");

        var first = await service.SubmitAsync(request, CancellationToken.None);
        var second = await service.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Set<MobileLocationPointEntity>().CountAsync(row => row.DeviceId == DeviceId));

        // 唯一索引确实存在并拦得住重复写入（绕过服务层直接插同一自然键；这是顺序写入，非并发）
        db.ChangeTracker.Clear();
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            RecordedAtUtc = request.RecordedAtUtc,
            Latitude = 31.2304161m,
            Longitude = 121.4737018m,
            HorizontalAccuracyMeters = 12.35m,
            Provider = "gps",
            Source = "manual",
            RawJson = "{}",
            Quality = "usable",
            CreatedAt = Now
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task MaterializeAsync_OnPostgresWithRetryingProvider_WorksWithoutAnAmbientTransaction()
    {
        // 生产 DbContext 开了 EnableRetryOnFailure：自管事务必须走执行策略，
        // 否则 EF 会抛"执行策略不支持用户发起的事务"（评审第三轮 Important）。
        await using var database = await TempDatabase.CreateAsync(enableRetry: true);
        await using var db = database.Db;

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = "com.example.messages",
            StartUtc = start.AddMinutes(5),
            EndUtc = start.AddMinutes(35),
            DurationMs = 30 * 60 * 1000,
            QualityFlagsJson = "[]",
            CreatedAt = Now
        });
        await db.SaveChangesAsync();

        var service = CreateMaterialization(db);
        var result = await service.MaterializeAsync(
            MobileTestHelpers.UserId,
            DeviceId,
            start,
            start.AddHours(1),
            CancellationToken.None);

        Assert.True(result.WrittenAggregates > 0);
        Assert.True(result.WrittenBlocks > 0);
        Assert.NotEmpty(await db.Set<MobileAnalyticsMaterializationEntity>()
            .AsNoTracking()
            .Where(row => row.DeviceId == DeviceId)
            .ToListAsync());
    }

    private static MobileAnalyticsMaterializationService CreateMaterialization(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        var currentUser = MobileTestHelpers.CurrentUser();
        return new MobileAnalyticsMaterializationService(
            db,
            new MobileUsageAggregationService(
                db,
                currentUser,
                new MobileAnalyticsQueryService(timeProvider),
                new MobileUsageGoalService(db, currentUser, timeProvider),
                timeProvider,
                new MobileAppClassificationService(db, currentUser)),
            new MobileTimelineBlockService(
                db,
                currentUser,
                timeProvider,
                new MobileAppClassificationService(db, currentUser)),
            timeProvider);
    }

    private static MobileUsageIngestService CreateIngest(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        return new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            timeProvider);
    }

    /// <summary>一次性数据库：EnsureCreated 只有在库内无表时才会建表，因此不能复用共享库。</summary>
    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _database;
        private readonly NpgsqlConnection _admin;

        private TempDatabase(string database, NpgsqlConnection admin, PimDbContext db)
        {
            _database = database;
            _admin = admin;
            Db = db;
        }

        public PimDbContext Db { get; }

        /// <summary>无 PostgreSQL（未设置 PIM_TEST_CONN 或连不上）时抛 SkipException 跳过。</summary>
        public static async Task<TempDatabase> CreateAsync(bool enableRetry = false)
        {
            var connStr = RealDbTestConnection.Require();
            MobileTestHelpers.RegisterMobileModule();
            var admin = new NpgsqlConnection(connStr);
            await admin.OpenAsync();

            var database = $"test_mobile_ingest_{Guid.NewGuid():N}";
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
                await create.ExecuteNonQueryAsync();

            var connectionString = new NpgsqlConnectionStringBuilder(connStr) { Database = database }.ConnectionString;
            var builder = new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(
                connectionString,
                npgsql =>
                {
                    if (enableRetry)
                        npgsql.EnableRetryOnFailure(3);
                });
            var db = new PimDbContext(builder.Options);
            await db.Database.EnsureCreatedAsync();
            return new TempDatabase(database, admin, db);
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
