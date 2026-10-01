using System.Text.RegularExpressions;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20261001 · REQ-3（#372）：体检冷缓存路径不得再对大表做无索引的
/// <c>MAX(duration)</c>。
/// <para>
/// 修复前实测（克隆库，231,666 行 <c>pc_aw_events</c>）：<c>max(duration)</c> 走
/// <c>Parallel Seq Scan</c>，349 ms；<c>pc_tracker_events</c> 走 <c>Seq Scan</c>，22.6 ms。
/// 两张表都没有 <c>duration</c> 索引，而该聚合在每次缓存未命中时都会执行（匿名 GET 即可触发）。
/// </para>
/// <para>
/// 这里把「两张事件表必须有 <c>duration</c> 索引」固定成回归断言：索引是本条 AC 的交付物本身，
/// 丢了索引就退回顺序扫描，而单测跑 EF InMemory 看不见执行计划。
/// </para>
/// </summary>
public sealed class PcTrackerDurationIndexTests
{
    [Theory]
    [InlineData("pc_aw_events")]
    [InlineData("pc_tracker_events")]
    public void SchemaSql_IndexesDurationColumnOnEventTable(string table)
    {
        var indexes = ParseIndexes(PcTrackerSchemaInitializer.SchemaSql, table);

        Assert.True(
            indexes.Any(columns => columns.Count == 1
                                   && columns[0].Equals("duration", StringComparison.OrdinalIgnoreCase)),
            $"{table} 缺少 duration 单列索引：体检冷缓存路径的 max(duration) 会退化为全表顺序扫描。" +
            $"现有索引：{string.Join(" | ", indexes.Select(columns => string.Join(",", columns)))}");
    }

    [Theory]
    [InlineData("pc_aw_events")]
    [InlineData("pc_tracker_events")]
    public void SchemaSql_DurationIndexIsCreatedIdempotently(string table)
    {
        // SchemaSql 每次启动都整体执行一遍，索引必须带 IF NOT EXISTS，否则重复启动会失败。
        var pattern = $@"CREATE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+\S+\s+ON\s+{table}\s*\(\s*duration\s*\)";
        Assert.Matches(new Regex(pattern, RegexOptions.IgnoreCase), PcTrackerSchemaInitializer.SchemaSql);
    }

    /// <summary>取出 <paramref name="table"/> 上所有索引的列清单（按出现顺序）。</summary>
    private static List<List<string>> ParseIndexes(string schemaSql, string table)
    {
        var pattern = $@"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?\S+\s+ON\s+{table}\s*\(([^)]*)\)";
        return Regex.Matches(schemaSql, pattern, RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList())
            .ToList();
    }
}
