using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

/// <summary>PC 聚合服务：专注块 / 应用时长 Top / 深夜使用 / 分类分布。
/// 数据源 pc_aw_events（window 事件）与 pc_activity_classifications（快照）。
/// 时区统一走 ResolveTimezone（默认 Asia/Shanghai，China Standard Time fallback），
/// 业务日窗口为本地 [D 04:00, D+1 04:00)，禁止使用依赖服务器本地时区的旧工具。</summary>
public sealed class PcActivityAggregationService
{
    private const int BusinessDayStartHour = 4;
    private const int LateNightStartHour = 23;
    private const int LateNightStartMinute = 30;
    private const int BlockMergeGapMinutes = 5;
    private const int MinFocusBlockMinutes = 10;
    /// <summary>
    /// 分类快照的单条时长上限（秒）：3600s。这是分类分布的既有口径（#301 明确保留
    /// 「先 cap 再按重叠比例分摊」），不要与下面的窗口事件上限混用。
    /// </summary>
    private const double MaxClassificationDurationSeconds = 3600;

    /// <summary>
    /// 前台窗口事件（AW / 原生 tracker）的单条时长上限（秒）。取一个业务日的长度：
    /// 它只用于防止异常值溢出，不构成低估 —— 区间随后都裁剪到业务日窗口，合计天然不超过 24 小时。
    /// 原先与分类快照共用 3600s，会把合法长事件截断（实测 tracker 存在 4170s / 9560s 的
    /// 前台窗口事件），也与概览指标的口径不一致（#303 review）。
    /// </summary>
    private const double MaxWindowEventSeconds = 24 * 60 * 60;
    private const double MinAppDurationSeconds = 60;
    private const int DefaultAppUsageLimit = 8;
    private const int MaxAppUsageLimit = 50;
    private const string DefaultTimezoneName = "Asia/Shanghai";
    private const string ChinaFallbackTimezone = "China Standard Time";
    private const string DefaultCategoryColor = "#64748b";

    private readonly PimDbContext _db;

    public PcActivityAggregationService(PimDbContext db)
    {
        _db = db;
    }

    // === 专注块 ===

    public async Task<PcFocusBlocksResponse> GetFocusBlocksAsync(PcAggregationQuery query, CancellationToken ct)
    {
        var window = ResolveWindow(query);
        var events = await LoadWindowEventsAsync(window, ct);

        var displayNames = await ResolveDisplayNamesAsync(
            events.Select(NormalizeApp).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ct);

        var items = new List<PcFocusBlockItem>();
        foreach (var block in BuildBlocks(events))
        {
            var durationMinutes = (int)Math.Round(block.MergedSeconds / 60.0);
            if (durationMinutes < MinFocusBlockMinutes)
                continue;

            var byApp = block.Intervals
                .GroupBy(i => i.App, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { App = g.Key, Seconds = g.Sum(i => (i.End - i.Start).TotalSeconds) })
                .OrderByDescending(x => x.Seconds)
                .ThenBy(x => x.App, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var mainApp = byApp.Count > 0
                ? DisplayName(byApp[0].App, displayNames)
                : "unknown";
            var topApps = byApp
                .Take(3)
                .Select(x => new PcAggregationAppMinutes(
                    DisplayName(x.App, displayNames),
                    (int)Math.Round(x.Seconds / 60.0)))
                .ToList();

            items.Add(new PcFocusBlockItem(
                block.StartUtc,
                block.EndUtc,
                FormatLocal(block.StartUtc, window.TimeZone),
                FormatLocal(block.EndUtc, window.TimeZone),
                durationMinutes,
                mainApp,
                topApps));
        }

        return new PcFocusBlocksResponse(items);
    }

    // === 应用时长 Top ===

    public async Task<PcAppUsageResponse> GetAppUsageAsync(PcAggregationQuery query, int? limit, CancellationToken ct)
    {
        var window = ResolveWindow(query);
        var clampedLimit = Math.Clamp(limit.GetValueOrDefault(DefaultAppUsageLimit), 1, MaxAppUsageLimit);
        var events = await LoadWindowEventsAsync(window, ct);

        var validEvents = events.Where(e => e.Duration > 0).ToList();
        var groupsAll = validEvents
            .GroupBy(NormalizeApp, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { App = g.Key, Seconds = SumMergedSeconds(g) })
            .OrderByDescending(x => x.Seconds)
            .ThenBy(x => x.App, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = groupsAll
            .Where(x => x.Seconds >= MinAppDurationSeconds)
            .ToList();

        // percentage 分母 = 去重后各 app 并集秒数之和（含 <60s 噪声），与 totalMinutes 同口径，跨 app 不合并以免低估多任务并行。
        var totalSeconds = groupsAll.Sum(x => x.Seconds);
        var totalMinutes = (int)Math.Round(totalSeconds / 60.0);

        var displayNames = await ResolveDisplayNamesAsync(
            groups.Select(g => g.App).ToList(), ct);

        var items = groups
            .Take(clampedLimit)
            .Select(g =>
            {
                var minutes = (int)Math.Round(g.Seconds / 60.0);
                var percentage = totalSeconds > 0 ? Math.Round(g.Seconds * 100.0 / totalSeconds, 1) : 0;
                return new PcAppUsageItem(g.App, displayNames.GetValueOrDefault(g.App), minutes, percentage);
            })
            .ToList();

        return new PcAppUsageResponse(items, totalMinutes);
    }

    // === 深夜使用 ===

    public async Task<PcLateNightResponse> GetLateNightAsync(PcAggregationQuery query, CancellationToken ct)
    {
        var window = ResolveWindow(query);
        var events = await LoadWindowEventsAsync(window, ct);

        var items = new List<PcLateNightDayItem>();
        for (var day = window.StartLocalDate; day <= window.EndLocalDate; day = day.AddDays(1))
        {
            var dayStartUtc = ToUtc(day, BusinessDayStartHour, 0, window.TimeZone);
            var dayEndUtc = ToUtc(day.AddDays(1), BusinessDayStartHour, 0, window.TimeZone);
            var lateStartUtc = ToUtc(day, LateNightStartHour, LateNightStartMinute, window.TimeZone);

            var dayEvents = events.Where(e => e.Timestamp >= dayStartUtc && e.Timestamp < dayEndUtc).ToList();
            var lateSeconds = SumMergedSeconds(dayEvents.Where(e => e.Timestamp >= lateStartUtc));
            var minutes = (int)Math.Round(lateSeconds / 60.0);

            items.Add(new PcLateNightDayItem(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                minutes,
                dayEvents.Count > 0));
        }

        return new PcLateNightResponse(items);
    }

    // === 分类分布 ===

    public async Task<PcCategoryDistributionResponse> GetCategoryDistributionAsync(PcAggregationQuery query, CancellationToken ct)
    {
        var window = ResolveWindow(query);
        var snapshots = await _db.Set<ActivityClassificationEntity>()
            .Where(s => s.StartedAt < window.EndUtc && s.EndedAt > window.StartUtc)
            .ToListAsync(ct);

        // cap 按事件总时长先 cap 再按 overlap 比例分摊，避免跨天分片各自 cap 导致膨胀
        static double OverlapSeconds(ActivityClassificationEntity s, DateTimeOffset start, DateTimeOffset end, double cap)
        {
            var totalSeconds = (s.EndedAt - s.StartedAt).TotalSeconds;
            if (totalSeconds <= 0) return 0;
            var cappedTotal = Math.Min(totalSeconds, cap);
            var overlapStart = s.StartedAt > start ? s.StartedAt : start;
            var overlapEnd = s.EndedAt < end ? s.EndedAt : end;
            var overlapSeconds = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
            if (overlapSeconds <= 0) return 0;
            // 按重叠占比分摊 capped 总量
            return cappedTotal * (overlapSeconds / totalSeconds);
        }

        var groups = snapshots
            .GroupBy(s => s.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Category = g.Key,
                Seconds = g.Sum(s => OverlapSeconds(s, window.StartUtc, window.EndUtc, MaxClassificationDurationSeconds)),
                Color = ResolveCategoryColor(g.Key, g.Select(s => s.CategoryColor))
            })
            .OrderByDescending(x => x.Seconds)
            .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalSeconds = groups.Sum(g => g.Seconds);
        var totalMinutes = (int)Math.Round(totalSeconds / 60.0);
        var items = groups
            .Select(g =>
            {
                var minutes = (int)Math.Round(g.Seconds / 60.0);
                var percentage = totalSeconds > 0 ? Math.Round(g.Seconds * 100.0 / totalSeconds, 1) : 0;
                return new PcCategoryDistributionItem(g.Category, g.Color, minutes, percentage);
            })
            .ToList();
        // 百分比和校正：确保四舍五入后和为100（INV-P11/C05要求 |sum-100|<=1）
        if (items.Count > 0)
        {
            var sumPct = items.Sum(i => i.Percentage);
            var diff = Math.Round(100.0 - sumPct, 1);
            if (Math.Abs(diff) > 0.05 && Math.Abs(diff) <= 1.0)
            {
                var idx = items.Count - 1;
                var last = items[idx];
                items[idx] = new PcCategoryDistributionItem(last.CategoryName, last.Color, last.Minutes, Math.Round(last.Percentage + diff, 1));
            }
        }

        return new PcCategoryDistributionResponse(items);
    }

    /// <summary>分类颜色兜底：快照 CategoryColor 合法（# + 6 位十六进制）→ 用之；
    /// 否则 CategoryLegacyMapper.UnifiedColors 按分类名取；再兜底 #64748b。</summary>
    private static string ResolveCategoryColor(string categoryName, IEnumerable<string?> snapshotColors)
    {
        foreach (var color in snapshotColors)
        {
            if (IsValidHexColor(color))
                return color!;
        }
        return CategoryLegacyMapper.UnifiedColors.TryGetValue(categoryName, out var unified)
            ? unified
            : DefaultCategoryColor;
    }

    private static bool IsValidHexColor(string? color)
        => !string.IsNullOrWhiteSpace(color)
            && color.Length == 7
            && color[0] == '#'
            && color[1..].All(char.IsAsciiHexDigit);

    // === 共享 ===

    /// <summary>解析查询窗口：date 单日 → 单业务日；start&end → [start 04:00, end+1 04:00) 本地。
    /// date 与范围同传以 date 为准；start &gt; end 抛 ArgumentException。</summary>
    private static PcQueryWindow ResolveWindow(PcAggregationQuery query)
    {
        var timeZone = ResolveTimezone(query.Timezone);
        DateTime startDate, endDate;
        if (!string.IsNullOrWhiteSpace(query.Date))
        {
            startDate = ParseDate(query.Date);
            endDate = startDate;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(query.Start) || string.IsNullOrWhiteSpace(query.End))
                throw new ArgumentException("请提供 date 或 start&end 参数。");
            startDate = ParseDate(query.Start);
            endDate = ParseDate(query.End);
            if (endDate < startDate)
                throw new ArgumentException("start 不能晚于 end。");
        }

        var startUtc = ToUtc(startDate, BusinessDayStartHour, 0, timeZone);
        var endUtc = ToUtc(endDate.AddDays(1), BusinessDayStartHour, 0, timeZone);
        return new PcQueryWindow(startUtc, endUtc, timeZone, startDate, endDate);
    }

    private static DateTime ParseDate(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture).Date;

    /// <summary>本地日期 D 的业务日窗口 [D 04:00, D+1 04:00) 换算为 UTC。</summary>
    private static DateTimeOffset ToUtc(DateTime localDate, int hour, int minute, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDate.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    /// <summary>时区解析：空值默认 Asia/Shanghai；该默认值在系统缺失时报 China Standard Time 兜底。</summary>
    private static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        var name = string.IsNullOrWhiteSpace(timezone) ? DefaultTimezoneName : timezone.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (TimeZoneNotFoundException) when (name == DefaultTimezoneName)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ChinaFallbackTimezone);
        }
        catch (InvalidTimeZoneException) when (name == DefaultTimezoneName)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ChinaFallbackTimezone);
        }
    }

    /// <summary>
    /// 载入聚合口径的前台窗口事件（#303）：AW 的 window 事件（排除 afk）并上
    /// <c>pc_tracker_events</c> 的 window 事件。
    /// <para>
    /// <c>pc_aw_events</c> 在原生 tracker 切换后已停止写入，只读它会让专注块 / 应用时长 /
    /// 深夜使用在 tracker-only 日期恒为空。按「时间 + 时长 + 应用」去重，避免两路来源
    /// 覆盖同一事件时重复计数。返回结果按时间升序。
    /// </para>
    /// </summary>
    private async Task<List<AggregationEvent>> LoadWindowEventsAsync(PcQueryWindow window, CancellationToken ct)
    {
        // 按**区间重叠**查询，而不是「起点落在窗口内」：窗口开始前开始、但延伸进窗口的
        // 长事件同样属于该业务日，只按起点筛选会整条丢弃（#303 review；镜像中有 26 条
        // AW window 事件跨 04:00 边界）。
        var windowStart = window.StartUtc.AddSeconds(-MaxWindowEventSeconds);
        var awEvents = await _db.Set<AwEventEntity>()
            .Where(e => e.EventType == "window"
                && (e.AfkStatus == null || e.AfkStatus != "afk")
                && e.Duration > 0
                && e.Timestamp >= windowStart
                && e.Timestamp < window.EndUtc)
            .Select(e => new { e.Timestamp, e.Duration, e.AppName, e.AppNameNormalized })
            .ToListAsync(ct);
        var trackerEvents = await _db.Set<TrackerEventEntity>()
            .Where(e => e.EventType == "window"
                && e.Duration > 0
                && e.Timestamp >= windowStart
                && e.Timestamp < window.EndUtc)
            .Select(e => new { e.Timestamp, e.Duration, e.AppName })
            .ToListAsync(ct);

        var seen = new HashSet<(long Ticks, long DurationMs, string App)>();
        var result = new List<AggregationEvent>(awEvents.Count + trackerEvents.Count);

        void Add(DateTimeOffset timestamp, double duration, string? appName)
        {
            var normalized = AppNameNormalizer.Normalize(appName);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            // 去重键基于**原始**事件身份（时刻 / 时长 / 应用），而不是裁剪后的区间：
            // 裁剪结果只反映「落在本业务日内的部分」，两条原始身份不同的长事件可能裁剪出
            // 完全相同的键而互相吞掉（当前各端点都是并集口径，故数值暂不可见，但键本身
            // 不应依赖裁剪结果）；同时与概览指标 / 热力图的去重口径保持一致（#303 review）。
            var cappedSeconds = Math.Min(duration, MaxWindowEventSeconds);
            if (!seen.Add((timestamp.UtcTicks, (long)Math.Round(cappedSeconds * 1000), normalized.ToLowerInvariant())))
                return;

            // 裁剪到业务日窗口：跨入 / 跨出的部分不计入本日。
            var cappedEnd = timestamp.AddSeconds(cappedSeconds);
            var start = timestamp > window.StartUtc ? timestamp : window.StartUtc;
            var end = cappedEnd < window.EndUtc ? cappedEnd : window.EndUtc;
            if (end <= start)
                return;

            result.Add(new AggregationEvent(start, (end - start).TotalSeconds, normalized));
        }

        foreach (var e in awEvents)
            Add(e.Timestamp, e.Duration, e.AppNameNormalized ?? e.AppName);
        foreach (var e in trackerEvents)
            Add(e.Timestamp, e.Duration, e.AppName);

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    private static string NormalizeApp(AggregationEvent e)
        => AppNameNormalizer.Normalize(e.AppName);

    private static string DisplayName(string app, IReadOnlyDictionary<string, string> displayNames)
        => displayNames.GetValueOrDefault(app, app);

    private static string FormatLocal(DateTimeOffset utc, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTime(utc, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private async Task<Dictionary<string, string>> ResolveDisplayNamesAsync(List<string> apps, CancellationToken ct)
    {
        if (apps.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var signatures = await _db.Set<AppSignatureEntity>()
            .Select(s => new { s.ProcessName, s.DisplayName })
            .ToListAsync(ct);

        return AppSignatureMatcher.ResolveDisplayNames(
            apps,
            signatures.Select(s => (s.ProcessName ?? string.Empty, s.DisplayName ?? string.Empty)));
    }

    /// <summary>专注块合并：先按 app 去重合并重叠区间，再按 5min 间隙切分，块时长为跨度（末结束-首开始，含 ≤5m 间隙）。</summary>
    private static List<PcFocusBlock> BuildBlocks(List<AggregationEvent> events)
    {
        // 1) 去重：按 app 分组各自按 Timestamp 合并重叠区间（capped 3600，Duration>0）
        var valid = events.Where(e => e.Duration > 0).ToList();
        var intervals = new List<PcInterval>();
        foreach (var group in valid.GroupBy(NormalizeApp, StringComparer.OrdinalIgnoreCase))
        {
            var merged = MergeIntervals(group.OrderBy(e => e.Timestamp).ToList());
            foreach (var m in merged)
                intervals.Add(new PcInterval(m.Start, m.End, group.Key));
        }
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        // 2) 按 5m 间隙切块，块时长按去重后并集总时长（不计间隙），避免膨胀
        var blocks = new List<PcFocusBlock>();
        List<PcInterval>? current = null;
        DateTimeOffset currentEnd = default;
        DateTimeOffset blockStart = default;
        double blockMergedSeconds = 0;
        foreach (var iv in intervals)
        {
            if (current is null)
            {
                current = new List<PcInterval> { iv };
                blockStart = iv.Start;
                currentEnd = iv.End;
                blockMergedSeconds = (iv.End - iv.Start).TotalSeconds;
                continue;
            }

            if (iv.Start <= currentEnd.AddMinutes(BlockMergeGapMinutes))
            {
                current.Add(iv);
                blockMergedSeconds += (iv.End - iv.Start).TotalSeconds;
                if (iv.End > currentEnd)
                    currentEnd = iv.End;
            }
            else
            {
                blocks.Add(new PcFocusBlock(blockStart, currentEnd, current, blockMergedSeconds));
                current = new List<PcInterval> { iv };
                blockStart = iv.Start;
                currentEnd = iv.End;
                blockMergedSeconds = (iv.End - iv.Start).TotalSeconds;
            }
        }

        if (current is not null)
        {
            blocks.Add(new PcFocusBlock(blockStart, currentEnd, current, blockMergedSeconds));
        }
        return blocks;
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(List<AggregationEvent> sortedEvents)
    {
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var e in sortedEvents)
        {
            var s = e.Timestamp;
            var en = e.Timestamp.AddSeconds(Math.Min(e.Duration, MaxWindowEventSeconds));
            if (result.Count == 0)
            {
                result.Add((s, en));
                continue;
            }
            var last = result[^1];
            if (s <= last.End)
            {
                if (en > last.End)
                    result[^1] = (last.Start, en);
            }
            else
            {
                result.Add((s, en));
            }
        }
        return result;
    }

    private static double SumMergedSeconds(IEnumerable<AggregationEvent> events)
    {
        var filtered = events.Where(e => e.Duration > 0).OrderBy(e => e.Timestamp).ToList();
        if (filtered.Count == 0) return 0;
        double total = 0;
        var curStart = filtered[0].Timestamp;
        var curEnd = filtered[0].Timestamp.AddSeconds(Math.Min(filtered[0].Duration, MaxWindowEventSeconds));
        for (var i = 1; i < filtered.Count; i++)
        {
            var s = filtered[i].Timestamp;
            var en = filtered[i].Timestamp.AddSeconds(Math.Min(filtered[i].Duration, MaxWindowEventSeconds));
            if (s <= curEnd)
            {
                if (en > curEnd) curEnd = en;
            }
            else
            {
                total += (curEnd - curStart).TotalSeconds;
                curStart = s;
                curEnd = en;
            }
        }
        total += (curEnd - curStart).TotalSeconds;
        return total;
    }

    private sealed record PcQueryWindow(
        DateTimeOffset StartUtc, DateTimeOffset EndUtc, TimeZoneInfo TimeZone,
        DateTime StartLocalDate, DateTime EndLocalDate);

    /// <summary>
    /// 聚合口径下的统一「前台窗口」事件（#303）。AW (<c>pc_aw_events</c>) 与原生 tracker
    /// (<c>pc_tracker_events</c>) 都归一为该形状，使专注块 / 应用时长 / 深夜使用三个端点
    /// 不再依赖已停写的 AW 事件流。
    /// </summary>
    private sealed record AggregationEvent(DateTimeOffset Timestamp, double Duration, string AppName);

    private sealed record PcInterval(DateTimeOffset Start, DateTimeOffset End, string App);

    private sealed record PcFocusBlock(
        DateTimeOffset StartUtc, DateTimeOffset EndUtc, List<PcInterval> Intervals, double MergedSeconds);
}
