using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityTimelineSmoothingServiceTests
{
    [Fact]
    public void Smooth_MergesLowConfidenceShortBlockBetweenSameProjectBlocks()
    {
        var service = new ActivityTimelineSmoothingService();
        var items = new[]
        {
            Item("2026-05-25T08:00:00Z", 10, "\u7f16\u7a0b", "PIM", 0.95, "rule"),
            Item("2026-05-25T08:10:00Z", 2, "\u5176\u4ed6", null, 0.2, "fallback"),
            Item("2026-05-25T08:12:00Z", 18, "\u7f16\u7a0b", "PIM", 0.95, "rule")
        };

        var smoothed = service.Smooth(items, 5);

        var item = Assert.Single(smoothed);
        Assert.Equal("2026-05-25T08:00:00.0000000Z", item.Start);
        Assert.Equal("2026-05-25T08:30:00.0000000Z", item.End);
        Assert.Equal(30, item.DurationMinutes);
        Assert.Equal("\u7f16\u7a0b", item.CategoryName);
        Assert.Equal("PIM", item.ProjectTag);
        Assert.Contains("short low-confidence activity was smoothed", item.ClassificationExplanation);
    }

    [Fact]
    public void Smooth_KeepsStrongShortCommunicationBlock()
    {
        var service = new ActivityTimelineSmoothingService();
        var items = new[]
        {
            Item("2026-05-25T08:00:00Z", 10, "\u7f16\u7a0b", "PIM", 0.95, "rule"),
            Item("2026-05-25T08:10:00Z", 1, "\u6c9f\u901a", "PIM", 0.95, "rule"),
            Item("2026-05-25T08:11:00Z", 19, "\u7f16\u7a0b", "PIM", 0.95, "rule")
        };

        var smoothed = service.Smooth(items, 5);

        Assert.Equal(3, smoothed.Count);
        Assert.Equal("\u6c9f\u901a", smoothed[1].CategoryName);
        Assert.Equal(1, smoothed[1].DurationMinutes);
        Assert.Equal("rule", smoothed[1].ClassificationSource);
    }

    [Fact]
    public void Smooth_DoesNotMergeAcrossGaps()
    {
        var service = new ActivityTimelineSmoothingService();
        var items = new[]
        {
            Item("2026-05-25T08:00:00Z", 10, "\u7f16\u7a0b", "PIM", 0.95, "rule"),
            Item("2026-05-25T08:15:00Z", 2, "\u5176\u4ed6", null, 0.2, "fallback"),
            Item("2026-05-25T08:20:00Z", 10, "\u7f16\u7a0b", "PIM", 0.95, "rule")
        };

        var smoothed = service.Smooth(items, 5);

        Assert.Equal(3, smoothed.Count);
        Assert.Equal("\u5176\u4ed6", smoothed[1].CategoryName);
        Assert.Equal("fallback", smoothed[1].ClassificationSource);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("heuristic")]
    public void Smooth_KeepsLowConfidenceShortNonFallbackBlocks(string source)
    {
        var service = new ActivityTimelineSmoothingService();
        var items = new[]
        {
            Item("2026-05-25T08:00:00Z", 10, "\u7f16\u7a0b", "PIM", 0.95, "rule"),
            Item("2026-05-25T08:10:00Z", 2, "\u5176\u4ed6", null, 0.2, source),
            Item("2026-05-25T08:12:00Z", 18, "\u7f16\u7a0b", "PIM", 0.95, "rule")
        };

        var smoothed = service.Smooth(items, 5);

        Assert.Equal(3, smoothed.Count);
        Assert.Equal(source, smoothed[1].ClassificationSource);
        Assert.Equal("\u5176\u4ed6", smoothed[1].CategoryName);
    }

    private static TimelineItem Item(
        string start,
        double minutes,
        string categoryName,
        string? projectTag,
        double confidence,
        string source)
    {
        var startTime = DateTimeOffset.Parse(start);
        var endTime = startTime.AddMinutes(minutes);

        return new TimelineItem(
            startTime.ToString("O"),
            endTime.ToString("O"),
            minutes,
            "Code.exe",
            "PIM",
            categoryName,
            categoryName == "\u7f16\u7a0b" ? "#6B5EE4" : "#64748b",
            projectTag,
            confidence,
            source,
            source == "rule" ? "Matched test rule." : "No rule or heuristic matched.");
    }
}
