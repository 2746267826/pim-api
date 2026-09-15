using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>物化数据能覆盖的分析面。</summary>
internal enum MobileAnalyticsSurface
{
    Aggregates,
    TimelineBlocks
}

/// <summary>
/// 判断某次分析请求是否可以由物化表回答（#247②）。
///
/// 两条硬性前提：
/// 1. 形状可复现 —— 物化时用的是"包含系统噪音 + 默认最短时长 + 不按分类/包过滤"的最大口径，
///    而 <c>minDurationSeconds</c> 会影响"哪些行参与分桶"（无法事后修正），<c>source</c> 在落库时被合并，
///    因此这两种过滤存在时一律回退在线计算；
/// 2. 覆盖对齐 —— 物化窗口按本地整日对齐，只有请求区间与某个已物化窗口逐刻相等时才可直接读取，
///    否则边界裁剪口径会与在线计算不一致。
/// </summary>
internal static class MobileAnalyticsMaterializationGate
{
    public static bool CanServeHeatmap(MobileAnalyticsQueryContext context, TimeSpan bucketSize)
        => bucketSize == TimeSpan.FromHours(1)
            && context.Granularity == MobileAnalyticsDefaults.HourGranularity
            && IsDefaultDuration(context)
            && HasSingleDevice(context)
            && string.IsNullOrWhiteSpace(context.Source);

    public static bool CanServeBlocks(MobileAnalyticsQueryContext context)
        => IsDefaultDuration(context)
            && HasSingleDevice(context)
            && string.IsNullOrWhiteSpace(context.PackageName)
            && string.IsNullOrWhiteSpace(context.LifeCategory)
            && string.IsNullOrWhiteSpace(context.Source)
            && !context.IncludeSystemNoise;

    /// <summary>是否存在一条与请求区间逐刻相等、时区一致、且范围内没有失效标记的覆盖记录。</summary>
    public static async Task<bool> IsCoveredAsync(
        PimDbContext db,
        Guid userId,
        string deviceId,
        string timezone,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        MobileAnalyticsSurface surface,
        CancellationToken ct)
    {
        var covered = await db.Set<MobileAnalyticsMaterializationEntity>()
            .AsNoTracking()
            .AnyAsync(row => row.UserId == userId
                && row.DeviceId == deviceId
                && row.Timezone == timezone
                && row.CoveredFromUtc == rangeStartUtc
                && row.CoveredToUtc == rangeEndUtc, ct);
        if (!covered)
            return false;

        var hasStale = surface switch
        {
            MobileAnalyticsSurface.Aggregates => await db.Set<MobileUsageAggregateEntity>()
                .AsNoTracking()
                .AnyAsync(row => row.UserId == userId
                    && row.DeviceId == deviceId
                    && row.IsStale
                    && row.BucketStartUtc < rangeEndUtc
                    && row.BucketEndUtc > rangeStartUtc, ct),
            _ => await db.Set<MobileTimelineBlockEntity>()
                .AsNoTracking()
                .AnyAsync(row => row.UserId == userId
                    && row.DeviceId == deviceId
                    && row.IsStale
                    && row.StartUtc < rangeEndUtc
                    && row.EndUtc > rangeStartUtc, ct)
        };

        // 失效标记（分类/规则被改动）说明派生数据不再可信：回退在线计算，
        // 等下一次上传物化时再刷新。
        return !hasStale;
    }

    private static bool HasSingleDevice(MobileAnalyticsQueryContext context)
        => !string.IsNullOrWhiteSpace(context.DeviceId);

    private static bool IsDefaultDuration(MobileAnalyticsQueryContext context)
        => context.MinDurationSeconds == MobileAnalyticsDefaults.DefaultShortEventThresholdSeconds;
}

/// <summary>物化 JSON 列的读写（与在线计算使用同一套序列化约定）。</summary>
internal static class MobileAnalyticsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeFlags(IEnumerable<string> flags)
        => JsonSerializer.Serialize(
            flags.Distinct(StringComparer.Ordinal).OrderBy(flag => flag, StringComparer.Ordinal).ToList(),
            Options);

    public static IReadOnlyList<string> DeserializeFlags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeTopApps(IReadOnlyList<MobileTimelineBlockAppDto> topApps)
        => JsonSerializer.Serialize(topApps, Options);

    public static IReadOnlyList<MobileTimelineBlockAppDto> DeserializeTopApps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<MobileTimelineBlockAppDto>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeSourceMix(IReadOnlyDictionary<string, long>? sourceMix)
        => JsonSerializer.Serialize(sourceMix ?? new Dictionary<string, long>(StringComparer.Ordinal), Options);

    public static IReadOnlyDictionary<string, long>? DeserializeSourceMix(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>由桶起点推导本地日期/小时（物化表只存 UTC 桶边界）。</summary>
internal static class MobileAnalyticsBucketLocal
{
    public static string LocalDate(DateTimeOffset bucketStartUtc, TimeZoneInfo timeZoneInfo)
        => TimeZoneInfo.ConvertTime(bucketStartUtc, timeZoneInfo)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static int LocalHour(DateTimeOffset bucketStartUtc, TimeZoneInfo timeZoneInfo, string granularity)
        => granularity == "day"
            ? 0
            : TimeZoneInfo.ConvertTime(bucketStartUtc, timeZoneInfo).Hour;
}
