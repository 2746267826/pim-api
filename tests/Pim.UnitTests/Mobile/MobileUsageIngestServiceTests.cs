using Microsoft.EntityFrameworkCore;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileUsageIngestServiceTests
{
    [Fact]
    public async Task IngestAsync_IsIdempotentAndStoresFallbackSummariesSeparately()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        var request = UploadRequest("batch-1", "Messages");

        var first = await service.IngestAsync(request, CancellationToken.None);
        var second = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(first.BatchId, second.BatchId);
        Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
        Assert.Equal(1, await db.Set<MobileUsageSummaryEntity>().CountAsync());
        Assert.Equal(1, await db.Set<MobileAppCatalogEntity>().CountAsync());
        Assert.Equal(2, first.AcceptedCount);
        Assert.Equal(0, first.FailedCount);
    }

    [Fact]
    public async Task IngestAsync_UpsertsAppMetadataByPackageName()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        await service.IngestAsync(UploadRequest("batch-1", "Messages"), CancellationToken.None);
        await service.IngestAsync(UploadRequest("batch-2", "Messages Beta"), CancellationToken.None);

        var app = await db.Set<MobileAppCatalogEntity>().SingleAsync();
        Assert.Equal("com.example.messages", app.PackageName);
        Assert.Equal("Messages Beta", app.DisplayName);
    }

    private static MobileUsageEventsUploadRequest UploadRequest(string batchId, string appName)
    {
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T09:00:00Z");

        return new MobileUsageEventsUploadRequest(
            "android-main",
            batchId,
            start,
            end,
            [
                new MobileAppMetadataDto(
                    "com.example.messages",
                    appName,
                    "1.2.3",
                    123,
                    false,
                    "communication",
                    "com.android.vending",
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                    "{}")
            ],
            [
                new MobileUsageEventDto(
                    "com.example.messages",
                    "MOVE_TO_FOREGROUND",
                    start.AddMinutes(5),
                    "MainActivity",
                    start.AddMinutes(6),
                    "{\"event\":\"fg\"}"),
                new MobileUsageEventDto(
                    "com.example.messages",
                    "MOVE_TO_BACKGROUND",
                    start.AddMinutes(25),
                    "MainActivity",
                    start.AddMinutes(26),
                    "{\"event\":\"bg\"}")
            ],
            [
                new MobileUsageSummaryDto(
                    "com.example.messages",
                    start,
                    end,
                    1_200_000,
                    start.AddMinutes(25),
                    "usage-stats-fallback",
                    "{\"summary\":true}")
            ]);
    }
}
