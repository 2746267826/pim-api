using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

public sealed class PcIssues234And238RealDbTests
{
    private static PimDbContext TryCreateDbContext()
    {
        var connStr = Environment.GetEnvironmentVariable("PIM_TEST_CONN");
        Skip.If(string.IsNullOrWhiteSpace(connStr), "RealDb unavailable (PIM_TEST_CONN not set), skipping test.");

        try
        {
            using var conn = new NpgsqlConnection(connStr);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();
        }
        catch (Exception ex)
        {
            throw new Xunit.SkipException($"RealDb connection failed: {ex.Message}");
        }

        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connStr)
            .Options;

        return new PimDbContext(options);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Issue238_PostAwDate_QualityCheck_UsesNativeEvents_AndDoesNotRequireAwBuckets()
    {
        await using var db = TryCreateDbContext();

        var fixedNow = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var service = new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = fixedNow });

        // Query date 2026-09-10 (post-AW era, native events exist)
        var result = await service.GetQualityAsync(new DateTime(2026, 9, 10), null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Components, c => c.Key == "aw-buckets");
        Assert.DoesNotContain(result.Components, c => c.Key == "aw-events");
        Assert.Contains(result.Components, c => c.Key == "tracker-events");
        Assert.Contains(result.Components, c => c.Key == "interpreted-timeline");
        Assert.DoesNotContain(result.Issues, i => i.Code.StartsWith("missing-aw-", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Issue234_PostAwDate_ActivityClassification_UsesNativeEvents()
    {
        await using var db = TryCreateDbContext();

        var recomputeService = new ActivityClassificationRecomputeService(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationRuleService(db),
            new StubCurrentUserService(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            NullLogger<ActivityClassificationRecomputeService>.Instance);

        // 环境前提（#339）：镜像库需存在原生 msedge tracker 事件，否则本用例无意义。
        // 原生 tracker 事件只在 AW 退役后写入，因此该日必然是 post-AW 业务日。
        // 取**最近**一个有 msedge 事件的日子（而不是写死某个历史日期）：
        // 镜像快照会滚动，写死日期早晚会没数据，让用例退化成永久 Skip。
        var probeDay = await db.Set<TrackerEventEntity>()
            .AsNoTracking()
            .Where(e => e.AppName == "msedge" && e.EventType == "window")
            .Select(e => e.Timestamp)
            .OrderByDescending(ts => ts)
            .FirstOrDefaultAsync(CancellationToken.None);

        Skip.If(
            probeDay == default,
            "镜像库无匹配数据（pc_tracker_events 无 msedge 原生事件），跳过 #234 原生事件核对。");

        var day = DateOnly.FromDateTime(probeDay.UtcDateTime).ToString("yyyy-MM-dd");

        // 分类名用现行统一字典：旧「浏览」已被迁移 20260815154954 统一为「文档」，
        // 沿用旧名会在任何已迁移库上以「分类不存在」失败（与空库无关的真实缺陷）。
        var rule = new SaveActivityClassificationRuleRequest(
            RuleName: "Microsoft Edge",
            Scope: "app",
            CategoryName: CategoryLegacyMapper.Documents,
            ProjectTag: null,
            Color: "#0078d4",
            Priority: 100,
            ConditionsJson: "{\"all\":[{\"field\":\"appName\",\"op\":\"equals\",\"value\":\"msedge\"}]}",
            Confidence: 1.0,
            Explanation: null);

        var range = new ActivityClassificationApplyRangeRequest("range", day, day);

        var preview = await recomputeService.PreviewRuleAsync(rule, range, CancellationToken.None);

        // 业务断言保持原样：有数据就必须真的算出受影响记录，且样本含 msedge。
        Assert.NotNull(preview);
        Assert.True(preview.AffectedRecordCount > 0, $"Expected affected native events on {day}, got {preview.AffectedRecordCount}");
        Assert.Contains(preview.Samples, s => s.AppName != null && s.AppName.Equals("msedge", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubCurrentUserService : ICurrentUserService
    {
        public StubCurrentUserService(Guid userId)
        {
            UserId = userId;
        }

        public Guid? UserId { get; }
        public string? Email => "test@test.local";
        public string? Role => "User";
        public bool IsAuthenticated => true;
    }
}
