using System.Globalization;
using Pim.Core.Common;
using Pim.Core.Quality;
using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

/// <summary>
/// 报告「收集质量 · PC」数据源：把既有 <see cref="PcTrackerQualityService"/> 的结论搬进报告。
///
/// <para>
/// **刻意不新造指标**（工单 01 §A2.2）：档位与结论用质量服务自己的，数值取既有
/// <c>tracker-events</c> 组件的 <c>eventCount</c>（采集事件数）与 <c>missingHourCount</c>
/// （缺数小时数，即该服务既有的覆盖判定依据）。组件缺席（该周期没有任何原生采集事件）时
/// 数值为 <c>null</c>，由报告写成「本周期无数据」，不填 0。
/// </para>
///
/// <para>
/// 入参换算是可核验的：<see cref="PcTrackerQualityService"/> 的窗口本来按业务日（04:00 起算）取，
/// 所以这里把报告周期换成<b>覆盖它的业务日范围</b>再调用；日报/周报/月报的周期边界与业务日对齐，
/// 换算后与报告周期完全一致，项目跨度这类非整日边界最多多覆盖不到 1 个业务日。
/// </para>
/// </summary>
public sealed class PcCollectionQualityProvider(PcTrackerQualityService qualityService) : ICollectionQualityProvider
{
    public string Key => CollectionQualitySources.Pc;

    public string Label => "PC（Windows）";

    public async Task<CollectionQualitySnapshot> GetForPeriodAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default)
    {
        var firstBusinessDate = BusinessDay.GetBusinessDate(rangeStartUtc);
        var lastBusinessDate = BusinessDay.GetBusinessDate(rangeEndUtc.AddTicks(-1));

        var quality = await qualityService.GetQualityAsync(
            null,
            firstBusinessDate.ToDateTime(TimeOnly.MinValue),
            lastBusinessDate.ToDateTime(TimeOnly.MinValue),
            ct);

        var tracker = quality.Components.FirstOrDefault(c => string.Equals(c.Key, "tracker-events", StringComparison.Ordinal));

        // 复用质量服务自己的"本库在范围内没有数据"判据（details.coverageEmpty），
        // 而不是把 0 条事件当成"采集正常"：空库与"有采集但这段时间真的没用电脑"是两回事。
        var empty = IsCoverageEmpty(tracker);
        var figures = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["Events"] = empty ? null : Figure(tracker, "eventCount"),
            ["MissingHours"] = empty ? null : Figure(tracker, "missingHourCount")
        };

        return new CollectionQualitySnapshot(
            Key,
            Label,
            quality.OverallStatus.ToString(),
            quality.Message,
            figures);
    }

    /// <summary>该周期本库完全没有数据（既有判据 <c>coverageEmpty</c>）。</summary>
    private static bool IsCoverageEmpty(PcQualityComponentDto? component)
        => component is not null
            && component.Details.TryGetValue("coverageEmpty", out var raw)
            && bool.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out var empty)
            && empty;

    /// <summary>从既有组件的 details 里取数；组件缺席或字段不可解析都算「取不到」。</summary>
    private static long? Figure(PcQualityComponentDto? component, string detailKey)
    {
        if (component is null || !component.Details.TryGetValue(detailKey, out var raw) || raw is null)
        {
            return null;
        }

        return long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
