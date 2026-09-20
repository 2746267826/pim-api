using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 真库取数烟测（#254）：把 13 条尺子的取数 SQL **真的发给 PostgreSQL 执行一遍**。
///
/// <para><b>为什么需要这个用例</b>：仓库里的取数层单测大多基于 <c>RecordingDbConnection</c>
/// —— 它只记录 SQL 文本、从不真正执行。因此 SQL 的**语法错误、未绑定参数、类型不匹配**
/// 在那些用例里全部静默通过，只有连上真库才会暴露。开发过程中就真的发生过一次：
/// S9 的 CTE 少了一个逗号，mock 用例全绿、真库直接 <c>42601 syntax error</c>。</para>
///
/// <para>本用例不断言业务结论（结论随镜像快照滚动），只断言"每一把尺子都真的跑通了"：
/// 任何一条尺子退化为 <c>取数执行异常</c> 或 <c>取数超时</c> 都视为失败。</para>
///
/// 需要 <c>PIM_MIRROR_CONN</c>；不可用时显式 Skip（仓库既定约定）。只读，不写任何表。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class LiveDbRuleSqlSmokeTests
{
    private readonly ITestOutputHelper _output;

    public LiveDbRuleSqlSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task EveryRuleSql_ExecutesWithoutSyntaxOrBindingErrors()
    {
        var connectionString = Environment.GetEnvironmentVariable("PIM_MIRROR_CONN");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "未提供 PIM_MIRROR_CONN，跳过取数 SQL 烟测。");

        // AGENTS.md 硬规则：绝不连接生产库。用断言执行，不靠注释约束。
        Assert.DoesNotContain("pim_prod", connectionString!, StringComparison.OrdinalIgnoreCase);

        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        await using var db = new PimDbContext(optionsBuilder.Options);

        var inspector = new DataReliabilityQualityInspector(
            db,
            Options.Create(new InvariantOptions()),
            NullLogger<DataReliabilityQualityInspector>.Instance);

        var report = await inspector.InspectReportAsync(DateTimeOffset.UtcNow);

        Assert.Equal(13, report.Rules.Count);

        // 取数异常/超时会被判据层翻译成这两类文案；它们代表"SQL 根本没跑通"，
        // 与基于真实数据得出的 UNKNOWN（数据源不足）有本质区别。
        string[] hardFailureMarkers = ["取数执行异常", "取数超时", "未知的尺子编号"];

        var broken = report.Rules
            .Where(rule => hardFailureMarkers.Any(marker => rule.Detail.Contains(marker, StringComparison.Ordinal)))
            .Select(rule => $"{rule.Code} ({rule.InvariantCode}): {rule.Detail}")
            .ToList();

        foreach (var rule in report.Rules)
        {
            _output.WriteLine($"{rule.Code,-4} {rule.Status,-8} {rule.Detail}");
        }

        Assert.True(broken.Count == 0,
            $"以下尺子的取数 SQL 未能真的执行成功（语法/参数绑定/类型问题）：{string.Join(" | ", broken)}");

        // 顺带验证导出通路（下钻清单）也真的能跑：它走的是另一条取数入口。
        var export = await inspector.GetViolationsAsync("S1", 5);
        Assert.Equal("S1", export.RuleCode);
    }
}
