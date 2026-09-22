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

        // 断言策略（#254 修复后重写）：
        // 这份用例连的是"生产形状"的镜像库，其快照会随时间滚动，而各条尺子的结论也会随
        // 修复上线而变化（例如 S13 从红转绿、S6 从红转黄）。因此**不再逐条冻结状态快照**，
        // 改为断言那些与快照无关、却能真正抓住"假绿灯/链路坏掉"的结构性事实：
        //   1. 绿基线 S3/S5/S8/S10/S12 必须保持绿（不得因修复回归）；
        //   2. 已知存在存量欠账的尺子必须"可判定"（红或黄），绝不能是 UNKNOWN 或绿；
        //   3. 任何一条尺子都不得因为"取数链路坏了"而退化成 UNKNOWN。
        foreach (var key in AllRuleKeys)
        {
            RequireDetail(details, key);
        }

        RequireDetail(details, "S8_INV-C19_covered_layers");

        // 1. 绿基线：修复任何条目都不得让这几条回归。
        string[] mustStayGreen =
        [
            "S3_INV-P18", "S5_INV-P19", "S8_INV-C19", "S10_INV-C21", "S12_INV-M22"
        ];
        foreach (var key in mustStayGreen)
        {
            var actual = RequireDetail(details, key);
            Assert.StartsWith("🟢 PASS", actual);
        }

        // 2. 已知存量欠账：生产形状数据上这些尺子必须仍然"看得见问题"（红或黄）。
        //    这里刻意不断言"必须红"：判据修好之后，只剩存量违规的尺子会正确降级为黄线
        //    （例如 S6），把它钉成红色等于要求尺子继续误报。
        string[] mustStillDetectProblems =
        [
            "S1_INV-P16", "S2_INV-P17", "S4_INV-C18", "S7_INV-P21", "S11_INV-M21"
        ];
        foreach (var key in mustStillDetectProblems)
        {
            var actual = RequireDetail(details, key);
            Assert.True(
                actual.StartsWith("🔴", StringComparison.Ordinal) || actual.StartsWith("🟡", StringComparison.Ordinal),
                $"{key} 在存在存量欠账的生产形状数据上既非红也非黄，疑似假绿灯：{actual}");
        }

        // 3. 绝不接受"因为取数链路坏了而整片 UNKNOWN"：判定项大面积退化说明取数坏了。
        var unavailable = AllRuleKeys
            .Select(key => (Key: key, Detail: RequireDetail(details, key)))
            .Where(entry => entry.Detail.StartsWith("⚪ UNKNOWN", StringComparison.Ordinal))
            .Select(entry => $"{entry.Key}: {entry.Detail}")
            .ToList();

        Assert.True(
            unavailable.Count == 0,
            $"不应有尺子因取数失败退化为 UNKNOWN（{unavailable.Count}）：{string.Join(" | ", unavailable)}");

        Assert.Equal("DataField", RequireDetail(details, "S8_INV-C19_covered_layers"));
        Assert.True(result.IssueCount > 0, "生产形状数据上必须检出问题，不能是假绿灯");

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

        // 结构化报告（设置页「数据可信度」面板的数据来源）也必须能落到真库上：
        // 13 条尺子齐全、S2 拿得到三态分布、每条都有阈值文案。
        var report = await inspector.InspectReportAsync(DateTimeOffset.UtcNow);
        Assert.Equal(13, report.Rules.Count);
        Assert.Single(report.Rules, rule => rule.Code == "S2" && rule.ThreeState != null);
        Assert.All(report.Rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Threshold)));
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
