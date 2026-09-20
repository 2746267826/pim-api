using System.Globalization;
using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

public sealed class PcActivityAnalysisService
{
    private readonly PcTrackerService _tracker;

    public PcActivityAnalysisService(PcTrackerService tracker)
    {
        _tracker = tracker;
    }

    public async Task<PcActivityAnalysisResponse> GetDailyAnalysisAsync(
        DateTime date,
        int blockMinutes,
        CancellationToken ct)
    {
        if (blockMinutes is < 15 or > 240)
            throw new ArgumentException("时间块分钟数必须在 15 到 240 之间。");

        var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var detail = await _tracker.QueryCompleteDetailAsync(
            new DetailQueryParams(
                dateText,
                dateText,
                null,
                null,
                null,
                null,
                null,
                null,
                "date",
                "asc",
                1,
                2000,
                View: "interpreted"),
            ct);

        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(date);
        var blockCount = (int)Math.Ceiling(TimeSpan.FromDays(1).TotalMinutes / blockMinutes);
        var blocks = new List<PcActivityAnalysisBlockDto>();

        for (var i = 0; i < blockCount; i++)
        {
            var start = dayStart.AddMinutes(i * blockMinutes);
            var end = start.AddMinutes(blockMinutes);
            var records = detail.Items
                .Where(record => record.DurationSeconds is > 0)
                // #331：gap / idle / afk 表示「这里没有人」，不是活动。
                // 不排除的话，空档会被算进 activeSeconds / 强度 / 类别分布，
                // 把一天里没人的时段显示成「有活动」（与分类分布、生产力统计的口径保持一致）。
                .Where(record => !PcActivityOverlapResolver.IsInactive(record.RecordType))
                .Where(record => DateTimeOffset.TryParse(record.Start, out var recordStart)
                    && recordStart >= start
                    && recordStart < end)
                .OrderBy(record => record.Start, StringComparer.Ordinal)
                .ToList();
            var activeSeconds = records.Sum(record => record.DurationSeconds ?? 0);
            var categories = records
                .GroupBy(record => record.CategoryName ?? "Other", StringComparer.OrdinalIgnoreCase)
                .Select(group => new PcActivityAnalysisCategoryDto(
                    group.Key,
                    group.Select(record => record.CategoryColor).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "#64748b",
                    group.Sum(record => record.DurationSeconds ?? 0)))
                .OrderByDescending(item => item.DurationSeconds)
                .ToList();
            var apps = records
                .GroupBy(record => record.RecordType == "web-page"
                    ? record.Domain ?? record.BrowserAppName ?? "web"
                    : record.AppName ?? record.DisplayName ?? "unknown",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new PcActivityAnalysisAppDto(
                    group.Key,
                    group.Sum(record => record.DurationSeconds ?? 0)))
                .OrderByDescending(item => item.DurationSeconds)
                .Take(5)
                .ToList();

            blocks.Add(new PcActivityAnalysisBlockDto(
                start.ToString("O"),
                end.ToString("O"),
                ToIntensity(activeSeconds, blockMinutes),
                activeSeconds,
                records.Count(IsPendingClassification),
                CountSwitches(records.Select(record => record.AppName ?? record.Domain ?? record.DisplayName ?? string.Empty)),
                CountSwitches(records.Select(record => record.CategoryName ?? string.Empty)),
                categories,
                apps));
        }

        return new PcActivityAnalysisResponse(dateText, blockMinutes, blocks);
    }

    private static bool IsPendingClassification(PcDetailRecord record) =>
        string.Equals(record.ClassificationSource, "fallback", StringComparison.OrdinalIgnoreCase)
        || record.ClassificationConfidence is < 0.5;

    private static int ToIntensity(double activeSeconds, int blockMinutes)
    {
        var ratio = activeSeconds / (blockMinutes * 60.0);
        if (ratio <= 0) return 0;
        if (ratio <= 0.2) return 1;
        if (ratio <= 0.45) return 2;
        if (ratio <= 0.7) return 3;
        return 4;
    }

    private static int CountSwitches(IEnumerable<string> values)
    {
        string? previous = null;
        var count = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (previous is not null && !string.Equals(previous, value, StringComparison.OrdinalIgnoreCase))
                count++;

            previous = value;
        }

        return count;
    }
}
