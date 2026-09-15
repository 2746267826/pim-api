using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// issue #247①：手机端分析接口每包逐次查库 —— 单次 /analytics/charts 实测 686 条 SQL，
/// 其中绝大多数来自"每个包各查 2~3 次"的分类路径。
///
/// 分析查询用到 DateTimeOffset 比较/排序，SQLite 无法执行，因此查询条数用真库（Postgres）计数。
/// 拿不到真库时用例显式 Skip（CI 显示 Skipped），不返回 0 —— 否则 `Assert.True(0 &lt;= 15)`
/// 这类断言会变成恒真式，"N+1 防护"就成了假象。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class MobileAnalyticsQueryCountTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
    private static readonly DateTimeOffset RangeStart = DateTimeOffset.Parse("2026-07-06T00:00:00Z");
    private static readonly DateTimeOffset RangeEnd = DateTimeOffset.Parse("2026-07-07T00:00:00Z");
    private const string DeviceId = "analytics-query-count-device";

    [SkippableFact]
    public async Task GetChartsAsync_QueryCountDoesNotGrowWithPackageCount()
    {
        var baseline = await CountChartsQueriesAsync(1);
        var scaled = await CountChartsQueriesAsync(40);

        // N+1 消除后：包数从 1 涨到 40，查询条数必须完全不变。
        Assert.Equal(baseline, scaled);
        Assert.True(scaled <= 15, $"分析请求应保持常数级查询条数（实测 {scaled} 条）");
    }

    [SkippableFact]
    public async Task GetHeatmapAsync_QueryCountDoesNotGrowWithPackageCount()
    {
        var baseline = await CountHeatmapQueriesAsync(1);
        var scaled = await CountHeatmapQueriesAsync(40);

        Assert.Equal(baseline, scaled);
        Assert.True(scaled <= 15, $"分析请求应保持常数级查询条数（实测 {scaled} 条）");
    }

    [Fact]
    public async Task ClassifyManyAsync_MatchesSinglePackageClassificationFieldByField()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = "com.unknown.vendor.app",
            DisplayName = "Vendor App",
            Category = "video",
            IsSystemApp = false,
            RawJson = "{}",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        db.Set<MobileAppCatalogOverrideEntity>().Add(new MobileAppCatalogOverrideEntity
        {
            UserId = MobileTestHelpers.UserId,
            PackageName = "com.overridden.app",
            DisplayNameOverride = "被覆盖",
            LifeCategory = MobileLifeCategories.Video,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var service = new MobileAppClassificationService(db, MobileTestHelpers.CurrentUser());
        string[] packageNames = ["com.unknown.vendor.app", "com.overridden.app", "com.brand.new.app"];

        var batched = await service.ClassifyManyAsync(packageNames, CancellationToken.None);

        foreach (var packageName in packageNames)
        {
            var single = await service.ClassifyAsync(packageName, CancellationToken.None);
            Assert.Equal(single, batched[packageName]);
        }
    }

    [Fact]
    public async Task ClassifyManyAsync_PreservesCallerPackageNameCasing()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileAppClassificationService(db, MobileTestHelpers.CurrentUser());

        var batched = await service.ClassifyManyAsync(["Com.Example.Mixed"], CancellationToken.None);

        var result = Assert.Single(batched.Values);
        Assert.Equal("com.example.mixed", result.PackageName);
        Assert.True(batched.ContainsKey("Com.Example.Mixed"));
    }

    private static async Task<int> CountChartsQueriesAsync(int packageCount)
        => await CountQueriesAsync(packageCount, async (service, ct) =>
        {
            var charts = await service.GetChartsAsync(
                new MobileAnalyticsQueryRequest(RangeStart, RangeEnd),
                ct);
            Assert.NotEmpty(charts);
        });

    private static async Task<int> CountHeatmapQueriesAsync(int packageCount)
        => await CountQueriesAsync(packageCount, async (service, ct) =>
        {
            var heatmap = await service.GetHeatmapAsync(
                new MobileAnalyticsQueryRequest(RangeStart, RangeEnd),
                ct);
            Assert.NotEmpty(heatmap);
        });

    private static async Task<int> CountQueriesAsync(
        int packageCount,
        Func<MobileUsageAggregationService, CancellationToken, Task> execute)
    {
        // 无真库时在这里 Skip（不会走到计数与断言）。
        var connStr = RealDbTestConnection.Require();
        MobileTestHelpers.RegisterMobileModule();
        await using var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        // 每次计数用一次性数据库：EnsureCreated 只有在库内无表时才会建表，
        // 复用它库（如 pim / pim_test）会得到"表不存在"的假失败。
        var database = $"test_analytics_qc_{Guid.NewGuid():N}";
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
                await create.ExecuteNonQueryAsync();

            var databaseConn = new NpgsqlConnectionStringBuilder(connStr) { Database = database }.ConnectionString;
            var interceptor = new CountingCommandInterceptor();
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(databaseConn)
                .AddInterceptors(interceptor)
                .Options;

            await using var db = new PimDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, packageCount);
            var service = CreateService(db);

            interceptor.Reset();
            await execute(service, CancellationToken.None);
            return interceptor.CommandCount;
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connStr);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedAsync(PimDbContext db, int packageCount)
    {
        for (var index = 0; index < packageCount; index++)
        {
            var packageName = $"com.example.app{index:D2}";
            var start = RangeStart.AddHours(8).AddMinutes(index);
            db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = DeviceId,
                PackageName = packageName,
                StartUtc = start,
                EndUtc = start.AddMinutes(30),
                DurationMs = 30 * 60 * 1000,
                QualityFlagsJson = "[]",
                CreatedAt = Now
            });
            db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = DeviceId,
                PackageName = packageName,
                DisplayName = $"App {index}",
                Category = index % 2 == 0 ? "video" : "communication",
                RawJson = "{}",
                CreatedAt = Now,
                UpdatedAt = Now
            });
        }

        await db.SaveChangesAsync();
    }

    private static MobileUsageAggregationService CreateService(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        var currentUser = MobileTestHelpers.CurrentUser();
        return new MobileUsageAggregationService(
            db,
            currentUser,
            new MobileAnalyticsQueryService(timeProvider),
            new MobileUsageGoalService(db, currentUser, timeProvider),
            timeProvider,
            new MobileAppClassificationService(db, currentUser));
    }

    internal sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _commandCount;

        public int CommandCount => Volatile.Read(ref _commandCount);

        public void Reset() => Volatile.Write(ref _commandCount, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _commandCount);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandCount);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _commandCount);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandCount);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _commandCount);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandCount);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
