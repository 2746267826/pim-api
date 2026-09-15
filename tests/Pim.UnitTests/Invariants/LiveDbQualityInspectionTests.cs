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

public class LiveDbQualityInspectionTests
{
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

        foreach (var kvp in result.Details)
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

        Assert.Contains("S1_INV-P16", result.Details.Keys);
        Assert.Contains("S2_INV-P17", result.Details.Keys);
        Assert.Contains("S3_INV-P18", result.Details.Keys);
        Assert.Contains("S4_INV-C18", result.Details.Keys);
        Assert.Contains("S5_INV-P19", result.Details.Keys);
        Assert.Contains("S6_INV-P20", result.Details.Keys);
        Assert.Contains("S7_INV-P21", result.Details.Keys);
        Assert.Contains("S8_INV-C19", result.Details.Keys);
        Assert.Contains("S8_INV-C19_covered_layers", result.Details.Keys);
        Assert.Contains("S9_INV-C20", result.Details.Keys);
        Assert.Contains("S10_INV-C21", result.Details.Keys);
        Assert.Contains("S11_INV-M21", result.Details.Keys);
        Assert.Contains("S12_INV-M22", result.Details.Keys);
        Assert.Contains("S13_INV-P22", result.Details.Keys);

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
            var actual = result.Details[key];
            if (!expected.StartsWith("⚪ UNKNOWN", StringComparison.Ordinal)
                && actual.StartsWith("⚪ UNKNOWN", StringComparison.Ordinal))
            {
                unavailable.Add($"{key}: {actual}");
                continue;
            }

            Assert.StartsWith(expected, actual);
        }

        Assert.Equal("DataField", result.Details["S8_INV-C19_covered_layers"]);
        Assert.True(result.IssueCount > 0, "生产形状数据上必须检出问题，不能是假绿灯");
        Assert.True(
            unavailable.Count <= 2,
            $"过多判定项因取数失败退化为 UNKNOWN（{unavailable.Count}）：{string.Join(" | ", unavailable)}");
        _output.WriteLine(
            $"数据不可判（UNKNOWN）的项：{unavailable.Count}"
            + (unavailable.Count == 0 ? string.Empty : $" -> {string.Join(" | ", unavailable)}"));
    }
}
