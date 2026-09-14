using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Invariants;
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

    [Fact]
    public async Task LiveDb_InspectAsync_VerifiesGroundTruthViolations()
    {
        // 依次尝试 pim_prod (真实生产镜像) 与 pim (本地开发库)
        var connectionStrings = new[]
        {
            "Host=127.0.0.1;Port=5432;Database=pim_prod;Username=pim;Password=pim_prod_2026_home",
            "Host=127.0.0.1;Port=5432;Database=pim;Username=opencode;Password=62f0a50bb963bb648f8e400399def95a"
        };

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
            catch
            {
                // 忽略并尝试下一个
            }
        }

        if (workingConnStr == null)
        {
            _output.WriteLine("本地真实 PostgreSQL 未运行或连接失败，跳过实机校验");
            return;
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

        Assert.StartsWith("🔴 FAIL", result.Details["S1_INV-P16"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S2_INV-P17"]);
        Assert.StartsWith("🟢 PASS", result.Details["S3_INV-P18"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S4_INV-C18"]);
        Assert.StartsWith("🟢 PASS", result.Details["S5_INV-P19"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S6_INV-P20"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S7_INV-P21"]);
        Assert.StartsWith("🟢 PASS", result.Details["S8_INV-C19"]);
        Assert.Equal("DataField", result.Details["S8_INV-C19_covered_layers"]);
        Assert.StartsWith("⚪ UNKNOWN", result.Details["S9_INV-C20"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S10_INV-C21"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S11_INV-M21"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S12_INV-M22"]);
        Assert.StartsWith("🔴 FAIL", result.Details["S13_INV-P22"]);
        Assert.Equal("9 Red, 0 Yellow, 3 Green, 1 Unknown", result.Details["summary"]);
    }
}
