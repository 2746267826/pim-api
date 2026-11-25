using System.Globalization;

namespace Pim.Module.NoteTaskSuggestions.Services;

internal static class NoteTaskSuggestionTimeZone
{
    public static bool TryFindIana(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        var trimmed = timeZoneId.Trim();
        // Windows 时区 ID 也常含空格（例如 "China Standard Time"），而
        // IANA 区域名不允许空白。先挡掉这类输入，再由 HasIanaId 拒绝运行时
        // 能识别、但不是 IANA ID 的其它别名。
        if (trimmed.Any(char.IsWhiteSpace))
            return false;

        try
        {
            var found = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            if (!found.HasIanaId)
                return false;
            timeZone = found;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static bool IsIana(string? timeZoneId) => TryFindIana(timeZoneId, out _);

    public static bool HasExplicitOffset(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z'))
            return true;

        var timeStart = trimmed.IndexOfAny(['T', 't', ' ']);
        if (timeStart < 0)
            return false;

        var timePart = trimmed[(timeStart + 1)..];
        return timePart.Contains('+') || timePart.Contains('-');
    }

    public static bool TryParseExplicitOffset(string text, out DateTimeOffset value)
    {
        var trimmed = text.Trim();
        if (!HasExplicitOffset(trimmed))
        {
            value = default;
            return false;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out value);
    }

    /// <summary>
    /// Resolve an ISO-8601 value to an absolute instant.
    /// Explicit offsets are absolute and never reinterpreted. Values without an
    /// offset are wall-clock values in <paramref name="timeZoneId"/>; missing,
    /// invalid, DST-gap and ambiguous values fail closed.
    /// </summary>
    public static bool TryConvertToUtc(
        string text,
        string? timeZoneId,
        out DateTimeOffset value,
        out string error)
    {
        value = default;
        error = "";
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            error = "必须是 ISO-8601 时间或 null";
            return false;
        }

        var zoneId = timeZoneId?.Trim();
        if (HasExplicitOffset(trimmed))
        {
            if (!TryParseExplicitOffset(trimmed, out var parsed))
            {
                error = "必须是 ISO-8601 时间或 null";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(zoneId) && !TryFindIana(zoneId, out _))
            {
                error = "timeZoneId 必须是有效的 IANA 时区";
                return false;
            }

            value = parsed.ToUniversalTime();
            return true;
        }

        if (string.IsNullOrWhiteSpace(zoneId))
        {
            error = "没有时区偏移时必须提供有效 timeZoneId";
            return false;
        }
        if (!TryFindIana(zoneId, out var timeZone))
        {
            error = "timeZoneId 必须是有效的 IANA 时区";
            return false;
        }
        if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var localDateTime))
        {
            error = "必须是 ISO-8601 时间或 null";
            return false;
        }

        var wallClock = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(wallClock))
        {
            error = "落在无效的夏令时本地时间";
            return false;
        }
        if (timeZone.IsAmbiguousTime(wallClock))
        {
            error = "落在重复的夏令时本地时间，无法唯一换算";
            return false;
        }

        value = new DateTimeOffset(wallClock, timeZone.GetUtcOffset(wallClock)).ToUniversalTime();
        return true;
    }
}
