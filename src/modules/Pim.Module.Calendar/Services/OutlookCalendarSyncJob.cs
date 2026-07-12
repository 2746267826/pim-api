using Pim.Module.Calendar.DTOs;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookCalendarSyncJob
{
    private readonly OutlookCalendarSyncService _sync;

    public OutlookCalendarSyncJob(OutlookCalendarSyncService sync) => _sync = sync;

    public async Task RunAllAsync()
    {
        foreach (var userId in await _sync.ListRunnableUsersAsync(CancellationToken.None))
            await _sync.SyncAsync(userId, new OutlookSyncRequest(Mode: "normal"), CancellationToken.None);
    }
}
