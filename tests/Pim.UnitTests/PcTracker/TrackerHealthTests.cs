using System;
using System.Threading;
using System.Threading.Tasks;
using Pim.Module.PcTracker.DTOs;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.PcTracker;

public sealed class TrackerHealthTests
{
    private static TrackerHealthRequest Health(
        string deviceId = "pc-1",
        string status = "running",
        bool siteConnected = false,
        double? siteAge = null,
        long siteUploaded = 0,
        string? siteError = null)
        => new(
            DeviceId: deviceId,
            Status: status,
            UptimeSeconds: 100,
            HookActive: true,
            PollCount: 5,
            SessionsCreated: 2,
            EventsUploaded: 42,
            UploadFailures: 1,
            LastError: null,
            BrowserConnected: true,
            BrowserHeartbeatAgeSeconds: 12,
            SiteConnected: siteConnected,
            SiteLastEventAgeSeconds: siteAge,
            SiteEventsUploaded: siteUploaded,
            SiteLastError: siteError);

    [Fact]
    public async Task Record_PersistsSiteChannelFields()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);

        await svc.RecordTrackerHealthAsync(Health(siteConnected: true, siteAge: 8.5, siteUploaded: 77), CancellationToken.None);

        var entity = await svc.GetTrackerHealthAsync("pc-1", CancellationToken.None);
        Assert.NotNull(entity);
        Assert.True(entity!.SiteConnected);
        Assert.Equal(8.5, entity.SiteLastEventAgeSeconds);
        Assert.Equal(77, entity.SiteEventsUploaded);
        Assert.True(entity.BrowserConnected);
    }

    [Fact]
    public async Task Record_UpdatesExistingRowInsteadOfDuplicating()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);

        await svc.RecordTrackerHealthAsync(Health(siteUploaded: 1), CancellationToken.None);
        await svc.RecordTrackerHealthAsync(Health(siteUploaded: 9, siteError: "boom"), CancellationToken.None);

        Assert.Single(db.Set<Pim.Module.PcTracker.Entities.TrackerHealthEntity>());
        var entity = await svc.GetTrackerHealthAsync("pc-1", CancellationToken.None);
        Assert.Equal(9, entity!.SiteEventsUploaded);
        Assert.Equal("boom", entity.SiteLastError);
    }

    [Fact]
    public async Task Latest_ReturnsMostRecentlyReportedDevice()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);

        await svc.RecordTrackerHealthAsync(Health("pc-old"), CancellationToken.None);
        await Task.Delay(5);
        await svc.RecordTrackerHealthAsync(Health("pc-new"), CancellationToken.None);

        // 显式拉开时间差，避免 InMemory 同毫秒写入导致排序不确定
        var oldRow = await svc.GetTrackerHealthAsync("pc-old", CancellationToken.None);
        oldRow!.ReportedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        var latest = await svc.GetLatestTrackerHealthAsync(CancellationToken.None);
        Assert.Equal("pc-new", latest!.DeviceId);
    }

    [Fact]
    public async Task Latest_EmptyDatabase_ReturnsNull()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);

        Assert.Null(await svc.GetLatestTrackerHealthAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Record_InvalidStatusOrDeviceId_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordTrackerHealthAsync(Health(deviceId: ""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordTrackerHealthAsync(Health(status: "wat"), CancellationToken.None));
    }
}
