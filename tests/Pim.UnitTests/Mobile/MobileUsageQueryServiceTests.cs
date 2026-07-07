using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileUsageQueryServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_ReturnsLocationCountsForBatchWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-location",
            WindowStartUtc = now.AddHours(-2),
            WindowEndUtc = now.AddHours(-1),
            AcceptedCount = 3,
            FailedCount = 0,
            Status = "completed",
            ErrorJson = "{}",
            CreatedAt = now.AddMinutes(-30),
            CompletedAtUtc = now.AddMinutes(-29)
        });
        db.Set<MobileLocationPointEntity>().AddRange(
            Location(now.AddMinutes(-100), "usable", 8),
            Location(now.AddMinutes(-90), "rejected", 75));
        await db.SaveChangesAsync();

        var service = new MobileUsageQueryService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(now));

        var summary = await service.GetSummaryAsync(new MobileSummaryQuery(
            "android-main",
            now.AddHours(-3),
            now), CancellationToken.None);

        var batch = Assert.Single(summary.SyncBatches);
        Assert.Equal(1, batch.AcceptedLocationCount);
        Assert.Equal(1, batch.RejectedLocationCount);
    }

    [Fact]
    public void WhereFallbackSummaries_BuildsRelationalQueryWithoutClientMethod()
    {
        MobileTestHelpers.RegisterMobileModule();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Database=pim_translation_tests")
            .Options;
        using var db = new PimDbContext(options);

        var sql = MobileUsageQueryService
            .WhereFallbackSummaries(db.Set<MobileUsageSummaryEntity>().AsNoTracking())
            .ToQueryString();

        Assert.Contains("fallback", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("summary", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static MobileLocationPointEntity Location(DateTimeOffset recordedAt, string quality, decimal accuracy)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            RecordedAtUtc = recordedAt,
            Latitude = 31.230416m,
            Longitude = 121.473701m,
            HorizontalAccuracyMeters = accuracy,
            Provider = "gps",
            Source = "manual",
            Quality = quality,
            RawJson = "{}",
            CreatedAt = recordedAt.AddSeconds(3)
        };
}
