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

    private readonly PimDbContext _db;

    public PcProductivityService(PimDbContext db)
    {
        _db = db;
    }

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

        var targetHours = 5.0;
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

        var items = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt < dayEnd
                     && c.EndedAt > dayStart)
            .OrderBy(c => c.StartedAt)
            .ToListAsync(ct);

        // 裁剪起止到业务日内并过滤 0 时长，保证 Start/End 与 Duration 一致
        return items.Select(c =>
            {
                var overlapStart = c.StartedAt > dayStart ? c.StartedAt : dayStart;
                var overlapEnd = c.EndedAt < dayEnd ? c.EndedAt : dayEnd;
                var sec = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
                return (c, overlapStart, overlapEnd, sec);
            })
            .Where(x => x.sec > 0)
            .Select(x => new TimelineV2Item
            {
                Start = x.overlapStart.DateTime,
                End = x.overlapEnd.DateTime,
                AppName = x.c.RecordKey,
                WindowTitle = null,
                CategoryName = x.c.CategoryName ?? "其他",
                CategoryColor = x.c.CategoryColor ?? "#64748b",
                Productivity = GetProductivity(x.c.CategoryName),
                Confidence = x.c.Confidence,
                DurationMinutes = Math.Round(x.sec / 60.0, 1)
            }).ToList();
    }

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

    private static DateTime BusinessDayForTimestamp(DateTimeOffset ts)
    {
        var tz = ResolveBusinessDayTimeZone();
        var local = TimeZoneInfo.ConvertTime(ts, tz).Date;
        // 04:00 边界：local 04:00 前归前一日
        var localDt = TimeZoneInfo.ConvertTime(ts, tz);
        if (localDt.Hour < BusinessDayStartHour)
            local = local.AddDays(-1);
        return local;
    }
}
