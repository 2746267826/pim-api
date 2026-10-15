using Pim.Core.Common;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// 报告周期：Asia/Shanghai <b>业务日</b>口径下的半开 UTC 区间 <c>[StartUtc, EndUtc)</c>。
///
/// <para>
/// 业务日 = <c>[D 04:00, D+1 04:00)</c>（<see cref="BusinessDay"/>，架构决策 D-1）。
/// 日报 = 请求业务日；周报 = 该业务日所在的业务周（周一 04:00 → 下周一 04:00）；
/// 月报 = 当月 1 日 04:00 → 次月 1 日 04:00；项目报告 = 项目下任务的时间跨度。
/// </para>
///
/// <para>
/// 存储与查询一律用 UTC 绝对时刻（<see cref="BusinessDay.GetStartUtc"/> 已归一为 UTC，
/// 满足 Npgsql 对 <c>timestamp with time zone</c> 只接受 offset 0 参数的要求）。
/// </para>
/// </summary>
/// <param name="StartUtc">周期起点（含）。</param>
/// <param name="EndUtc">周期终点（不含）。</param>
/// <param name="FirstBusinessDate">周期覆盖的第一个业务日。</param>
/// <param name="LastBusinessDate">周期覆盖的最后一个业务日。</param>
/// <param name="Description">周期的人话描述（写进报告正文）。</param>
internal sealed record ReportPeriod(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateOnly FirstBusinessDate,
    DateOnly LastBusinessDate,
    string Description)
{
    /// <summary>周期的本地（Asia/Shanghai）起点，用于报告正文展示。</summary>
    public DateTimeOffset LocalStart => TimeZoneInfo.ConvertTime(StartUtc, BusinessDay.TimeZone);

    /// <summary>周期的本地（Asia/Shanghai）终点，用于报告正文展示。</summary>
    public DateTimeOffset LocalEnd => TimeZoneInfo.ConvertTime(EndUtc, BusinessDay.TimeZone);

    /// <summary>把一段时间裁剪到本周期，返回重叠时长（不相交时为 <see cref="TimeSpan.Zero"/>）。</summary>
    public TimeSpan Overlap(DateTimeOffset start, DateTimeOffset end)
    {
        var from = start > StartUtc ? start : StartUtc;
        var to = end < EndUtc ? end : EndUtc;
        return to > from ? to - from : TimeSpan.Zero;
    }
}

/// <summary>按报告种类解析报告周期（口径见 <see cref="ReportPeriod"/>）。</summary>
internal static class ReportPeriods
{
    /// <summary>日报：请求业务日当天。</summary>
    public static ReportPeriod Daily(DateOnly date)
        => Between(BusinessDay.GetStartUtc(date), BusinessDay.GetStartUtc(date.AddDays(1)),
            $"日报 · 业务日 {BusinessDay.FormatDate(date)}");

    /// <summary>周报：请求业务日所在的业务周（周一 04:00 → 下周一 04:00）。</summary>
    public static ReportPeriod Weekly(DateOnly date)
    {
        var monday = WeekStart(date);
        return Between(BusinessDay.GetStartUtc(monday), BusinessDay.GetStartUtc(monday.AddDays(7)),
            $"周报 · 业务周 {BusinessDay.FormatDate(monday)} ~ {BusinessDay.FormatDate(monday.AddDays(6))}");
    }

    /// <summary>月报：请求业务日所在的业务月（当月 1 日 04:00 → 次月 1 日 04:00）。</summary>
    public static ReportPeriod Monthly(DateOnly date)
    {
        var first = new DateOnly(date.Year, date.Month, 1);
        return Between(BusinessDay.GetStartUtc(first), BusinessDay.GetStartUtc(first.AddMonths(1)),
            $"月报 · {first:yyyy-MM}");
    }

    /// <summary>项目报告：项目下任务的时间跨度（起点由任务推导，见 ReportService）。</summary>
    public static ReportPeriod Span(DateTimeOffset startUtc, DateTimeOffset endUtc, string description)
        => Between(startUtc, endUtc, description);

    private static DateOnly WeekStart(DateOnly date)
        => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// 由 UTC 起止构造周期，并算出它覆盖的业务日范围。
    /// 业务日范围用"起点所属业务日 … 终点前一刻所属业务日"推导，项目跨度这种非整日边界也成立。
    /// </summary>
    private static ReportPeriod Between(DateTimeOffset startUtc, DateTimeOffset endUtc, string description)
    {
        var first = BusinessDay.GetBusinessDate(startUtc);
        var last = BusinessDay.GetBusinessDate(endUtc.AddTicks(-1));
        return new ReportPeriod(startUtc, endUtc, first, last, description);
    }
}
