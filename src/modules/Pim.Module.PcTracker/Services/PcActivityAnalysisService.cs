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
        // REQ-1（#362）：聚合输入必须覆盖整个业务日。这里走**内部分析路径**，
        // 不吃 /pc/detail 的对外分页契约（PageSize 被夹到 1–200 且只取第 1 页）——
        // 之前传 pageSize:2000 实际只拿到 200 条，24 个块里只有 2 块有数据。
        var records = await _tracker.QueryAllDetailRecordsAsync(
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

            // REQ-2（#363）：块内候选先过 PcActivityOverlapResolver 的统一口径再汇总。
            // 直接 Sum(DurationSeconds) 会把同一时刻的多路记录（window / web-page / input-minute）
            // 重复计费 —— 实测 1 小时块报 5960 秒（99.3 分钟）。
            // 段已裁剪到块窗口，因此合计恒 ≤ 块时长。
            var segments = PcActivityActiveSegments.Resolve(records, start, end);
            var activeSeconds = PcActivityActiveSegments.SumSeconds(segments);

            var blockRecords = records
                .Where(record => (record.DurationSeconds ?? 0) > 0)
                // #331：gap / idle / afk 表示「这里没有人」，不是活动。
                // 不排除的话，空档会被算进 activeSeconds / 强度 / 类别分布，
                // 把一天里没人的时段显示成「有活动」（与分类分布、生产力统计的口径保持一致）。
                .Where(record => !PcActivityOverlapResolver.IsInactive(record.RecordType))
                .Where(record => PcActivityActiveSegments.TryGetInterval(record, out var recordStart, out var recordEnd)
                    && recordStart < end
                    && recordEnd > start)
                .OrderBy(record => record.Start, StringComparer.Ordinal)
                .ToList();

            var categories = segments
                .GroupBy(segment => segment.Record.CategoryName ?? "Other", StringComparer.OrdinalIgnoreCase)
                .Select(group => new PcActivityAnalysisCategoryDto(
                    group.Key,
                    group.Select(segment => segment.Record.CategoryColor).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "#64748b",
                    PcActivityActiveSegments.SumSeconds(group)))
                .OrderByDescending(item => item.DurationSeconds)
                .ToList();
            var apps = segments
                .GroupBy(segment => ResolveAppName(segment.Record), StringComparer.OrdinalIgnoreCase)
                .Select(group => new PcActivityAnalysisAppDto(
                    group.Key,
                    PcActivityActiveSegments.SumSeconds(group)))
                .OrderByDescending(item => item.DurationSeconds)
                .Take(5)
                .ToList();

            blocks.Add(new PcActivityAnalysisBlockDto(
                start.ToString("O"),
                end.ToString("O"),
                // REQ-3（#364, P-2 方案 a）：统一为「活跃时长 / 块时长」的 0–5 档。
                // 60 分钟块的边界正好是 5/15/30/45 活跃分钟，与 summary.heatmap 同值（AC-3.2）。
                PcActivityIntensity.ForSeconds(activeSeconds, blockMinutes * 60.0),
                PcActivityIntensity.MaxLevel,
                activeSeconds,
                blockRecords.Count(IsPendingClassification),
                CountSwitches(blockRecords.Select(record => record.AppName ?? record.Domain ?? record.DisplayName ?? string.Empty)),
                CountSwitches(blockRecords.Select(record => record.CategoryName ?? string.Empty)),
                categories,
                apps));
        }

        return new PcActivityAnalysisResponse(dateText, blockMinutes, blocks);
    }

    private static string ResolveAppName(PcDetailRecord record)
        => record.RecordType == "web-page"
            ? record.Domain ?? record.BrowserAppName ?? "web"
            : record.AppName ?? record.DisplayName ?? "unknown";

    private static bool IsPendingClassification(PcDetailRecord record) =>
        string.Equals(record.ClassificationSource, "fallback", StringComparison.OrdinalIgnoreCase)
        || record.ClassificationConfidence is < 0.5;

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
