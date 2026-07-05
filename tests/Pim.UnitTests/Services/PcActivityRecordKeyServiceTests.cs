using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcActivityRecordKeyServiceTests
{
    [Fact]
    public void Build_PrefersAwBucketAndSourceEventId()
    {
        var record = NewRecord() with
        {
            SourceBucketIds = ["aw-watcher-window_device-1"],
            SourceWindowEventIds = [42]
        };

        var result = PcActivityRecordKeyService.Build(record);

        Assert.Equal("pc-aw-v1:aw-watcher-window_device-1:42", result.RecordKey);
        Assert.Equal("pc-aw-v1", result.KeyVersion);
        Assert.Equal("aw", result.SourceType);
        Assert.Equal("stable", result.Stability);
        Assert.Equal("[42]", result.SourceEventIdsJson);
        Assert.Equal("[\"aw-watcher-window_device-1\"]", result.SourceBucketIdsJson);
    }

    [Fact]
    public void Build_UsesSortedSourceIdsForMergedWebPage()
    {
        var record = NewRecord() with
        {
            RecordType = "web-page",
            SourceBucketIds = ["aw-watcher-web-edge_device-1"],
            SourceWebEventIds = [9, 7, 8]
        };

        var result = PcActivityRecordKeyService.Build(record);

        Assert.Equal("pc-aw-v1:aw-watcher-web-edge_device-1:7-8-9", result.RecordKey);
        Assert.Equal("stable", result.Stability);
    }

    [Fact]
    public void Build_FallsBackWithExplicitLowerStability()
    {
        var record = NewRecord() with
        {
            SourceBucketIds = null,
            SourceWebEventIds = null,
            SourceWindowEventIds = null
        };

        var result = PcActivityRecordKeyService.Build(record);

        Assert.StartsWith("pc-fallback-v1:", result.RecordKey);
        Assert.Equal("pc-fallback-v1", result.KeyVersion);
        Assert.Equal("fallback", result.SourceType);
        Assert.Equal("low", result.Stability);
    }

    [Fact]
    public void Build_FallbackIgnoresClassificationFields()
    {
        var first = NewRecord() with { CategoryName = "Other", ClassificationExplanation = "first" };
        var second = first with { CategoryName = "Learning", ClassificationExplanation = "second" };

        Assert.Equal(
            PcActivityRecordKeyService.Build(first).RecordKey,
            PcActivityRecordKeyService.Build(second).RecordKey);
    }

    private static PcDetailRecord NewRecord() =>
        new(
            "window",
            "2026-07-05T01:00:00Z",
            "2026-07-05T01:10:00Z",
            600,
            "device-1",
            "Code.exe",
            "code",
            "Other",
            "Program.cs",
            null,
            null,
            null,
            null,
            null,
            null);
}
