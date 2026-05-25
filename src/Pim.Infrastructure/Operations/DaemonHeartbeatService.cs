using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class DaemonHeartbeatService : IDaemonHeartbeatService
{
    private readonly PimDbContext _db;

    public DaemonHeartbeatService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<DaemonHeartbeatDto> UpsertAsync(
        DaemonHeartbeatRequest request,
        CancellationToken ct = default)
    {
        var entity = await _db.DaemonHeartbeats
            .SingleOrDefaultAsync(d =>
                d.DeviceId == request.DeviceId
                && d.DaemonKind == request.DaemonKind,
                ct);

        if (entity is null)
        {
            entity = new DaemonHeartbeatEntity
            {
                DeviceId = request.DeviceId,
                DaemonKind = request.DaemonKind
            };
            _db.DaemonHeartbeats.Add(entity);
        }

        entity.Version = request.Version;
        entity.ServerUrl = request.ServerUrl;
        entity.LastSuccessfulUploadAt = request.LastSuccessfulUploadAt;
        entity.LastAttemptedUploadAt = request.LastAttemptedUploadAt;
        entity.LastError = request.LastError;
        entity.UploadQueueCount = request.UploadQueueCount;
        entity.ActivityWatchState = request.ActivityWatchState.ToString();
        entity.KeyStatsState = request.KeyStatsState.ToString();
        entity.CollectionPaused = request.CollectionPaused;
        entity.StatusJson = string.IsNullOrWhiteSpace(request.StatusJson) ? "{}" : request.StatusJson;
        entity.ReceivedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<DaemonHeartbeatDto?> GetLatestAsync(string deviceId, CancellationToken ct = default)
    {
        var entity = await _db.DaemonHeartbeats
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Map(entity);
    }

    public async Task<DaemonHeartbeatDto?> GetLatestWindowsAsync(CancellationToken ct = default)
    {
        var entity = await _db.DaemonHeartbeats
            .AsNoTracking()
            .Where(d => d.DaemonKind == "windows")
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Map(entity);
    }

    private static DaemonHeartbeatDto Map(DaemonHeartbeatEntity entity)
    {
        return new DaemonHeartbeatDto(
            entity.DeviceId,
            entity.DaemonKind,
            entity.Version,
            entity.ServerUrl,
            entity.LastSuccessfulUploadAt,
            entity.LastAttemptedUploadAt,
            entity.LastError,
            entity.UploadQueueCount,
            Enum.Parse<DaemonSourceState>(entity.ActivityWatchState),
            Enum.Parse<DaemonSourceState>(entity.KeyStatsState),
            entity.CollectionPaused,
            entity.StatusJson,
            entity.ReceivedAt);
    }
}
