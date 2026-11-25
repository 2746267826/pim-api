using Pim.Core.Exceptions;

namespace Pim.Module.Calendar.Services;

public static class EventQueryContract
{
    public const int MaxRangeDays = 366;

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
}
