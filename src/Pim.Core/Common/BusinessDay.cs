using System.Globalization;

namespace Pim.Core.Common;

/// <summary>
/// 业务日口径统一工具（EPIC #254 · 架构决策 D-1）。
/// <para>
/// 业务日 D = <c>[D 04:00, D+1 04:00)</c>，以 <c>Asia/Shanghai</c>（UTC+8）为基准，左闭右开。
/// 北京时间 00:00–04:00 的活动属于前一天（用户作息偏晚，按午夜切会把一段连续工作劈成两天）。
/// </para>
/// <para>
/// 存储永远只存 UTC 绝对时间；"哪一天"只作为查询与聚合口径存在。
/// 数据字段的日期标签 / 按日接口的查询窗口 / 页面展示的业务日必须共用这一条线。
/// </para>
/// </summary>
public static class BusinessDay
{
    /// <summary>业务日起算小时（Asia/Shanghai 本地时间 04:00）。</summary>
    public const int StartHour = 4;

    private const string PrimaryTimeZoneId = "Asia/Shanghai";
    private const string WindowsTimeZoneId = "China Standard Time";

    /// <summary>业务日使用的时区（中国无夏令时，无需 DST 处理）。</summary>
    public static TimeZoneInfo TimeZone { get; } = ResolveTimeZone();

    /// <summary>获取某个绝对时刻所属的业务日。</summary>
    public static DateOnly GetBusinessDate(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, TimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return local.Hour < StartHour ? date.AddDays(-1) : date;
    }

    /// <summary>获取某个业务日在 UTC 时间轴上的起点（即当日北京时间 04:00，闭区间）。</summary>
    public static DateTimeOffset GetStartUtc(DateOnly date)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, StartHour, 0, 0, DateTimeKind.Unspecified);
        var offset = TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>获取某个业务日在 UTC 时间轴上的终点（即次日北京时间 04:00，开区间）。</summary>
    public static DateTimeOffset GetEndUtc(DateOnly date) => GetStartUtc(date.AddDays(1));

    /// <summary>获取某个业务日对应的半开 UTC 查询窗口 <c>[start, end)</c>。</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetRangeUtc(DateOnly date)
        => (GetStartUtc(date), GetEndUtc(date));

    /// <summary>按 <c>yyyy-MM-dd</c> 格式化业务日。</summary>
    public static string FormatDate(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>解析 <c>yyyy-MM-dd</c> 形式的业务日。</summary>
    public static bool TryParseDate(string? value, out DateOnly date)
        => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { PrimaryTimeZoneId, WindowsTimeZoneId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // 尝试下一个候选时区
            }
            catch (InvalidTimeZoneException)
            {
                // 尝试下一个候选时区
            }
        }

        // 最后兜底：固定 UTC+8（中国自 1991 年起不再使用夏令时）
        return TimeZoneInfo.CreateCustomTimeZone("Asia/Shanghai (UTC+8)", TimeSpan.FromHours(8), "Asia/Shanghai", "CST");
    }
}
