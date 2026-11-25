using System.Globalization;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// R02：把事件来源时区统一解析为 IANA，并显式区分 resolved/sourceOnly/unknown。
/// <para>
/// 绝对时刻 <c>dtStart/dtEnd</c> 不参与“纠偏”。解析器只描述该时刻原本属于哪个时区，
/// 原始 Windows/IANA 字符串始终保留在 <see cref="EventTimeZoneInfo.SourceId"/>。
/// </para>
/// </summary>
public static class EventTimeZoneResolver
{
    public const string DefaultDisplayTimeZoneId = "Asia/Shanghai";
    public const string Resolved = "resolved";
    public const string SourceOnly = "sourceOnly";
    public const string Unknown = "unknown";
    public const string IanaIdType = "iana";
    public const string WindowsSourceType = "windows";
    public const string IanaSourceType = "iana";
    public const string UnknownSourceType = "unknown";

    private static readonly TimeZoneInfo DisplayTimeZone = ResolveDisplayTimeZone();

    public static EventTimeZoneInfo Resolve(
        string? timeZoneId,
        string? sourceTimeZoneId,
        string? originalStartTimeZone = null)
    {
        var source = FirstNonBlank(sourceTimeZoneId, originalStartTimeZone, timeZoneId);
        if (source is null)
            return EventTimeZoneInfo.Unresolved(null);

        if (TryResolveId(timeZoneId, out var normalized, out _))
        {
            var resolvedOriginalSourceType = TryResolveId(source, out _, out var resolvedSourceType)
                ? resolvedSourceType
                : UnknownSourceType;
            return EventTimeZoneInfo.ResolvedInfo(normalized, source, resolvedOriginalSourceType);
        }

        if (TryResolveId(source, out var fromSource, out var sourceType))
            return EventTimeZoneInfo.ResolvedInfo(fromSource, source, sourceType);

        return EventTimeZoneInfo.Unresolved(source);
    }

    public static TimeZoneInfo DisplayZone => DisplayTimeZone;

    public static TimeZoneInfo DefaultDisplayTimeZone => DisplayTimeZone;

    public static bool TryResolveIana(
        string? value,
        out string ianaId,
        out TimeZoneInfo zone,
        out string? sourceType)
    {
        ianaId = string.Empty;
        zone = TimeZoneInfo.Utc;
        sourceType = null;

        if (!TryResolveId(value, out ianaId, out var resolvedSourceType))
            return false;

        zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        sourceType = resolvedSourceType;
        return true;
    }

    public static DateTimeOffset ConvertToZone(DateTimeOffset instant, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(instant, zone);

    public static DateTimeOffset LocalWallClockToInstant(
        DateTime localWallClock,
        TimeZoneInfo zone,
        TimeSpan? preferredOffset = null)
    {
        var local = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            var offsetBeforeGap = zone.GetUtcOffset(local.AddDays(-1));
            var utcWallClock = local - offsetBeforeGap;
            return new DateTimeOffset(
                DateTime.SpecifyKind(utcWallClock, DateTimeKind.Unspecified),
                TimeSpan.Zero);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var offset = preferredOffset is { } preferred && offsets.Contains(preferred)
                ? preferred
                : offsets.Max();
            return new DateTimeOffset(local, offset);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static DateTimeOffset StartOfLocalDate(
        DateOnly date,
        TimeZoneInfo? zone,
        TimeSpan fallbackOffset)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return (zone is null
                ? new DateTimeOffset(local, fallbackOffset)
                : LocalWallClockToInstant(local, zone))
            .ToUniversalTime();
    }

    public static string FormatLocal(DateTimeOffset instant)
        => FormatLocal(instant, DisplayTimeZone);

    public static string FormatLocal(DateTimeOffset instant, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(instant, zone)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static DateTimeOffset DisplayDateStartUtc(DateOnly date)
        => ToUtc(date, TimeOnly.MinValue, DisplayTimeZone);

    public static DateOnly DisplayDate(DateTimeOffset instant)
        => instant == DateTimeOffset.MinValue
            ? DateOnly.MinValue
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, DisplayTimeZone).DateTime);

    public static DateOnly DisplayDateEndExclusive(DateTimeOffset instant)
    {
        if (instant == DateTimeOffset.MaxValue)
            return DateOnly.MaxValue;
        var local = TimeZoneInfo.ConvertTime(instant, DisplayTimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return local.TimeOfDay == TimeSpan.Zero ? date : date.AddDays(1);
    }

    private static bool TryResolveId(string? raw, out string ianaId, out string sourceType)
    {
        ianaId = string.Empty;
        sourceType = UnknownSourceType;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (value is "UTC" or "Etc/UTC" or "GMT")
        {
            ianaId = "UTC";
            sourceType = IanaSourceType;
            return true;
        }

        if (value.Contains('/', StringComparison.Ordinal)
            && TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
        {
            ianaId = value;
            sourceType = IanaSourceType;
            return true;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(value, out var converted)
            && !string.IsNullOrWhiteSpace(converted)
            && TimeZoneInfo.TryFindSystemTimeZoneById(converted, out _))
        {
            ianaId = converted;
            sourceType = WindowsSourceType;
            return true;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(value, out var direct))
        {
            if (direct.Id.Contains('/', StringComparison.Ordinal))
            {
                ianaId = direct.Id;
                sourceType = IanaSourceType;
                return true;
            }
        }

        return false;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static TimeZoneInfo ResolveDisplayTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultDisplayTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }
}

public sealed record EventTimeZoneInfo(
    string Resolution,
    string? Id,
    string? SourceId,
    string? SourceType,
    TimeZoneInfo? Zone)
{
    public bool IsResolved => Resolution == EventTimeZoneResolver.Resolved && Zone is not null;

    public static EventTimeZoneInfo ResolvedInfo(string ianaId, string sourceId, string sourceType)
        => new(
            EventTimeZoneResolver.Resolved,
            ianaId,
            sourceId,
            sourceType,
            TimeZoneInfo.FindSystemTimeZoneById(ianaId));

    public static EventTimeZoneInfo Unresolved(string? sourceId)
        => new(
            sourceId is null ? EventTimeZoneResolver.Unknown : EventTimeZoneResolver.SourceOnly,
            null,
            sourceId,
            sourceId is null ? null : EventTimeZoneResolver.UnknownSourceType,
            null);
}
