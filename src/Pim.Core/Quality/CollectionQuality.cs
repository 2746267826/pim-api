namespace Pim.Core.Quality;

/// <summary>
/// 报告「收集质量」模块的一路数据源（手机 / PC）在某个周期内的覆盖结论。
///
/// <para>
/// 刻意只做**搬运**：取值全部来自各模块既有的质量服务（手机 <c>MobileQualityService</c>、
/// PC <c>PcTrackerQualityService</c>）已经算好的口径与字段，本抽象不新造任何指标定义
/// （工单 01 §A2.2「复用现成服务即可…不要新造指标口径」）。
/// </para>
/// </summary>
/// <param name="Key">数据源标识，见 <see cref="CollectionQualitySources"/>。</param>
/// <param name="Label">面向用户的数据源名称（报告正文用）。</param>
/// <param name="Status">既有质量服务的整体档位（Healthy/Warning/Critical/Unknown 的字符串形式）。</param>
/// <param name="Conclusion">既有质量服务给出的结论文字（报告正文直接采用）。</param>
/// <param name="Figures">
/// 该数据源在周期内的数值结论，键为报告字段后缀（报告会拼成 <c>{Key}{后缀}</c>）。
/// 值为 <c>null</c> 表示该口径在本周期取不到数据（报告须显式标注"本周期无数据"，不得填 0）。
/// </param>
public sealed record CollectionQualitySnapshot(
    string Key,
    string Label,
    string Status,
    string Conclusion,
    IReadOnlyDictionary<string, long?> Figures)
{
    /// <summary>本周期是否至少有一个可用的数值结论。</summary>
    public bool HasAnyFigure => Figures.Values.Any(value => value.HasValue);
}

/// <summary>
/// 报告「收集质量」模块的数据源提供方：由各采集模块（Mobile / PcTracker）实现，
/// 报告服务经 DI 可选消费。缺某个实现时报告不会失败，只是该数据源显式标注「本周期无数据」。
/// </summary>
public interface ICollectionQualityProvider
{
    /// <summary>数据源标识，见 <see cref="CollectionQualitySources"/>。</summary>
    string Key { get; }

    /// <summary>面向用户的数据源名称。</summary>
    string Label { get; }

    /// <summary>取该数据源在 <c>[rangeStartUtc, rangeEndUtc)</c> 周期内的覆盖结论。</summary>
    Task<CollectionQualitySnapshot> GetForPeriodAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default);
}

/// <summary>
/// 报告「收集质量」模块的已知数据源。报告对**每一个**已知数据源都要出字段
/// （取不到就标注「本周期无数据」），因此这份清单是报告字段名的稳定来源：
/// 新增数据源时在这里加一项，并由对应模块实现 <see cref="ICollectionQualityProvider"/>。
/// </summary>
public static class CollectionQualitySources
{
    public const string Mobile = "mobile";
    public const string Pc = "pc";

    public static readonly IReadOnlyList<string> All = [Mobile, Pc];
}
