using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.Core.Invariants;

/// <summary>
/// 体检总览的口径计算（WO-RELIABILITY-WINDOW-20260928 REQ-2）。
///
/// <para><b>为什么单独抽出来</b>：总览数字是"历史欠账不得计入总览"这条口径的落点，
/// 放在取数层里就只能靠连真库才能验证；抽成纯函数后可以用构造出来的尺子结论直接钉住口径。</para>
///
/// <para><b>口径</b>：</para>
/// <list type="bullet">
///   <item><description><see cref="WindowViolations"/> = 13 条尺子的窗内违规合计 = <see cref="TotalViolations"/>
///   （总览"违规数"只由窗内违规产生，AC-2.4）；</description></item>
///   <item><description><see cref="HistoricalViolations"/> = 13 条尺子的历史欠账合计，**含绿灯尺子的欠账**
///   （绿不代表没有欠账，只是欠账不参与颜色）；</description></item>
///   <item><description><see cref="Status"/> 只由红 / 黄 / 未知条数决定（绿 + 欠账 仍是绿）。</description></item>
/// </list>
/// </summary>
public sealed record DataReliabilityInspectionSummary(
    int RedCount,
    int YellowCount,
    int GreenCount,
    int UnknownCount,
    int TotalViolations,
    int WindowViolations,
    int HistoricalViolations,
    string Status)
{
    /// <summary>按 13 条尺子的结论算出总览（顺序与颜色优先级：红 &gt; 黄 &gt; 未知 &gt; 绿）。</summary>
    public static DataReliabilityInspectionSummary Compute(IReadOnlyList<DataReliabilityRuleReport>? rules)
    {
        var list = rules ?? Array.Empty<DataReliabilityRuleReport>();

        int redCount = list.Count(rule => rule.Status == "red");
        int yellowCount = list.Count(rule => rule.Status == "yellow");
        int greenCount = list.Count(rule => rule.Status == "green");
        int unknownCount = list.Count(rule => rule.Status == "unknown");

        int windowViolations = list.Sum(rule => rule.WindowViolations);
        int historicalViolations = list.Sum(rule => rule.HistoricalViolations);

        var status = redCount > 0
            ? "red"
            : yellowCount > 0
                ? "yellow"
                : unknownCount > 0
                    ? "unknown"
                    : "green";

        return new DataReliabilityInspectionSummary(
            RedCount: redCount,
            YellowCount: yellowCount,
            GreenCount: greenCount,
            UnknownCount: unknownCount,
            TotalViolations: windowViolations,
            WindowViolations: windowViolations,
            HistoricalViolations: historicalViolations,
            Status: status);
    }
}
