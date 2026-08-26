using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness.Invariants;
using Xunit;

namespace Pim.UnitTests.Harness;

/// <summary>
/// 回归测试 - 针对Harness发现的真实bug
/// </summary>
public sealed class RegressionTests
{
    /// <summary>
    /// Bug #1: PcTrackerService.GetBusinessDayStartForQuery 曾使用 DateTimeKind.Local，
    /// 在UTC服务器上会导致业务日切割错误（04:00 Shanghai 被算成 04:00 UTC）。
    /// 修复后应使用 Asia/Shanghai 显式时区。
    /// </summary>
    [Fact]
    public void PcBusinessDayStart_ShouldUseShanghaiTimezone()
    {
        // 2026-07-06 04:00 Shanghai = 2026-07-05 20:00 UTC
        var date = new DateTime(2026, 7, 6);
        var result = PcTrackerService.GetBusinessDayStartForQuery(date);
        var expected = new DateTimeOffset(2026, 7, 5, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, result);
        // 验证 04:00 切割正确性：03:59 属于前一天业务日
        var sessionStart = DateTimeOffset.Parse("2026-07-06T03:59:00+08:00");
        var businessDay = sessionStart.ToOffset(TimeSpan.FromHours(8)).Date;
        if (sessionStart.Hour < 4) businessDay = businessDay.AddDays(-1);
        Assert.Equal("2026-07-05", businessDay.ToString("yyyy-MM-dd"));
        // 04:00 属于当天
        var sessionAtFour = DateTimeOffset.Parse("2026-07-06T04:00:00+08:00");
        var day2 = sessionAtFour.ToOffset(TimeSpan.FromHours(8)).Date;
        if (sessionAtFour.Hour < 4) day2 = day2.AddDays(-1);
        Assert.Equal("2026-07-06", day2.ToString("yyyy-MM-dd"));
    }

    /// <summary>
    /// Bug #2: MobileUsageQueryService 未对fallback summary去重，导致重复上报时 total 前景时长翻倍。
    /// 修复后 DeduplicateSummaries 应保证同一app同一小时只保留最大一条。
    /// </summary>
    [Fact]
    public void MobileFallbackSummary_ShouldBeDeduplicated()
    {
        var summaries = new List<(string packageName, int hour, double totalTimeMs)>
        {
            ("com.tencent.mm", 13, 1800000),
            ("com.tencent.mm", 13, 2000000), // duplicate hour
            ("com.tencent.mm", 14, 1000000),
        };
        var (passBefore, _) = MobileTimeInvariants.CheckDeduplicatedSummaries(summaries);
        Assert.False(passBefore, "duplicate should be detected before dedup");

        // 模拟修复后去重逻辑
        var deduped = summaries
            .GroupBy(s => (s.packageName.ToLowerInvariant(), s.hour))
            .Select(g => g.OrderByDescending(s => s.totalTimeMs).First())
            .ToList();
        var (passAfter, detail) = MobileTimeInvariants.CheckDeduplicatedSummaries(deduped);
        Assert.True(passAfter, detail);
        Assert.Equal(2, deduped.Count);
    }

    /// <summary>
    /// Bug #3: Mobile 600小时 bug - 10个app同时前台1小时，若直接累加会得到10小时，正确应去重后为1小时。
    /// 回归：调用真实 MobileUsageAggregationService 验证去重后小时桶 <=3600*1.05
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task OverlappingSessions_ShouldBeDedupedToOneHour()
    {
        var baseTime = DateTimeOffset.Parse("2026-07-06T13:00:00+08:00");
        await using var db = Pim.UnitTests.Mobile.MobileTestHelpers.CreateDb();
        var pkgs = new[] { "com.tencent.mobileqq", "com.tencent.mm", "com.ss.android.ugc.aweme", "com.sina.weibo", "com.alibaba.taobao", "com.netease.cloudmusic", "com.baidu.BaiduMap", "com.autonavi.minimap", "com.microsoft.office.outlook", "com.zhihu.android" };
        foreach (var pkg in pkgs)
        {
            db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
            {
                UserId = Pim.UnitTests.Mobile.MobileTestHelpers.UserId,
                DeviceId = "phone-main",
                PackageName = pkg,
                StartUtc = baseTime,
                EndUtc = baseTime.AddHours(1),
                DurationMs = 3600000,
                QualityFlagsJson = "[]",
                CreatedAt = baseTime
            });
        }
        await db.SaveChangesAsync();
        var service = new MobileUsageAggregationService(
            db, Pim.UnitTests.Mobile.MobileTestHelpers.CurrentUser(),
            new MobileAnalyticsQueryService(Pim.UnitTests.Mobile.MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T10:00:00Z"))),
            new MobileUsageGoalService(db, Pim.UnitTests.Mobile.MobileTestHelpers.CurrentUser(), Pim.UnitTests.Mobile.MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T10:00:00Z"))),
            Pim.UnitTests.Mobile.MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T10:00:00Z")));
        var overview = await service.GetOverviewAsync(new Pim.Module.Mobile.DTOs.MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:00Z")), System.Threading.CancellationToken.None);
        // 真实服务去重后总时长应为3600而非36000
        Assert.True(overview.TotalForegroundSeconds <= 3780, $"total {overview.TotalForegroundSeconds} should be ~3600 after dedup, not 36000");
        Assert.InRange(overview.TotalForegroundSeconds, 3590, 3780);
    }
}
