using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 真库用例（#301）：在**生产镜像数据**上核对分类分布与生产力统计的口径，
/// 证明「逐条相加」确实超出物理上限、而修正后的口径落在合理范围内。
///
/// 需要 <c>PIM_TEST_CONN</c>；不可用时显式 Skip（仓库既定约定）。
/// 只读：不对镜像库做任何写入。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class PcCategoryDedupRealDbTests
{
    /// <summary>业务日窗口内用于核对的一个日期（镜像库当天有较多分类记录）。</summary>
    private static readonly DateTime ProbeDate = new(2026, 8, 18);

    private static PimDbContext CreateContext(string connectionString)
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PimDbContext(options);
    }

    [SkippableFact]
    public async Task CategoryDistribution_OnRealData_RespectsPhysicalDayLimit()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var db = CreateContext(connectionString);

        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(ProbeDate);
        var dayEnd = dayStart.AddDays(1);

        var raw = await db.Set<ActivityClassificationEntity>()
            .Where(s => s.StartedAt < dayEnd && s.EndedAt > dayStart)
            .ToListAsync();

        var active = raw.Where(s => !PcActivityOverlapResolver.IsInactive(s.RecordType)).ToList();
        Skip.If(active.Count == 0, "镜像库该业务日没有分类记录，跳过。");

        // 旧口径（逐条 cap + 按重叠比例分摊后相加）—— 修复前的实现。
        // 关键：它**不过滤** gap / idle / afk，且对记录之间的重叠不做任何消解。
        double OldProratedSeconds(ActivityClassificationEntity s)
        {
            var total = (s.EndedAt - s.StartedAt).TotalSeconds;
            if (total <= 0) return 0;
            var capped = Math.Min(total, 3600);
            var os = s.StartedAt > dayStart ? s.StartedAt : dayStart;
            var oe = s.EndedAt < dayEnd ? s.EndedAt : dayEnd;
            var overlap = Math.Max(0, (oe - os).TotalSeconds);
            return overlap <= 0 ? 0 : capped * (overlap / total);
        }

        var oldMinutes = raw.Sum(OldProratedSeconds) / 60.0;

        // 真实墙钟：所有活动记录的时间并集
        var intervals = active
            .Where(s => s.EndedAt > s.StartedAt)
            .Select(s => (
                Start: s.StartedAt > dayStart ? s.StartedAt : dayStart,
                End: s.EndedAt < dayEnd ? s.EndedAt : dayEnd))
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ToList();
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var iv in intervals)
        {
            if (merged.Count > 0 && iv.Start <= merged[^1].End)
            {
                if (iv.End > merged[^1].End) merged[^1] = (merged[^1].Start, iv.End);
                continue;
            }
            merged.Add(iv);
        }
        var unionMinutes = merged.Sum(m => (m.End - m.Start).TotalMinutes);

        // 修复后的口径：通过服务端点取（与线上一致的代码路径）
        var svc = new PcActivityAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(
            new PcAggregationQuery(ProbeDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);

        var newMinutes = res.Items.Sum(i => i.Minutes);

        // 不变量 1：新口径不得超过一天物理上限
        Assert.True(newMinutes <= 24 * 60, $"修正后合计 {newMinutes} 分钟仍超过 24 小时");

        // 不变量 2：新口径不得超过所有活动记录的并集（同一时刻只归属一个分类）
        Assert.True(newMinutes <= unionMinutes + 1.5,
            $"修正后合计 {newMinutes} 分钟超过了活动并集 {unionMinutes:F1} 分钟");

        // 不变量 3：旧口径确实膨胀（否则本用例没有验证意义）
        Assert.True(oldMinutes > unionMinutes,
            $"旧口径 {oldMinutes:F1} 分钟未超过并集 {unionMinutes:F1} 分钟，镜像数据无法体现该缺陷");

        // 诊断输出（xunit 会捕获 Console）
        Console.WriteLine($"[#301 realdb] date={ProbeDate:yyyy-MM-dd} records={active.Count} " +
                          $"old={oldMinutes:F1}min union={unionMinutes:F1}min new={newMinutes}min");

        // 不变量 4：百分比合计 ≈100，且无负值
        Assert.InRange(res.Items.Sum(i => i.Percentage), 99.0, 101.0);
        Assert.All(res.Items, i => Assert.True(i.Percentage >= 0, $"分类 {i.CategoryName} 百分比为负"));
    }

    [SkippableFact]
    public async Task ProductivityDashboard_OnRealData_DoesNotExceedPhysicalDayLimit()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var db = CreateContext(connectionString);

        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(ProbeDate);
        var hasAny = await db.Set<ActivityClassificationEntity>()
            .AnyAsync(s => s.StartedAt < dayStart.AddDays(1) && s.EndedAt > dayStart);
        Skip.If(!hasAny, "镜像库该业务日没有分类记录，跳过。");

        var svc = new PcProductivityService(db);
        var res = await svc.GetDashboardAsync(ProbeDate, CancellationToken.None);

        var totalHours = res.ProductiveHours + res.DistractingHours + res.NeutralHours;
        Console.WriteLine($"[#301 realdb] productivity {ProbeDate:yyyy-MM-dd} " +
                          $"prod={res.ProductiveHours}h dist={res.DistractingHours}h neut={res.NeutralHours}h total={totalHours}h");

        Assert.True(totalHours <= 24.0, $"生产力合计 {totalHours} 小时超过 24 小时物理上限");
        Assert.InRange(res.TodayScore, 0, 100);
        Assert.True(res.ProductiveHours >= 0 && res.DistractingHours >= 0 && res.NeutralHours >= 0);

        // 与分类分布口径自洽：生产力合计不应超过同日的活动并集
        var agg = new PcActivityAggregationService(db);
        var dist = await agg.GetCategoryDistributionAsync(
            new PcAggregationQuery(ProbeDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        var distHours = dist.Items.Sum(i => i.Minutes) / 60.0;

        Assert.True(totalHours <= distHours + 1.0,
            $"生产力合计 {totalHours}h 超过分类分布合计 {distHours:F1}h，两者口径不一致");
    }
}
