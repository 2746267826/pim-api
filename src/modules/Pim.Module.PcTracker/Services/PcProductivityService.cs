using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class PcProductivityService
{
    private const int BusinessDayStartHour = 4;
    private const string DefaultTimezoneName = "Asia/Shanghai";
    private const string ChinaFallbackTimezone = "China Standard Time";

    /// <summary>时间线碎片段下限（#237）：短于该值的块视为噪声丢弃，与
    /// <see cref="PcActivityAggregationService"/> 的 60 秒应用时长下限保持一致。</summary>
    private static readonly TimeSpan MinTimelineSegment = TimeSpan.FromSeconds(60);

    /// <summary>原生事件时长上限：与 <see cref="PcActivityAggregationService"/> 的窗口事件截断口径一致。</summary>
    private const double MaxTrackerEventSeconds = 3600;

    private readonly PimDbContext _db;
    private readonly TimeProvider _timeProvider;

    public PcProductivityService(PimDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 解析按日查询的目标业务日（EPIC #254 D-1）：调用方显式给出日期时按该自然日；
    /// 省略时取「现在」所属的<b>业务日</b>——北京时间凌晨 0–4 点仍算前一天，
    /// 不再依赖服务器本地日历日（服务器时区未必是 Asia/Shanghai）。
    /// </summary>
    public DateTime ResolveBusinessDay(DateTime? date)
        => date?.Date ?? BusinessDayForTimestamp(_timeProvider.GetUtcNow());

    public async Task<ProductivityDashboardDto> GetDashboardAsync(DateTime? date, CancellationToken ct)
    {
        var targetDate = (date ?? DateTime.UtcNow).Date;
        var weekStart = targetDate.AddDays(-(int)targetDate.DayOfWeek);

        var weekStartUtc = BusinessDayStart(weekStart);
        var weekEndUtc = BusinessDayStart(weekStart.AddDays(7));
        var classifications = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt < weekEndUtc
                     && c.EndedAt > weekStartUtc)
            .ToListAsync(ct);

        var todayStartUtc = BusinessDayStart(targetDate);
        var todayEndUtc = BusinessDayStart(targetDate.AddDays(1));

        double OverlapMin(ActivityClassificationEntity c, DateTimeOffset s, DateTimeOffset e)
            => OverlapSeconds(c, s, e) / 60.0;

        var todayProductive = classifications.Where(c => GetProductivity(c.CategoryName) == "productive").Sum(c => OverlapMin(c, todayStartUtc, todayEndUtc));
        var todayDistracting = classifications.Where(c => GetProductivity(c.CategoryName) == "distracting").Sum(c => OverlapMin(c, todayStartUtc, todayEndUtc));
        var todayNeutral = classifications.Where(c => GetProductivity(c.CategoryName) == "neutral").Sum(c => OverlapMin(c, todayStartUtc, todayEndUtc));
        var todayTotal = todayProductive + todayDistracting + todayNeutral;

        var weeklyTrend = new List<DailyProductivityDto>();
        for (int i = 0; i < 7; i++)
        {
            var day = weekStart.AddDays(i);
            var ds = BusinessDayStart(day);
            var de = BusinessDayStart(day.AddDays(1));
            var p = classifications.Where(c => GetProductivity(c.CategoryName) == "productive").Sum(c => OverlapMin(c, ds, de));
            var d = classifications.Where(c => GetProductivity(c.CategoryName) == "distracting").Sum(c => OverlapMin(c, ds, de));
            var n = classifications.Where(c => GetProductivity(c.CategoryName) == "neutral").Sum(c => OverlapMin(c, ds, de));
            var t = p + d + n;
            weeklyTrend.Add(new DailyProductivityDto
            {
                Date = day.ToString("yyyy-MM-dd"),
                ProductiveMinutes = Math.Round(p, 1),
                DistractingMinutes = Math.Round(d, 1),
                NeutralMinutes = Math.Round(n, 1),
                TotalMinutes = Math.Round(t, 1),
                ProductiveRatio = t > 0 ? Math.Round(p / t, 4) : 0
            });
        }

        var goal = await GetGoalsAsync(ct);
        var targetHours = goal.DailyProductiveHours;
        return new ProductivityDashboardDto
        {
            TodayScore = todayTotal > 0 ? Math.Round(todayProductive / todayTotal * 100, 1) : 0,
            ProductiveHours = Math.Round(todayProductive / 60.0, 1),
            DistractingHours = Math.Round(todayDistracting / 60.0, 1),
            NeutralHours = Math.Round(todayNeutral / 60.0, 1),
            TargetHours = targetHours,
            GoalMet = todayProductive / 60.0 >= targetHours,
            WeeklyTrend = weeklyTrend
        };
    }


    public async Task<ProductivityGoalDto> GetGoalsAsync(CancellationToken ct)
    {
        var settings = await _db.Set<ActivityClassificationSettingsEntity>()
            .FirstOrDefaultAsync(s => s.SettingsKey == "default", ct);
        return new ProductivityGoalDto
        {
            DailyProductiveHours = settings?.DailyProductiveHoursGoal ?? 5.0
        };
    }

    public async Task<ProductivityGoalDto> UpdateGoalsAsync(ProductivityGoalDto req, CancellationToken ct)
    {
        var settings = await _db.Set<ActivityClassificationSettingsEntity>()
            .FirstOrDefaultAsync(s => s.SettingsKey == "default", ct);
        if (settings is null)
        {
            settings = new ActivityClassificationSettingsEntity
            {
                SettingsKey = "default",
                DailyProductiveHoursGoal = Math.Max(0.5, Math.Min(24.0, req.DailyProductiveHours)),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.Set<ActivityClassificationSettingsEntity>().Add(settings);
        }
        else
        {
            settings.DailyProductiveHoursGoal = Math.Max(0.5, Math.Min(24.0, req.DailyProductiveHours));
            settings.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return new ProductivityGoalDto
        {
            DailyProductiveHours = settings.DailyProductiveHoursGoal
        };
    }

    public async Task<List<DailyProductivityDto>> GetRangeAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var rangeStartUtc = BusinessDayStart(start.Date);
        var rangeEndUtc = BusinessDayStart(end.Date.AddDays(1));
        var classifications = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt < rangeEndUtc
                     && c.EndedAt > rangeStartUtc)
            .ToListAsync(ct);

        var startDate = start.Date;
        var endDate = end.Date;
        var dayCount = (endDate - startDate).Days + 1;
        var acc = new Dictionary<DateTime, (double p, double d, double n)>();
        var dayBounds = new Dictionary<DateTime, (DateTimeOffset Start, DateTimeOffset End)>();
        for (int i = 0; i < dayCount; i++)
        {
            var day = startDate.AddDays(i);
            acc[day] = (0, 0, 0);
            dayBounds[day] = (BusinessDayStart(day), BusinessDayStart(day.AddDays(1)));
        }

        // O(N * span) 而非 O(N*D)：仅遍历事件实际跨越的业务日（通常 1-2 天）
        foreach (var c in classifications)
        {
            var prod = GetProductivity(c.CategoryName);
            var startDay = BusinessDayForTimestamp(c.StartedAt);
            var endDay = c.EndedAt > c.StartedAt
                ? BusinessDayForTimestamp(c.EndedAt.AddTicks(-1))
                : startDay;
            if (endDay < startDate || startDay > endDate) continue;
            if (startDay < startDate) startDay = startDate;
            if (endDay > endDate) endDay = endDate;

            for (var day = startDay; day <= endDay; day = day.AddDays(1))
            {
                if (!dayBounds.TryGetValue(day, out var bounds)) continue;
                var sec = OverlapSeconds(c, bounds.Start, bounds.End);
                if (sec <= 0) continue;
                var min = sec / 60.0;
                var cur = acc[day];
                if (prod == "productive") cur.p += min;
                else if (prod == "distracting") cur.d += min;
                else cur.n += min;
                acc[day] = cur;
            }
        }

        // Return only days that have any activity (preserves previous grouping semantics) but with prorated splits
        // If caller expects all days, they can still handle empty; we return sorted with all days that had >0
        var result = new List<DailyProductivityDto>();
        foreach (var kv in acc.OrderBy(k => k.Key))
        {
            var p = kv.Value.p;
            var d = kv.Value.d;
            var n = kv.Value.n;
            var t = p + d + n;
            if (t <= 0) continue;
            result.Add(new DailyProductivityDto
            {
                Date = kv.Key.ToString("yyyy-MM-dd"),
                ProductiveMinutes = Math.Round(p, 1),
                DistractingMinutes = Math.Round(d, 1),
                NeutralMinutes = Math.Round(n, 1),
                TotalMinutes = Math.Round(t, 1),
                ProductiveRatio = t > 0 ? Math.Round(p / t, 4) : 0
            });
        }
        return result;
    }

    public async Task<List<TimelineV2Item>> GetTimelineV2Async(DateTime date, CancellationToken ct)
    {
        var dayStart = BusinessDayStart(date.Date);
        var dayEnd = BusinessDayStart(date.Date.AddDays(1));

        // 显式二级排序（Id）：相同 StartedAt 的行在 SQL 中的返回顺序未定义，
        // 固定下来可让消解输入顺序、进而让整份时间线结果完全可复现。
        var items = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt < dayEnd
                     && c.EndedAt > dayStart)
            .OrderBy(c => c.StartedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        // 裁剪到业务日窗口，作为重叠消解的输入候选
        var clipped = new List<ClippedSnapshot>(items.Count);
        foreach (var entity in items)
        {
            var start = entity.StartedAt > dayStart ? entity.StartedAt : dayStart;
            var end = entity.EndedAt < dayEnd ? entity.EndedAt : dayEnd;
            if (end <= start)
                continue;
            clipped.Add(new ClippedSnapshot(entity, start, end));
        }
        if (clipped.Count == 0)
            return [];

        // #237：扫描线消解重叠，保证结果按 start 升序且两两不重叠
        var segments = PcTimelineOverlapResolver.Resolve(
            clipped
                .Select(x => new PcTimelineOverlapResolver.Candidate(x.Start, x.End, x.Entity.Confidence, x.Entity.RecordKey))
                .ToList(),
            MinTimelineSegment);

        // #235：应用身份（应用名 / 显示名 / 窗口标题）
        var identities = await ResolveAppIdentitiesAsync(
            segments.Select(segment => clipped[segment.WinnerIndex].Entity).DistinctBy(entity => entity.Id).ToList(),
            dayStart,
            dayEnd,
            ct);

        return segments.Select(segment =>
        {
            var entity = clipped[segment.WinnerIndex].Entity;
            var identity = identities[entity.Id];
            return new TimelineV2Item
            {
                Start = ToBusinessTime(segment.Start),
                End = ToBusinessTime(segment.End),
                AppName = identity.AppName,
                AppDisplayName = identity.AppDisplayName,
                WindowTitle = identity.WindowTitle,
                CategoryName = entity.CategoryName ?? "其他",
                CategoryColor = entity.CategoryColor ?? "#64748b",
                Productivity = GetProductivity(entity.CategoryName),
                Confidence = entity.Confidence,
                DurationMinutes = Math.Round((segment.End - segment.Start).TotalSeconds / 60.0, 1)
            };
        }).ToList();
    }

    /// <summary>把 UTC 绝对时刻转为业务日时区（Asia/Shanghai）的带偏移表示，
    /// 使接口输出带上 +08:00，消费方无需猜测时区（#236）。</summary>
    private static DateTimeOffset ToBusinessTime(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, ResolveBusinessDayTimeZone());

    /// <summary>
    /// 解析时间线块的应用身份（#235）。优先级：
    /// <list type="number">
    /// <item><c>app_name</c>（进程名）：取快照落库值，历史行缺失时按时间重叠从 <c>pc_tracker_events</c> 的原生 window 事件兜底</item>
    /// <item><c>app_display_name</c>（显示名）：先查 <c>pc_app_signatures</c> 签名表（与应用时长 Top 面板同口径），
    /// 再退到快照/事件记录的显示名，最后退到进程名</item>
    /// <item><c>window_title</c>：取快照落库值，缺失时同样按重叠事件兜底</item>
    /// </list>
    /// 进程名完全无法解析时才退回 <c>record_key</c>（明确表示「无应用信息」，不伪装成真实应用名）。
    /// </summary>
    private async Task<Dictionary<Guid, AppIdentity>> ResolveAppIdentitiesAsync(
        IReadOnlyList<ActivityClassificationEntity> entities,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        CancellationToken ct)
    {
        var needsEventFallback = entities
            .Where(entity => string.IsNullOrWhiteSpace(entity.AppName) || string.IsNullOrWhiteSpace(entity.WindowTitle))
            .ToList();
        var eventIndex = needsEventFallback.Count == 0
            ? null
            : await LoadDayWindowEventIndexAsync(dayStart, dayEnd, ct);

        // 进程名 / 显示名线索 / 窗口标题：快照优先，缺失时由重叠事件兜底
        var processNames = new Dictionary<Guid, string?>(entities.Count);
        var displayNameHints = new Dictionary<Guid, string?>(entities.Count);
        var windowTitles = new Dictionary<Guid, string?>(entities.Count);

        foreach (var entity in entities)
        {
            var processName = entity.AppName;
            var displayNameHint = entity.AppDisplayName;
            var windowTitle = entity.WindowTitle;

            if (eventIndex is not null
                && (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(windowTitle)))
            {
                var match = eventIndex.FindBestOverlap(entity.StartedAt, entity.EndedAt);
                if (match is not null)
                {
                    if (string.IsNullOrWhiteSpace(processName))
                        processName = string.IsNullOrWhiteSpace(match.AppName) ? match.DisplayName : match.AppName;
                    if (string.IsNullOrWhiteSpace(displayNameHint))
                        displayNameHint = match.DisplayName;
                    if (string.IsNullOrWhiteSpace(windowTitle))
                        windowTitle = match.WindowTitle;
                }
            }

            processNames[entity.Id] = processName;
            displayNameHints[entity.Id] = displayNameHint;
            windowTitles[entity.Id] = windowTitle;
        }

        // 已知进程名批量查签名表（一次查询，避免逐行往返）
        var knownProcessNames = processNames.Values
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var signatureNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (knownProcessNames.Count > 0)
        {
            var signatures = await _db.Set<AppSignatureEntity>()
                .Select(s => new { s.ProcessName, s.DisplayName })
                .ToListAsync(ct);
            signatureNames = AppSignatureMatcher.ResolveDisplayNames(
                knownProcessNames,
                signatures.Select(s => (s.ProcessName ?? string.Empty, s.DisplayName ?? string.Empty)));
        }

        var result = new Dictionary<Guid, AppIdentity>(entities.Count);
        foreach (var entity in entities)
        {
            var processName = processNames[entity.Id];
            var resolvedAppName = string.IsNullOrWhiteSpace(processName)
                ? entity.RecordKey
                : processName.Trim();

            var displayName = signatureNames.TryGetValue(resolvedAppName, out var curated)
                ? curated
                : FirstNonEmpty(displayNameHints[entity.Id], resolvedAppName)!;

            result[entity.Id] = new AppIdentity(
                resolvedAppName,
                displayName,
                FirstNonEmpty(windowTitles[entity.Id]));
        }

        return result;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    /// <summary>加载业务日窗口内的原生 window 事件，按 <c>timestamp</c> 升序，供重叠兜底查询。
    /// 事件时长按 <see cref="MaxTrackerEventSeconds"/> 截断，与聚合口径一致。</summary>
    private async Task<DayWindowEventIndex> LoadDayWindowEventIndexAsync(
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        CancellationToken ct)
    {
        var events = await _db.Set<TrackerEventEntity>()
            .Where(e => e.EventType == "window"
                && e.Timestamp < dayEnd
                && e.Timestamp >= dayStart.AddSeconds(-MaxTrackerEventSeconds))
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return new DayWindowEventIndex(events);
    }

    /// <summary>业务日内的 window 事件索引：对给定区间找出重叠时长最长的原生事件。</summary>
    private sealed class DayWindowEventIndex
    {
        private readonly List<TrackerEventEntity> _events;

        public DayWindowEventIndex(List<TrackerEventEntity> events)
        {
            _events = events;
        }

        public TrackerEventEntity? FindBestOverlap(DateTimeOffset start, DateTimeOffset end)
        {
            if (_events.Count == 0 || end <= start)
                return null;

            // 事件起点最早的兜底搜索下界：起点不早于 start 减去事件时长上限
            var lowerBound = LowerBound(start.AddSeconds(-MaxTrackerEventSeconds));

            TrackerEventEntity? best = null;
            var bestOverlap = 0d;
            for (var i = lowerBound; i < _events.Count; i++)
            {
                var candidate = _events[i];
                if (candidate.Timestamp >= end)
                    break;

                var candidateEnd = candidate.Timestamp.AddSeconds(
                    Math.Min(Math.Max(candidate.Duration, 0), MaxTrackerEventSeconds));
                var overlapStart = candidate.Timestamp > start ? candidate.Timestamp : start;
                var overlapEnd = candidateEnd < end ? candidateEnd : end;
                var overlap = (overlapEnd - overlapStart).TotalSeconds;
                if (overlap <= 0)
                    continue;

                // 严格更大才替换：并列时保留更早的事件（确定性）
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>第一个 timestamp &gt;= target 的事件下标。</summary>
        private int LowerBound(DateTimeOffset target)
        {
            var low = 0;
            var high = _events.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (_events[mid].Timestamp < target)
                    low = mid + 1;
                else
                    high = mid;
            }
            return low;
        }
    }

    private sealed record ClippedSnapshot(
        ActivityClassificationEntity Entity,
        DateTimeOffset Start,
        DateTimeOffset End);

    private sealed record AppIdentity(string AppName, string AppDisplayName, string? WindowTitle);

    private static double OverlapSeconds(ActivityClassificationEntity c, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var overlapStart = c.StartedAt > windowStart ? c.StartedAt : windowStart;
        var overlapEnd = c.EndedAt < windowEnd ? c.EndedAt : windowEnd;
        var seconds = (overlapEnd - overlapStart).TotalSeconds;
        return Math.Max(0, seconds);
    }

    private string GetProductivity(string? categoryName)
    {
        if (string.IsNullOrEmpty(categoryName) || categoryName == "其他")
            return "neutral";

        var productiveKeywords = new[] { "工作", "编程", "文档", "会议", "设计", "运维", "学习", "技术", "外语", "邮件" };
        var distractingKeywords = new[] { "游戏", "视频", "娱乐", "社交" };

        if (productiveKeywords.Any(k => categoryName.Contains(k)))
            return "productive";
        if (distractingKeywords.Any(k => categoryName.Contains(k)))
            return "distracting";
        return "neutral";
    }

    private static TimeZoneInfo ResolveBusinessDayTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimezoneName); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(ChinaFallbackTimezone); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById(ChinaFallbackTimezone); }
    }

    private static DateTimeOffset BusinessDayStart(DateTime date)
    {
        var tz = ResolveBusinessDayTimeZone();
        var local = DateTime.SpecifyKind(date.Date.AddHours(BusinessDayStartHour), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static DateTime BusinessDayForTimestamp(DateTimeOffset ts) => PcTrackerService.GetBusinessDayForTimestamp(ts);
}
