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

        // 验证评审指出的各项基准尺子状态（当前生产数据事实）：
        // S1: 🔴 FAIL (window / web-page 重叠)
        // S2: 🔴 FAIL (idle 444 分钟仅 6 次按键)
        // S4: 🔴 FAIL (定位与手机同刻重复)
        // S5: 🟢 PASS (时钟本身健康，反向验证不把好的判红)
        // S6: 🔴 FAIL (29 处无声明空档)
        // S7: 🔴 FAIL (29 处空洞未标记)
        // S10: 🔴 FAIL (分类快照补齐自 09-01 起每天产出 0)
        // S11: 🔴 FAIL (102 个批次语义不自洽)
        // S12: 🔴 FAIL (mobile_timeline_blocks 与 mobile_usage_aggregates 均 0 行)

        foreach (var key in AllRuleKeys)
        {
            RequireDetail(details, key);
        }

        RequireDetail(details, "S8_INV-C19_covered_layers");

        // 期望结论来自"生产形状数据"。镜像/开发库缺失部分数据时，对应项会返回 UNKNOWN
        // （取数失败），这时既不能判红也不能判绿 —— 因此逐项接受"期望状态 或 UNKNOWN"，
        // 但把 UNKNOWN 单独计数与打印（绝不把 UNKNOWN 当 PASS），并限制其数量：
        // 判定项大面积退化成 UNKNOWN 说明取数链路坏了，必须失败。
        (string Key, string Expected)[] expectations =
        [
            ("S1_INV-P16", "🔴 FAIL"),
            ("S2_INV-P17", "🔴 FAIL"),
            ("S3_INV-P18", "🟢 PASS"),
            ("S4_INV-C18", "🔴 FAIL"),
            ("S5_INV-P19", "🟢 PASS"),
            ("S6_INV-P20", "🔴 FAIL"),
            ("S7_INV-P21", "🔴 FAIL"),
            ("S8_INV-C19", "🟢 PASS"),
            ("S9_INV-C20", "⚪ UNKNOWN"),
            ("S10_INV-C21", "🔴 FAIL"),
            ("S11_INV-M21", "🔴 FAIL"),
            ("S12_INV-M22", "🔴 FAIL"),
            ("S13_INV-P22", "🔴 FAIL")
        ];
        var unavailable = new List<string>();
        foreach (var (key, expected) in expectations)
        {
            var actual = RequireDetail(details, key);
            if (!expected.StartsWith("⚪ UNKNOWN", StringComparison.Ordinal)
                && actual.StartsWith("⚪ UNKNOWN", StringComparison.Ordinal))
            {
                unavailable.Add($"{key}: {actual}");
                continue;
            }

            Assert.StartsWith(expected, actual);
        }

        Assert.Equal("DataField", RequireDetail(details, "S8_INV-C19_covered_layers"));
        Assert.True(result.IssueCount > 0, "生产形状数据上必须检出问题，不能是假绿灯");
        Assert.True(
            unavailable.Count <= 2,
            $"过多判定项因取数失败退化为 UNKNOWN（{unavailable.Count}）：{string.Join(" | ", unavailable)}");
        _output.WriteLine(
            $"数据不可判（UNKNOWN）的项：{unavailable.Count}"
            + (unavailable.Count == 0 ? string.Empty : $" -> {string.Join(" | ", unavailable)}"));

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
