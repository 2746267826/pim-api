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
    }
}
