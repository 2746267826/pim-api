using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

public class ActivityTimelineSmoothingService
{
    private static readonly TimeSpan ContiguousTolerance = TimeSpan.FromSeconds(1);

    public IReadOnlyList<TimelineItem> Smooth(IReadOnlyList<TimelineItem> items, int recommendedMinimumMinutes)
    {
        if (items.Count < 3 || recommendedMinimumMinutes <= 1)
            return items.ToList();

        var ordered = items
            .OrderBy(item => ParseTime(item.Start))
            .ThenBy(item => ParseTime(item.End))
            .ToList();
        var smoothed = new List<TimelineItem>();

        foreach (var item in ordered)
        {
            smoothed.Add(item);
            while (smoothed.Count >= 3)
            {
                var previous = smoothed[^3];
                var current = smoothed[^2];
                var next = smoothed[^1];
                if (!CanMerge(previous, current, next, recommendedMinimumMinutes))
                    break;

                smoothed.RemoveRange(smoothed.Count - 3, 3);
                smoothed.Add(Merge(previous, current, next));
            }
        }

        return smoothed;
    }

    private static bool CanMerge(
        TimelineItem previous,
        TimelineItem current,
        TimelineItem next,
        int recommendedMinimumMinutes)
    {
        return current.DurationMinutes < recommendedMinimumMinutes
            && current.ClassificationConfidence < 0.5
            && string.Equals(current.ClassificationSource, "fallback", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(current.ProjectTag)
            && AreContiguous(previous, current)
            && AreContiguous(current, next)
            && string.Equals(previous.CategoryName, next.CategoryName, StringComparison.Ordinal)
            && string.Equals(previous.ProjectTag, next.ProjectTag, StringComparison.OrdinalIgnoreCase)
            && previous.ClassificationConfidence >= 0.7
            && next.ClassificationConfidence >= 0.7;
    }

    private static bool AreContiguous(TimelineItem left, TimelineItem right)
    {
        return (ParseTime(right.Start) - ParseTime(left.End)).Duration() <= ContiguousTolerance;
    }

    private static TimelineItem Merge(TimelineItem previous, TimelineItem current, TimelineItem next)
    {
        var start = ParseTime(previous.Start);
        var end = ParseTime(next.End);
        return previous with
        {
            Start = FormatUtc(start),
            End = FormatUtc(end),
            DurationMinutes = Math.Max(0, (end - start).TotalMinutes),
            AppName = previous.AppName,
            WindowTitle = previous.WindowTitle,
            ClassificationConfidence = Math.Min(previous.ClassificationConfidence, next.ClassificationConfidence),
            ClassificationSource = previous.ClassificationSource,
            ClassificationExplanation = $"{previous.ClassificationExplanation} short low-confidence activity was smoothed into the surrounding matching project context."
        };
    }

    private static DateTimeOffset ParseTime(string value)
    {
        return DateTimeOffset.Parse(value).ToUniversalTime();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O");
    }
}
