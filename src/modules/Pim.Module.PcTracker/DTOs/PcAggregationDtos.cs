namespace Pim.Module.PcTracker.DTOs;

/// <summary>PC 聚合接口统一查询参数（date 单日与 start&end 范围二选一；timezone 默认 Asia/Shanghai）。</summary>
public sealed record PcAggregationQuery(string? Date, string? Start, string? End, string? Timezone);

/// <summary>专注块单条。</summary>
public sealed record PcFocusBlockItem(
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string StartLocal, string EndLocal,
    int DurationMinutes, string MainApp, IReadOnlyList<PcAggregationAppMinutes> TopApps);

/// <summary>应用时长（分钟）条目，用于专注块 topApps。</summary>
public sealed record PcAggregationAppMinutes(string Name, int Minutes);

public sealed record PcFocusBlocksResponse(IReadOnlyList<PcFocusBlockItem> Items);

/// <summary>应用时长排行条目。</summary>
public sealed record PcAppUsageItem(string AppName, string? DisplayName, int TotalMinutes, double Percentage);

public sealed record PcAppUsageResponse(IReadOnlyList<PcAppUsageItem> Items, int TotalMinutes);

/// <summary>深夜使用按业务日条目。</summary>
public sealed record PcLateNightDayItem(string Date, int Minutes, bool HadActivity);

public sealed record PcLateNightResponse(IReadOnlyList<PcLateNightDayItem> Items);

/// <summary>分类分布条目。</summary>
public sealed record PcCategoryDistributionItem(string CategoryName, string Color, int Minutes, double Percentage);

public sealed record PcCategoryDistributionResponse(IReadOnlyList<PcCategoryDistributionItem> Items);
