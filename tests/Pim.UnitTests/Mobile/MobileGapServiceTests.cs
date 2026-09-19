using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileGapServiceTests
{
    [Fact]
    public async Task GetGapsAsync_ClampsRequestedRangeToMostRecentFourteenDays()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var response = await service.GetGapsAsync(new MobileGapRequest(
            "android-main",
            now.AddDays(-45),
            now,
            "{\"usageEvents\":true}"), CancellationToken.None);

        var expectedStart = now.AddDays(-14);
        Assert.Equal(expectedStart, response.MaxBackfillStartUtc);
        Assert.NotEmpty(response.Windows);
        Assert.All(response.Windows, window => Assert.True(window.WindowStartUtc >= expectedStart));
        Assert.Equal(expectedStart, response.Windows.Min(window => window.WindowStartUtc));
    }

    [Fact]
    public async Task GetGapsAsync_ReturnsOnlyMissingFallbackOrCurrentTailWindows()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.events",
            EventType = "ACTIVITY_RESUMED",
            EventTimestampUtc = DateTimeOffset.Parse("2026-07-04T10:00:00Z"),
            SourceWindowStartUtc = DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            SourceWindowEndUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            CollectedAtUtc = DateTimeOffset.Parse("2026-07-04T10:01:00Z"),
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = DateTimeOffset.Parse("2026-07-04T10:02:00Z")
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.fallback",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            WindowEndUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            TotalTimeVisibleMs = 60_000,
            SourceKind = "usage-stats-fallback",
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = DateTimeOffset.Parse("2026-07-05T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-05T12:00:00Z")
        });
        await db.SaveChangesAsync();
        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var response = await service.GetGapsAsync(new MobileGapRequest(
            "android-main",
            DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            now,
            "{\"usageEvents\":true}"), CancellationToken.None);

        Assert.DoesNotContain(response.Windows, window => window.WindowStartUtc == DateTimeOffset.Parse("2026-07-04T00:00:00Z"));
        Assert.Contains(response.Windows, window => window.Reason == "fallback-only"
            && window.WindowStartUtc == DateTimeOffset.Parse("2026-07-05T00:00:00Z"));
        Assert.Contains(response.Windows, window => window.Reason == "missing-day"
            && window.WindowStartUtc == DateTimeOffset.Parse("2026-07-06T00:00:00Z"));
    }

    [Fact]
    public async Task GetGapsAsync_ReturnsMissingTailWhenCurrentDayHasOnlyEarlyCoverage()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.events",
            EventType = "ACTIVITY_RESUMED",
            EventTimestampUtc = DateTimeOffset.Parse("2026-07-06T01:30:00Z"),
            SourceWindowStartUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            SourceWindowEndUtc = DateTimeOffset.Parse("2026-07-06T02:00:00Z"),
            CollectedAtUtc = DateTimeOffset.Parse("2026-07-06T02:01:00Z"),
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = DateTimeOffset.Parse("2026-07-06T02:02:00Z")
        });
        await db.SaveChangesAsync();
        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var response = await service.GetGapsAsync(new MobileGapRequest(
            "android-main",
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            now,
            "{\"usageEvents\":true}"), CancellationToken.None);

        var window = Assert.Single(response.Windows);
        Assert.Equal("missing-tail", window.Reason);
        Assert.Equal(DateTimeOffset.Parse("2026-07-06T02:00:00Z"), window.WindowStartUtc);
        Assert.Equal(now, window.WindowEndUtc);
    }

    [Fact]
    public async Task GetGapsAsync_TreatsCompletedEmptyBatchAsCovered()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "empty-day",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            WindowEndUtc = now,
            AcceptedCount = 0,
            FailedCount = 0,
            Status = "completed",
            ErrorJson = "{}",
            CreatedAt = now.AddMinutes(-5),
            CompletedAtUtc = now.AddMinutes(-4)
        });
        await db.SaveChangesAsync();
        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var response = await service.GetGapsAsync(new MobileGapRequest(
            "android-main",
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            now,
            "{\"usageEvents\":true}"), CancellationToken.None);

        Assert.Empty(response.Windows);
    }

    [Fact]
    public async Task GetGapsAsync_SkippedOnlyBatchCoversItsWindow()
    {
        // S5/S6 覆盖联动对照（EPIC #254 S11 空转批次）：
        // 写入侧修复后，空载上传不再产生批次行（真无数据窗口如实报缺口）；
        // 但"只含跳过条目"的合法批次（skipped_count > 0，#243）仍覆盖其窗口——
        // 普通空闲窗口（有零时长汇总被跳过）不会因此变成覆盖漏洞。
        // 历史"全零空载行"的覆盖语义由既有用例 GetGapsAsync_TreatsCompletedEmptyBatchAsCovered 守护（数据侧不回填不清理）。
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-skipped-only",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            WindowEndUtc = now,
            AcceptedCount = 0,
            FailedCount = 0,
            RejectedCount = 0,
            SkippedCount = 3,
            Status = "completed",
            ErrorJson = "{}",
            CreatedAt = now.AddMinutes(-5),
            CompletedAtUtc = now.AddMinutes(-4)
        });
        await db.SaveChangesAsync();
        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var response = await service.GetGapsAsync(new MobileGapRequest(
            "android-main",
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            now,
            "{\"usageEvents\":true}"), CancellationToken.None);

        Assert.Empty(response.Windows);
    }
}
