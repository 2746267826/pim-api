using System.Globalization;
using Pim.Core.Operations;
using Pim.Core.Quality;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 报告「收集质量 · 手机」数据源：把既有 <see cref="MobileQualityService"/> 的结论搬进报告。
///
/// <para>
/// **刻意不新造指标**（工单 01 §A2.2）：档位用质量服务自己的整体档位，结论用它的文案，
/// 数值直接用既有组件已有的字段 ——「使用事件数」取自 <c>mobile-usage-coverage</c> 的
/// <c>eventCount</c>，「定位点」取自 <c>mobile-location</c> 的 <c>locationPointCount</c>。
/// 组件缺席或判为 Unknown（本周期没有这一路数据）时对应数值为 <c>null</c>，
/// 由报告写成「本周期无数据」，不填 0。
/// </para>
/// </summary>
public sealed class MobileCollectionQualityProvider(MobileQualityService qualityService) : ICollectionQualityProvider
{
    public string Key => CollectionQualitySources.Mobile;

    public string Label => "手机（Android）";

    public async Task<CollectionQualitySnapshot> GetForPeriodAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default)
    {
        var quality = await qualityService.GetQualityAsync(rangeStartUtc, rangeEndUtc, ct);

        var usage = Component(quality, "mobile-usage-coverage");
        var location = Component(quality, "mobile-location");

        var figures = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["UsageEvents"] = Figure(usage, "eventCount"),
            ["LocationPoints"] = Figure(location, "locationPointCount")
        };

        return new CollectionQualitySnapshot(
            Key,
            Label,
            quality.OverallStatus.ToString(),
            quality.Message,
            figures);
    }

    private static MobileQualityComponentDto? Component(MobileQualityResponse quality, string key)
        => quality.Components.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));

    /// <summary>从既有组件的 details 里取数；组件缺席、判为 Unknown、或字段不可解析都算「取不到」。</summary>
    private static long? Figure(MobileQualityComponentDto? component, string detailKey)
    {
        if (component is null || component.Status == PimHealthStatus.Unknown)
        {
            return null;
        }

        if (!component.Details.TryGetValue(detailKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
