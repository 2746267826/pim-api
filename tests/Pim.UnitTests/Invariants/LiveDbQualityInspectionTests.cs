using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Invariants;
using Pim.UnitTests.Harness.RealDb;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 真库体检回放：连上"生产形状"的镜像库跑一次完整取数，验证 13 条尺子都能落到真实数据上。
/// 连接串只来自环境变量（源码不内置口令），拿不到就 <see cref="Skip"/> 显式跳过；
/// 连接串里出现 <c>pim_prod</c> 会被断言直接拦下（AGENTS.md 硬规则，不靠注释约束）。
/// </summary>
public class LiveDbQualityInspectionTests
{
    private static readonly string[] AllRuleKeys =
    {
        "S1_INV-P16", "S2_INV-P17", "S3_INV-P18", "S4_INV-C18", "S5_INV-P19",
        "S6_INV-P20", "S7_INV-P21", "S8_INV-C19", "S9_INV-C20", "S10_INV-C21",
        "S11_INV-M21", "S12_INV-M22", "S13_INV-P22"
    };

    private readonly ITestOutputHelper _output;

    public LiveDbQualityInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task LiveDb_InspectAsync_VerifiesGroundTruthViolations()
    {
        // 本用例断言的是"生产形状数据"下的期望结论（哪些不变式必须红/必须绿），
        // 开发库（PIM_TEST_CONN 指向的 pim/pim_test）数据不同，结论也不同 ——
        // 因此只接受显式指定的生产形状镜像库，未提供时显式 Skip。
        // 连接串一律来自环境变量，源码不内置口令。
        var connectionStrings = new[] { Environment.GetEnvironmentVariable("PIM_MIRROR_CONN") }
            .Where(connString => !string.IsNullOrWhiteSpace(connString))
            .Select(connString => connString!)
            .ToArray();

        string? workingConnStr = null;
        foreach (var connString in connectionStrings)
        {
            // AGENTS.md 硬规则：绝不连接生产库。硬规则必须由代码执行，而不是靠注释提醒。
            Assert.DoesNotContain("pim_prod", connString, StringComparison.OrdinalIgnoreCase);

            try
            {
                await using var testConn = new NpgsqlConnection(connString);
                await testConn.OpenAsync();
                workingConnStr = connString;
                break;
            }
            catch (Exception ex) when (RealDbTestConnection.IsServerUnreachable(ex))
            {
                // 只有"不可达"才尝试下一个候选；口令错误/库不存在/权限不足等配置错误原样抛出。
                _output.WriteLine($"候选连接不可达，尝试下一个：{ex.Message}");
            }
        }

        if (workingConnStr == null)
        {
            // 显式 Skip（而不是静默通过）：报告里要能看出"这台机器没有可用的真实库"。
            throw new Xunit.SkipException(
                "未提供可用真库连接（PIM_PROD_CONN / PIM_TEST_CONN），跳过实机校验。");
        }

        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(workingConnStr);

        await using var db = new PimDbContext(optionsBuilder.Options);

        var options = Options.Create(new InvariantOptions());
        var inspector = new DataReliabilityQualityInspector(db, options, NullLogger<DataReliabilityQualityInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        _output.WriteLine("=== 数据可靠性实机体检结果 ===");
        _output.WriteLine($"Healthy: {result.IsHealthy}");
        _output.WriteLine($"Issues: {result.IssueCount}");
        _output.WriteLine($"Message: {result.Message}");
        _output.WriteLine($"Details Count: {result.Details.Count}");

        // Details 是可空属性：先钉住再取值，缺键/空值都给出可定位的失败信息，而不是 NullReferenceException。
        Assert.NotNull(result.Details);
        var details = result.Details!;
        foreach (var kvp in details)
        {
            _output.WriteLine($"[{kvp.Key}] => {kvp.Value}");
        }

        // 必须不健康（真实生产数据存在违规，绝不得为假绿灯）
        Assert.False(result.IsHealthy);

        // 断言策略（WO-RELIABILITY-WINDOW-20260928 后重写）：
        // 这份用例连的是"生产形状"的镜像库，其快照会随时间滚动，各条尺子的颜色也会随修复上线而变化。
        // 机制改造把颜色从"全历史"改为"只丈量考核窗"，因此这里**不再冻结任何颜色快照**，
        // 改为断言与快照无关、却能真正抓住"假绿灯 / 历史欠账被折色 / 取数链路坏掉"的结构性事实。
        foreach (var key in AllRuleKeys)
        {
            RequireDetail(details, key);
        }

        RequireDetail(details, "S8_INV-C19_covered_layers");

        // 1. 绝不接受"因为取数链路坏了而整片 UNKNOWN"：判定项大面积退化说明取数坏了。
        var unavailable = AllRuleKeys
            .Select(key => (Key: key, Detail: RequireDetail(details, key)))
            .Where(entry => entry.Detail.StartsWith("⚪ UNKNOWN", StringComparison.Ordinal))
            .Select(entry => $"{entry.Key}: {entry.Detail}")
            .ToList();

        Assert.True(
            unavailable.Count == 0,
            $"不应有尺子因取数失败退化为 UNKNOWN（{unavailable.Count}）：{string.Join(" | ", unavailable)}");

        Assert.Equal("DataField", RequireDetail(details, "S8_INV-C19_covered_layers"));

        // summary 必须与逐条状态自洽，不能出现"面板红、汇总绿"。
        var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllRuleKeys)
        {
            var status = ReadStatus(RequireDetail(details, key));
            statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
        }

        Assert.Equal(
            $"{statusCounts.GetValueOrDefault("Red")} Red, {statusCounts.GetValueOrDefault("Yellow")} Yellow, "
            + $"{statusCounts.GetValueOrDefault("Green")} Green, {statusCounts.GetValueOrDefault("Unknown")} Unknown",
            RequireDetail(details, "summary"));

        var report = await inspector.InspectReportAsync(DateTimeOffset.UtcNow);
        Assert.Equal(13, report.Rules.Count);
        Assert.Single(report.Rules, rule => rule.Code == "S2" && rule.ThreeState != null);
        Assert.All(report.Rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Threshold)));

        // 2. 形态 1：颜色只能由**窗内**违规撑起 —— 任何红/黄尺子的窗内计数必须 > 0，
        //    且"窗内 = 0"的尺子必须是绿（历史欠账不得把尺子折黄/折红，AC-2.3 / AC-3.x）。
        foreach (var rule in report.Rules)
        {
            if (rule.Status is "red" or "yellow")
            {
                Assert.True(
                    rule.WindowViolations > 0,
                    $"{rule.Code} 是 {rule.Status} 却没有窗内违规支撑（窗内 {rule.WindowViolations} / 欠账 {rule.HistoricalViolations}），疑似历史欠账折色");
            }
            else if (rule.Status == "green")
            {
                Assert.Equal(0, rule.WindowViolations);
            }
        }

        // 3. 形态 2：计数不重不漏（AC-2.6），且报告级"违规数"只统计窗内（AC-2.4）。
        Assert.All(report.Rules, rule =>
            Assert.Equal(rule.TotalViolations, rule.WindowViolations + rule.HistoricalViolations));
        Assert.Equal(report.Rules.Sum(rule => rule.WindowViolations), report.TotalViolations);
        Assert.Equal(report.TotalViolations, report.WindowViolations);
        Assert.Equal(report.Rules.Sum(rule => rule.HistoricalViolations), report.HistoricalViolations);

        // 4. 生产形状数据上必须**看得见**历史欠账：机制改造是让欠账退出颜色，不是把违规丢掉。
        Assert.True(
            report.HistoricalViolations > 0,
            "生产形状镜像库里存在修复前的大批历史违规，欠账计数不应为 0（否则说明分档或取数出了问题）");

        // 5. 总览状态必须与逐条状态自洽，且只由红/黄/未知支撑。
        var expectedStatus = report.RedCount > 0
            ? "red"
            : report.YellowCount > 0
                ? "yellow"
                : report.UnknownCount > 0
                    ? "unknown"
                    : "green";
        Assert.Equal(expectedStatus, report.Status);
    }

    /// <summary>取一条必须存在的详情；缺失或为空时报出"缺哪个键、实际有哪些键"，便于定位。</summary>
    private static string RequireDetail(IReadOnlyDictionary<string, string> details, string key)
    {
        Assert.True(details.ContainsKey(key), $"体检结果缺少详情键 {key}；实际键：{string.Join(", ", details.Keys)}");
        var value = details[key];
        Assert.False(string.IsNullOrWhiteSpace(value), $"体检结果的 {key} 详情为空");
        return value;
    }

    private static string ReadStatus(string detail)
    {
        if (detail.StartsWith("🔴", StringComparison.Ordinal)) return "Red";
        if (detail.StartsWith("🟡", StringComparison.Ordinal)) return "Yellow";
        if (detail.StartsWith("🟢", StringComparison.Ordinal)) return "Green";
        return "Unknown";
    }
}
