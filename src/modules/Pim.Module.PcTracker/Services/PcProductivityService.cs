using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class PcProductivityService
{
    private readonly PimDbContext _db;

    public PcProductivityService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<ProductivityDashboardDto> GetDashboardAsync(DateTime? date, CancellationToken ct)
    {
        var targetDate = (date ?? DateTime.UtcNow).Date;
        if (targetDate.Kind != DateTimeKind.Utc)
            targetDate = DateTime.SpecifyKind(targetDate, DateTimeKind.Utc);
        var weekStart = targetDate.AddDays(-(int)targetDate.DayOfWeek);

        var classifications = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt >= new DateTimeOffset(weekStart, TimeSpan.Zero)
                     && c.StartedAt < new DateTimeOffset(weekStart.AddDays(7), TimeSpan.Zero))
            .ToListAsync(ct);

        var todayItems = classifications
            .Where(c => c.StartedAt.Date == targetDate)
            .ToList();

        var todayProductive = todayItems.Where(c => GetProductivity(c.CategoryName) == "productive").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
        var todayDistracting = todayItems.Where(c => GetProductivity(c.CategoryName) == "distracting").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
        var todayNeutral = todayItems.Where(c => GetProductivity(c.CategoryName) == "neutral").Sum(c => (c.EndedAt - c.StartedAt).TotalMinutes);
        var todayTotal = todayProductive + todayDistracting + todayNeutral;

        var weeklyTrend = new List<DailyProductivityDto>();
        for (int i = 0; i < 7; i++)
        {
            var day = weekStart.AddDays(i);
            var dayItems = classifications.Where(c => c.StartedAt.Date == day).ToList();
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
        var utcStart = start.Kind != DateTimeKind.Utc ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : start;
        var utcEnd = end.Kind != DateTimeKind.Utc ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : end;
        var classifications = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt >= new DateTimeOffset(utcStart, TimeSpan.Zero)
                     && c.StartedAt < new DateTimeOffset(utcEnd.AddDays(1), TimeSpan.Zero))
            .ToListAsync(ct);

        return classifications
            .GroupBy(c => c.StartedAt.Date)
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
        var dayStart = date.Kind != DateTimeKind.Utc ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc) : date.Date;
        var dayEnd = dayStart.AddDays(1);

        var items = await _db.Set<ActivityClassificationEntity>()
            .Where(c => c.StartedAt >= new DateTimeOffset(dayStart, TimeSpan.Zero)
                     && c.StartedAt < new DateTimeOffset(dayEnd, TimeSpan.Zero))
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
}
