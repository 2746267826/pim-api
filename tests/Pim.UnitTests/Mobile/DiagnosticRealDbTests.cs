using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Diagnostics;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Xunit;

namespace Pim.UnitTests.Mobile;

[Trait("DataSource", "RealDb")]
public sealed class DiagnosticRealDbTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-25T12:00:00Z");

    [SkippableFact]
    public async Task ConcurrentReplay_HitsTheRealUniqueIndexAndStillPersistsOneRow()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var connectionString = scope.Db.Database.GetConnectionString()!;
        await using var secondDb = new PimDbContext(
            new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(connectionString)
                .Options,
            MobileTestHelpers.CurrentUser());
        var first = new DiagnosticEventService(
            scope.Db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));
        var second = new DiagnosticEventService(
            secondDb,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));
        var request = Request("android-realdb", "same-event", "concurrent");

        var results = await Task.WhenAll(
            first.UploadAsync(request),
            second.UploadAsync(request));

        Assert.Equal(1, results.Sum(result => result.AcceptedCount));
        Assert.Equal(1, results.Sum(result => result.SkippedCount));
        Assert.Equal(1, await scope.Db.Set<DiagnosticEventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task SameDeviceAndClientEventId_RemainIsolatedAcrossAccounts()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var otherUserId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var connectionString = scope.Db.Database.GetConnectionString()!;
        await using var otherDb = new PimDbContext(
            new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(connectionString)
                .Options,
            MobileTestHelpers.CurrentUser(otherUserId));
        var owner = new DiagnosticEventService(
            scope.Db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));
        var other = new DiagnosticEventService(
            otherDb,
            MobileTestHelpers.CurrentUser(otherUserId),
            MobileTestHelpers.Time(Now));

        var ownerUpload = await owner.UploadAsync(Request("shared-device", "shared-event", "owner"));
        var otherUpload = await other.UploadAsync(Request("shared-device", "shared-event", "other"));
        var ownerOverview = await owner.GetOverviewAsync("shared-device", null, 10);
        var otherOverview = await other.GetOverviewAsync("shared-device", null, 10);

        Assert.Equal(1, ownerUpload.AcceptedCount);
        Assert.Equal(1, otherUpload.AcceptedCount);
        Assert.Equal(2, await scope.Db.Set<DiagnosticEventEntity>()
            .IgnoreQueryFilters()
            .CountAsync());
        Assert.Single(ownerOverview.Items);
        Assert.Equal("owner", ownerOverview.Items[0].Message);
        Assert.Single(otherOverview.Items);
        Assert.Equal("other", otherOverview.Items[0].Message);
    }

    [SkippableFact]
    public async Task MixedBatch_UniqueConflictSkipsOnlyTheConflictingRow()
    {
        await using var scope = await RealDbScope.CreateAsync(MobileTestHelpers.UserId);
        var connectionString = scope.Db.Database.GetConnectionString()!;
        const string conflictId = "mixed-conflict";
        await using var conflictDb = new PimDbContext(
            new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(connectionString)
                .Options,
            MobileTestHelpers.CurrentUser());
        var interceptor = new InsertConflictingEventInterceptor(async () =>
        {
            conflictDb.Set<DiagnosticEventEntity>().Add(
                Event(MobileTestHelpers.UserId, "mixed-device", conflictId, "concurrent"));
            await conflictDb.SaveChangesAsync();
        });
        await using var subjectDb = new PimDbContext(
            new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptor)
                .Options,
            MobileTestHelpers.CurrentUser());
        var service = new DiagnosticEventService(
            subjectDb,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var result = await service.UploadAsync(
            new DiagnosticEventUploadRequest(
                "mixed-device",
                DiagnosticPlatforms.Android,
                "mixed-batch",
                [
                    Item("mixed-new", "new"),
                    Item(conflictId, "batched-duplicate")
                ]));

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Contains(
            result.Items,
            item => item.ClientEventId == "mixed-new" && item.Outcome == "accepted");
        Assert.Contains(
            result.Items,
            item => item.ClientEventId == conflictId && item.Outcome == "skipped");
        var rows = await scope.Db.Set<DiagnosticEventEntity>()
            .Where(row => row.UserId == MobileTestHelpers.UserId && row.DeviceId == "mixed-device")
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.ClientEventId == conflictId && row.Message == "concurrent");
        Assert.Contains(rows, row => row.ClientEventId == "mixed-new" && row.Message == "new");
    }

    private static DiagnosticEventUploadItem Item(string clientEventId, string message)
        => new(
            clientEventId,
            DiagnosticEventKinds.Error,
            DiagnosticSeverities.Error,
            Now,
            "1.0.0",
            "Android 15",
            message);

    private static DiagnosticEventEntity Event(
        Guid userId,
        string deviceId,
        string clientEventId,
        string message)
        => new()
        {
            UserId = userId,
            DeviceId = deviceId,
            Platform = DiagnosticPlatforms.Android,
            ClientEventId = clientEventId,
            ClientBatchId = "concurrent-batch",
            Kind = DiagnosticEventKinds.Error,
            Severity = DiagnosticSeverities.Error,
            AppVersion = "1.0.0",
            OsVersion = "Android 15",
            Message = message,
            OccurredAtUtc = Now,
            ReceivedAtUtc = Now,
            CreatedAt = Now
        };

    private static DiagnosticEventUploadRequest Request(
        string deviceId,
        string clientEventId,
        string message)
        => new(
            deviceId,
            DiagnosticPlatforms.Android,
            "real-db-batch",
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

    private sealed class InsertConflictingEventInterceptor(Func<Task> insert) : SaveChangesInterceptor
    {
        private int _inserted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _inserted, 1) == 0)
            {
                await insert();
            }

            return result;
        }
    }
}
