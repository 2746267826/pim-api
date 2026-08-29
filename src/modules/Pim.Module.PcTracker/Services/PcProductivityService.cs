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
            .Where(c => c.StartedAt >= weekStartUtc
                     && c.StartedAt < weekEndUtc)
            .ToListAsync(ct);

        var todayStartUtc = BusinessDayStart(targetDate);
        var todayEndUtc = BusinessDayStart(targetDate.AddDays(1));
        var todayItems = classifications
            .Where(c => c.StartedAt >= todayStartUtc && c.StartedAt < todayEndUtc)
            .ToList();

        var todayProductive = todayItems.Where(c => GetProductivity(c.CategoryName) == "productive").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
        var todayDistracting = todayItems.Where(c => GetProductivity(c.CategoryName) == "distracting").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
        var todayNeutral = todayItems.Where(c => GetProductivity(c.CategoryName) == "neutral").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
        var todayTotal = todayProductive + todayDistracting + todayNeutral;

        var weeklyTrend = new List<DailyProductivityDto>();
        for (int i = 0; i < 7; i++)
        {
            var day = weekStart.AddDays(i);
            var ds = BusinessDayStart(day);
            var de = BusinessDayStart(day.AddDays(1));
            var dayItems = classifications.Where(c => c.StartedAt >= ds && c.StartedAt < de).ToList();
            var p = dayItems.Where(c => GetProductivity(c.CategoryName) == "productive").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
            var d = dayItems.Where(c => GetProductivity(c.CategoryName) == "distracting").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
            var n = dayItems.Where(c => GetProductivity(c.CategoryName) == "neutral").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
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
            .Where(c => c.StartedAt >= rangeStartUtc
                     && c.StartedAt < rangeEndUtc)
            .ToListAsync(ct);

        return classifications
            .GroupBy(c => BusinessDayForTimestamp(c.StartedAt))
            .Select(g =>
            {
                var p = g.Where(c => GetProductivity(c.CategoryName) == "productive").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
                var d = g.Where(c => GetProductivity(c.CategoryName) == "distracting").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
                var n = g.Where(c => GetProductivity(c.CategoryName) == "neutral").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
                var t = p + d + n;
                return new DailyProductivityDto
                {
                    Date = g.Key.ToString("yyyy-MM-dd"),
                    ProductiveMinutes = Math.Round(p, 1),
                    DistractingMinutes = Math.Round(d, 1),
                    NeutralMinutes = Math.Round(n, 1),
                    TotalMinutes = Math.Round(t, 1),
                    ProductiveRatio = t > 0 ? Math.Round(p / t, 4) : 0
                };
            })
            .OrderBy(x => x.Date)
            .ToList();
    }

    public async Task<List<TimelineV2Item>> GetTimelineV2Async(DateTime date, CancellationToken ct)
    {
        var dayStart = BusinessDayStart(date.Date);
        var dayEnd = BusinessDayStart(date.Date.AddDays(1));

        var items = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt >= dayStart
                     && c.StartedAt < dayEnd)
            .OrderBy(c => c.StartedAt)
            .ToListAsync(ct);

        return items.Select(c =>
        {
            var dur = (c.EndedAt - c.StartedAt).TotalMinutes;
            return new TimelineV2Item
            {
                Start = c.StartedAt.DateTime,
                End = c.EndedAt.DateTime,
                AppName = c.RecordKey,
                WindowTitle = null,
                CategoryName = c.CategoryName ?? "其他",
                CategoryColor = c.CategoryColor ?? "#64748b",
                Productivity = GetProductivity(c.CategoryName),
                Confidence = c.Confidence,
                DurationMinutes = Math.Round(dur, 1)
            };
        }).ToList();
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
