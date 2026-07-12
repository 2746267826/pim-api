using Pim.Module.Calendar;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarEndpointPathTests
{
    [Fact]
    public void CalendarEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/calendar", CalendarEndpointPaths.Root);
        Assert.Equal("/api/v1/calendar/recycle-bin", CalendarEndpointPaths.RecycleBin);
        Assert.Equal("/api/v1/calendar/events/batch-delete", CalendarEndpointPaths.EventBatchDelete);
        Assert.Equal("/api/v1/calendar/tasks/batch-update", CalendarEndpointPaths.TaskBatchUpdate);
        Assert.Equal("/api/v1/calendar/tasks/batch-delete", CalendarEndpointPaths.TaskBatchDelete);
        Assert.Equal("/api/v1/calendar/import-ics", CalendarEndpointPaths.ImportIcs);
        Assert.Equal("/api/v1/calendar/export-ics", CalendarEndpointPaths.ExportIcs);
        Assert.Equal("/api/v1/calendar/tasks/abc/plan", CalendarEndpointPaths.TaskPlan("abc"));
        Assert.Equal("/api/v1/calendar/recycle-bin/event/abc/restore-preview", CalendarEndpointPaths.RecycleRestorePreview("event", "abc"));
        Assert.Equal("/api/v1/calendar/recycle-bin/event/abc/restore", CalendarEndpointPaths.RecycleRestore("event", "abc"));

        Assert.Equal("/api/v1/calendar/outlook/settings", CalendarEndpointPaths.OutlookSettings);
        Assert.Equal("/api/v1/calendar/outlook/device-code", CalendarEndpointPaths.OutlookDeviceCode);
        Assert.Equal("/api/v1/calendar/outlook/device-code/poll", CalendarEndpointPaths.OutlookDeviceCodePoll);
        Assert.Equal("/api/v1/calendar/outlook/sync", CalendarEndpointPaths.OutlookSync);
        Assert.Equal("/api/v1/calendar/outlook/sync/batches", CalendarEndpointPaths.OutlookSyncBatches);
        Assert.Equal("/api/v1/calendar/outlook/check", CalendarEndpointPaths.OutlookCheck);
        Assert.Equal("/api/v1/calendar/outlook/calendars/discover", CalendarEndpointPaths.OutlookCalendarsDiscover);
        Assert.Equal("/api/v1/calendar/outlook/calendars/selection", CalendarEndpointPaths.OutlookCalendarsSelection);
        Assert.Equal("/api/v1/calendar/outlook/events/writeback", CalendarEndpointPaths.OutlookEventsWriteback);
        Assert.Equal("/api/v1/calendar/outlook/disconnect", CalendarEndpointPaths.OutlookDisconnect);
        Assert.Equal("/api/v1/calendar/outlook/local-data/preview", CalendarEndpointPaths.OutlookLocalDataPreview);
        Assert.Equal("/api/v1/calendar/outlook/local-data", CalendarEndpointPaths.OutlookLocalDataDelete);

        var sessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.Equal($"/api/v1/calendar/outlook/device-code/{sessionId}/cancel", CalendarEndpointPaths.OutlookDeviceCodeCancel(sessionId));
        var batchId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Assert.Equal($"/api/v1/calendar/outlook/sync/{batchId}/cancel", CalendarEndpointPaths.OutlookSyncCancel(batchId));
    }
}
