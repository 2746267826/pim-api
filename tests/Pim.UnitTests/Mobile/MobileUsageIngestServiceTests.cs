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

    [Fact]
    public async Task IngestAsync_SkipsDuplicateEventsAcrossBatches()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var first = await service.IngestAsync(UploadRequest("batch-1", "Messages"), CancellationToken.None);
        var second = await service.IngestAsync(UploadRequest("batch-2", "Messages"), CancellationToken.None);

        Assert.Equal(2, first.AcceptedCount);
        Assert.Equal(0, first.SkippedCount);
        Assert.Equal(0, second.AcceptedCount);
        Assert.Equal(2, second.SkippedCount);
        Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_SkipsDuplicateEventsWithNullClassName()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var request = UploadRequest(
            "batch-null-class",
            "Messages",
            [
                new MobileUsageEventDto(
                    "com.example.messages",
                    "USER_INTERACTION",
                    start.AddMinutes(5),
                    null,
                    start.AddMinutes(6),
                    "{\"event\":\"tap\"}"),
                new MobileUsageEventDto(
                    "com.example.messages",
                    "USER_INTERACTION",
                    start.AddMinutes(5),
                    null,
                    start.AddMinutes(6),
                    "{\"event\":\"tap\"}")
            ]);

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.SkippedCount);
        var usageEvent = Assert.Single(await db.Set<MobileUsageEventEntity>().ToListAsync());
        Assert.Equal(string.Empty, usageEvent.ClassName);
    }

    [Fact]
    public async Task IngestAsync_SkipsDuplicateEventsWhenExistingClassNameIsNull()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.messages",
            EventType = "USER_INTERACTION",
            EventTimestampUtc = start.AddMinutes(5),
            ClassName = null,
            SourceWindowStartUtc = start,
            SourceWindowEndUtc = start.AddHours(1),
            CollectedAtUtc = start.AddMinutes(6),
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = start.AddMinutes(6)
        });
        await db.SaveChangesAsync();

        var result = await service.IngestAsync(
            UploadRequest(
                "batch-existing-null-class",
                "Messages",
                [
                    new MobileUsageEventDto(
                        "com.example.messages",
                        "USER_INTERACTION",
                        start.AddMinutes(5),
                        null,
                        start.AddMinutes(6),
                        "{\"event\":\"tap\"}")
                ]),
            CancellationToken.None);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_RebuildsSessionsWhenRetryingExistingBatch()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var request = UploadRequest("batch-existing", "Messages");
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = request.DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            AcceptedCount = 2,
            FailedCount = 0,
            Status = "completed",
            CreatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z"),
            CompletedAtUtc = DateTimeOffset.Parse("2026-07-06T12:00:00Z")
        });
        foreach (var usageEvent in request.Events)
        {
            db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = request.DeviceId,
                PackageName = usageEvent.PackageName,
                EventType = usageEvent.EventType,
                EventTimestampUtc = usageEvent.EventTimestampUtc,
                ClassName = usageEvent.ClassName,
                SourceWindowStartUtc = request.WindowStartUtc,
                SourceWindowEndUtc = request.WindowEndUtc,
                CollectedAtUtc = usageEvent.CollectedAtUtc,
                RawJson = usageEvent.RawJson,
                QualityFlagsJson = "[]",
                CreatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z")
            });
        }
        await db.SaveChangesAsync();

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(2, result.AcceptedCount);
        var session = Assert.Single(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.Equal("com.example.messages", session.PackageName);
        Assert.Equal(request.Events[0].EventTimestampUtc, session.StartUtc);
        Assert.Equal(request.Events[1].EventTimestampUtc, session.EndUtc);
    }

    private static MobileUsageEventsUploadRequest UploadRequest(
        string batchId,
        string appName,
        IReadOnlyList<MobileUsageEventDto>? events = null)
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
            events ??
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
