using System.Globalization;
using System.Text.RegularExpressions;
using Pim.Core.Exceptions;

namespace Pim.Module.Calendar.Services;

public static class EventQueryContract
{
    public const int MaxRangeDays = 366;

    private static readonly Regex DateOnlyPattern = new(
        @"^\d{4}-\d{2}-\d{2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DateTimePattern = new(
        @"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:[Zz]|[+-]\d{2}:?\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// R02：REST 查询窗口与 MCP <c>get_events</c> 使用同一套 UTC 解释规则。
    /// 日期或省略偏移的时间按 UTC 处理，显式偏移标识绝对时刻；绝不使用服务器/机器时区。
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) ParseQueryRange(
        string? startRaw,
        string? endRaw)
    {
        if (string.IsNullOrWhiteSpace(startRaw) || string.IsNullOrWhiteSpace(endRaw))
            throw new DomainException(02009, "start/end 必填：日程查询必须提供明确时间范围，最长 366 天。");

        if (!TryParseQueryInstant(startRaw, out var start))
            throw new DomainException(
                02009,
                "start 格式无效：必须是 ISO-8601；日期或省略偏移的时间按 UTC 解释，显式偏移标识绝对时刻。");

        if (!TryParseQueryInstant(endRaw, out var end))
            throw new DomainException(
                02009,
                "end 格式无效：必须是 ISO-8601；日期或省略偏移的时间按 UTC 解释，显式偏移标识绝对时刻。");

        return Validate(start, end);
    }

    public static bool TryParseQueryInstant(string? raw, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64)
            return false;

        var value = raw.Trim();
        if (DateOnlyPattern.IsMatch(value))
        {
            if (!DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                return false;
            }

            instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return true;
        }

        if (!DateTimePattern.IsMatch(value))
            return false;

        var iso = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            ? value[..^1] + "+00:00"
            : value;
        var styles = HasExplicitOffset(value)
            ? DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal
            : DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, styles, out instant);
    }

    public static (DateTimeOffset Start, DateTimeOffset End) Validate(
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        if (!start.HasValue || !end.HasValue)
            throw new DomainException(02009, "start/end 必填：日程查询必须提供明确时间范围，最长 366 天。");

        if (end.Value < start.Value)
            throw new DomainException(02009, "查询范围无效：start 必须早于或等于 end。");

        if (end.Value - start.Value > TimeSpan.FromDays(MaxRangeDays))
            throw new DomainException(02009, $"查询范围过大：最长 {MaxRangeDays} 天。");

        return (start.Value, end.Value);
    }

    /// <summary>
    /// 全天事件按独立日期过滤，而 REST 仍以绝对时刻表达查询窗口。候选窗口向两侧各放宽
    /// 一天，吸收事件来源时区与显示时区之间的日界差异；最终仍由展开阶段按绝对时刻精确相交。
    /// </summary>
    public static (DateOnly Start, DateOnly EndExclusive) GetAllDayDateCandidateWindow(
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var displayStart = EventTimeZoneResolver.DisplayDate(start);
        var displayEndExclusive = EventTimeZoneResolver.DisplayDateEndExclusive(end);
        return (ShiftDateClamped(displayStart, -1), ShiftDateClamped(displayEndExclusive, 1));
    }

    private static DateOnly ShiftDateClamped(DateOnly date, int days)
    {
        var dayNumber = Math.Clamp(
            (long)date.DayNumber + days,
            DateOnly.MinValue.DayNumber,
            DateOnly.MaxValue.DayNumber);
        return DateOnly.FromDayNumber((int)dayNumber);
    }

    private static bool HasExplicitOffset(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z'))
            return true;

        var timeStart = trimmed.IndexOfAny(['T', 't', ' ']);
        if (timeStart < 0)
            return false;

        var timePart = trimmed[(timeStart + 1)..];
        return timePart.Contains('+') || timePart.Contains('-');
    }
}
