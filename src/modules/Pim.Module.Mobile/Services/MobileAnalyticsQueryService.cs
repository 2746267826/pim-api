using System.Globalization;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

public sealed class MobileAnalyticsQueryService
{
    private static readonly HashSet<string> Granularities = new(StringComparer.OrdinalIgnoreCase)
    {
        "hour",
        "30m",
        "15m",
        "day"
    };

    private readonly TimeProvider _timeProvider;

    public MobileAnalyticsQueryService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public MobileAnalyticsQueryContext Normalize(MobileAnalyticsQueryRequest request)
    {
        var timezone = string.IsNullOrWhiteSpace(request.Timezone)
            ? MobileAnalyticsDefaults.DefaultTimezone
            : request.Timezone.Trim();
        var timeZoneInfo = ResolveTimezone(timezone);

        var (rangeStartUtc, rangeEndUtc) = NormalizeRange(
            request.RangeStartUtc,
            request.RangeEndUtc,
            timeZoneInfo);

        if (rangeEndUtc < rangeStartUtc)
            (rangeStartUtc, rangeEndUtc) = (rangeEndUtc, rangeStartUtc);

        var localStart = TimeZoneInfo.ConvertTime(rangeStartUtc, timeZoneInfo).Date;
        var localEnd = TimeZoneInfo.ConvertTime(rangeEndUtc.AddTicks(-1), timeZoneInfo).Date;
        var granularity = string.IsNullOrWhiteSpace(request.Granularity) || !Granularities.Contains(request.Granularity)
            ? "hour"
            : request.Granularity;
        var pageSize = request.PageSize.GetValueOrDefault(MobileAnalyticsDefaults.DefaultPageSize);
        pageSize = Math.Clamp(pageSize, 1, MobileAnalyticsDefaults.MaxPageSize);
        var page = Math.Max(1, request.Page.GetValueOrDefault(1));
        var minDurationSeconds = request.MinDurationSeconds.GetValueOrDefault(
            MobileAnalyticsDefaults.DefaultShortEventThresholdSeconds);
        minDurationSeconds = Math.Max(0, minDurationSeconds);

        return new MobileAnalyticsQueryContext(
            new MobileAnalyticsRangeDto(
                rangeStartUtc,
                rangeEndUtc,
                timezone,
                FormatDate(localStart),
                FormatDate(localEnd)),
            NormalizeString(request.DeviceId),
            NormalizeString(request.LifeCategory),
            NormalizeString(request.PackageName),
            NormalizeString(request.Source),
            request.IncludeSystemNoise.GetValueOrDefault(false),
            minDurationSeconds,
            granularity,
            NormalizeString(request.Cursor),
            page,
            pageSize);
    }

    public TimeZoneInfo ResolveTimezone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException) when (timezone == MobileAnalyticsDefaults.DefaultTimezone)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (InvalidTimeZoneException) when (timezone == MobileAnalyticsDefaults.DefaultTimezone)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }

    private (DateTimeOffset StartUtc, DateTimeOffset EndUtc) NormalizeRange(
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        TimeZoneInfo timeZoneInfo)
    {
        if (startUtc is not null && endUtc is not null)
            return (startUtc.Value, endUtc.Value);

        if (startUtc is not null)
            return (startUtc.Value, startUtc.Value.AddDays(7));

        if (endUtc is not null)
            return (endUtc.Value.AddDays(-7), endUtc.Value);

        var nowLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZoneInfo).Date;
        var startLocal = nowLocal.AddDays(-6);
        var endExclusiveLocal = nowLocal.AddDays(1);
        return (LocalDateStartUtc(startLocal, timeZoneInfo), LocalDateStartUtc(endExclusiveLocal, timeZoneInfo));
    }

    private static DateTimeOffset LocalDateStartUtc(DateTime localDate, TimeZoneInfo timeZoneInfo)
    {
        var unspecified = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZoneInfo);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string FormatDate(DateTime date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
