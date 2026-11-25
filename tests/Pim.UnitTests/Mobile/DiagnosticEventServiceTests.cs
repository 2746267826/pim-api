using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Module.Mobile.Diagnostics;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class DiagnosticEventServiceTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-25T12:00:00Z");

    [Fact]
    public async Task Upload_IsIdempotentAndScopedByUserAndDevice()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var ownerId = MobileTestHelpers.UserId;
        var otherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var ownerService = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(ownerId),
            MobileTestHelpers.Time(Now));
        var request = Request("device-shared", "event-1", "first");

        var first = await ownerService.UploadAsync(request);
        var replay = await ownerService.UploadAsync(request);

        Assert.Equal(1, first.AcceptedCount);
        Assert.Equal(0, replay.AcceptedCount);
        Assert.Equal(1, replay.SkippedCount);
        Assert.Equal(1, await db.Set<DiagnosticEventEntity>()
            .CountAsync(row => row.UserId == ownerId));

        var otherDb = MobileTestHelpers.CreateDb();
        var otherService = new DiagnosticEventService(
            otherDb,
            MobileTestHelpers.CurrentUser(otherUserId),
            MobileTestHelpers.Time(Now));
        var other = await otherService.UploadAsync(request);
        Assert.Equal(1, other.AcceptedCount);
        Assert.Equal(1, await otherDb.Set<DiagnosticEventEntity>()
            .CountAsync(row => row.UserId == otherUserId && row.DeviceId == "device-shared"));
    }

    [Fact]
    public async Task Upload_RedactsMessageStackAndAttributesBeforePersisting()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        await service.UploadAsync(new DiagnosticEventUploadRequest(
            "device-redaction",
            DiagnosticPlatforms.Windows,
            "batch-1",
            [
                new DiagnosticEventUploadItem(
                    "event-redaction",
                    DiagnosticEventKinds.Error,
                    DiagnosticSeverities.Error,
                    Now,
                    "1.0.0",
                    "Windows 11",
                    "Authorization: Bearer abcdefghijklmnop GraphEventId=graph-secret",
                    @"at C:\Users\alice\AppData\Local\Temp\pim.log:12",
                    new Dictionary<string, string>
                    {
                        ["route"] = "/diagnostics",
                        ["changeKey"] = "change-secret",
                        ["detail"] = "/tmp/pim/dump.bin"
                    })
            ]));

        var row = await db.Set<DiagnosticEventEntity>().SingleAsync();
        Assert.DoesNotContain("abcdefghijklmnop", row.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("graph-secret", row.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\alice", row.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("change-secret", row.AttributesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", row.AttributesJson, StringComparison.Ordinal);
        Assert.Contains("/diagnostics", row.AttributesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_ExceededDailyQuotaReturnsRetryDelayWithoutWriting()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var dayStart = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        db.Set<DiagnosticEventEntity>().AddRange(
            Enumerable.Range(0, DiagnosticLimits.MaxEventsPerUserPerUtcDay)
                .Select(index => new DiagnosticEventEntity
                {
                    UserId = MobileTestHelpers.UserId,
                    DeviceId = "quota-device",
                    Platform = DiagnosticPlatforms.Android,
                    ClientEventId = $"quota-{index}",
                    ClientBatchId = "quota",
                    Kind = DiagnosticEventKinds.Error,
                    Severity = DiagnosticSeverities.Error,
                    AppVersion = "1.0",
                    OsVersion = "Android 15",
                    Message = "quota seed",
                    OccurredAtUtc = dayStart,
                    ReceivedAtUtc = dayStart,
                    CreatedAt = dayStart
                }));
        await db.SaveChangesAsync();
        var before = await db.Set<DiagnosticEventEntity>().CountAsync();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var error = await Assert.ThrowsAsync<DiagnosticQuotaExceededException>(
            () => service.UploadAsync(Request("quota-device", "quota-new", "new")));

        Assert.True(error.RetryAfterSeconds > 0);
        Assert.Equal(before, await db.Set<DiagnosticEventEntity>().CountAsync());
    }

    [Fact]
    public async Task Upload_RedactsSensitiveDeviceIdAndUsesTheSameValueForFiltering()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));
        const string rawDeviceId = "Bearer abcdefghijklmnop /tmp/pim/device.bin";

        var result = await service.UploadAsync(
            new DiagnosticEventUploadRequest(
                rawDeviceId,
                DiagnosticPlatforms.Android,
                "device-redaction-batch",
                [Item("device-redaction-event", "device id redaction")]));

        Assert.Equal(1, result.AcceptedCount);
        var row = await db.Set<DiagnosticEventEntity>().SingleAsync();
        Assert.DoesNotContain("abcdefghijklmnop", row.DeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", row.DeviceId, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.Marker, row.DeviceId, StringComparison.Ordinal);

        var overview = await service.GetOverviewAsync(rawDeviceId, null, 10);
        Assert.Single(overview.Items);
        Assert.Equal(row.DeviceId, overview.Items[0].DeviceId);
    }

    [Fact]
    public async Task Export_RedactsSensitiveDeviceIdThatPredatesIngestionPolicy()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<DiagnosticEventEntity>().Add(
            Event(MobileTestHelpers.UserId, "Bearer abcdefghijklmnop /tmp/pim/legacy.bin", "legacy-device"));
        await db.SaveChangesAsync();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var events = await service.GetExportEventsAsync(null, null, 7);

        var exported = Assert.Single(events);
        Assert.DoesNotContain("abcdefghijklmnop", exported.DeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", exported.DeviceId, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.Marker, exported.DeviceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_RedactsHistoricalSensitiveDeviceIdsAndMergesThem()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<DiagnosticEventEntity>().AddRange(
            Event(
                MobileTestHelpers.UserId,
                "Bearer abcdefghijklmnop",
                "legacy-sensitive-1"),
            Event(
                MobileTestHelpers.UserId,
                "Bearer zyxwvutsrqponmlk",
                "legacy-sensitive-2"));
        await db.SaveChangesAsync();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var overview = await service.GetOverviewAsync(null, null, 10);

        Assert.All(overview.Items, item =>
        {
            Assert.DoesNotContain("abcdefghijklmnop", item.DeviceId, StringComparison.Ordinal);
            Assert.DoesNotContain("zyxwvutsrqponmlk", item.DeviceId, StringComparison.Ordinal);
            Assert.Contains(DiagnosticRedaction.Marker, item.DeviceId, StringComparison.Ordinal);
        });
        var device = Assert.Single(overview.Devices);
        Assert.Equal(2, device.EventCount);
        Assert.Equal(DiagnosticPlatforms.Web, device.Platform);
        Assert.Contains(DiagnosticRedaction.Marker, device.DeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", device.DeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("zyxwvutsrqponmlk", device.DeviceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_RejectsMoreEventsThanTheConfiguredLimit()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<DiagnosticEventEntity>().AddRange(
            Enumerable.Range(0, DiagnosticLimits.MaxEventsInExport + 1)
                .Select(index => Event(
                    MobileTestHelpers.UserId,
                    "export-limit-device",
                    $"export-limit-{index}")));
        await db.SaveChangesAsync();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.GetExportEventsAsync(null, null, 7));

        Assert.Equal(41302, error.ErrorCode);
    }

    [Fact]
    public async Task Overview_UsesCurrentUserScopeAndRejectsOversizedPage()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var ownerId = MobileTestHelpers.UserId;
        db.Set<DiagnosticEventEntity>().AddRange(
            Event(ownerId, "owner-device", "owner-1"),
            Event(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "owner-device", "other-1"));
        await db.SaveChangesAsync();
        var service = new DiagnosticEventService(
            db,
            MobileTestHelpers.CurrentUser(ownerId),
            MobileTestHelpers.Time(Now));

        var overview = await service.GetOverviewAsync(null, null, 50);

        Assert.Equal(1, overview.TotalCount);
        Assert.Single(overview.Items);
        Assert.Equal(ownerId, await db.Set<DiagnosticEventEntity>()
            .Where(row => row.Id == overview.Items[0].Id)
            .Select(row => row.UserId)
            .SingleAsync());
        await Assert.ThrowsAsync<DomainException>(
            () => service.GetOverviewAsync(null, null, DiagnosticLimits.MaxEventsInOverview + 1));
    }

    private static DiagnosticEventUploadRequest Request(
        string deviceId,
        string clientEventId,
        string message)
        => new(
            deviceId,
            DiagnosticPlatforms.Android,
            "batch-1",
            [
                new DiagnosticEventUploadItem(
                    clientEventId,
                    DiagnosticEventKinds.Error,
                    DiagnosticSeverities.Error,
                    Now,
                    "1.0.0",
                    "Android 15",
                    message)
            ]);

    private static DiagnosticEventUploadItem Item(string clientEventId, string message)
        => new(
            clientEventId,
            DiagnosticEventKinds.Error,
            DiagnosticSeverities.Error,
            Now,
            "1.0.0",
            "Android 15",
            message);

    private static DiagnosticEventEntity Event(Guid userId, string deviceId, string clientEventId)
        => new()
        {
            UserId = userId,
            DeviceId = deviceId,
            Platform = DiagnosticPlatforms.Web,
            ClientEventId = clientEventId,
            ClientBatchId = "batch",
            Kind = DiagnosticEventKinds.Manual,
            Severity = DiagnosticSeverities.Warning,
            AppVersion = "1.0",
            OsVersion = "Web",
            Message = "event",
            OccurredAtUtc = Now,
            ReceivedAtUtc = Now,
            CreatedAt = Now
        };
}
