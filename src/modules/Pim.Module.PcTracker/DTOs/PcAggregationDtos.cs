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

/// <summary>
/// 键鼠范围聚合（REQ-7 / #368，<c>GET /api/v1/pc/aggregation/keystats?start&amp;end[&amp;timezone]</c>）。
/// <para>
/// 字段与单日版 <c>pc/summary.keystats</c> **同构**（同名同量纲），前端可复用同一套组件：
/// <see cref="KeyPressCounts"/> 是按 <c>keyName</c> 合并后的整段分布（规模由不同按键数决定，
/// 不随天数线性膨胀），<see cref="TopKeys"/> 只留 TopN；<see cref="TotalClicks"/> 与单日版口径一致，
/// 含中键与侧键，因此不一定等于 Left+Middle+Right。
/// </para>
/// <para>
/// 取数与单日版一致：每个业务日取该日**最近写入**的一条 keystats 快照。
/// </para>
/// </summary>
public sealed record PcKeystatsRangeResponse(
    IReadOnlyDictionary<string, int> KeyPressCounts,
    IReadOnlyList<KeyCountItem> TopKeys,
    int LeftClicks,
    int MiddleClicks,
    int RightClicks,
    double ScrollDistance,
    int PeakKps,
    int PeakCps,
    int TotalKeyPresses,
    int TotalClicks,
    // 以下 4 个字段是「与单日版同构」的补充（REQ-7 / AC-7.3：前端复用同一组件）：
    // 单日 KeystatsSummary 用 keyPresses / 侧键 / mouseDistance，范围版必须同名给出，
    // 否则 KeyboardHeatmap 之类按单日字段取数的组件在范围模式下仍然取不到值。
    int KeyPresses = 0,
    int SideBackClicks = 0,
    int SideForwardClicks = 0,
    double MouseDistance = 0);
