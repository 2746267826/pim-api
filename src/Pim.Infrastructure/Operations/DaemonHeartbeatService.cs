using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class DaemonHeartbeatService : IDaemonHeartbeatService
{
    private readonly PimDbContext _db;
    private readonly TimeProvider _timeProvider;

    public DaemonHeartbeatService(PimDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DaemonHeartbeatDto> UpsertAsync(
        DaemonHeartbeatRequest request,
        CancellationToken ct = default)
    {
        var statusJson = NormalizeStatusJson(request.StatusJson);
        var entity = await _db.DaemonHeartbeats
            .SingleOrDefaultAsync(d =>
                d.DeviceId == request.DeviceId
                && d.DaemonKind == request.DaemonKind,
                ct);

        var isNew = entity is null;
        if (entity is null)
        {
            entity = new DaemonHeartbeatEntity
            {
                DeviceId = request.DeviceId,
                DaemonKind = request.DaemonKind
            };
            _db.DaemonHeartbeats.Add(entity);
        }

        Apply(request, statusJson, entity);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            _db.ChangeTracker.Clear();
            entity = await _db.DaemonHeartbeats
                .SingleOrDefaultAsync(d =>
                    d.DeviceId == request.DeviceId
                    && d.DaemonKind == request.DaemonKind,
                    ct);

            if (entity is null)
            {
                throw;
            }

            Apply(request, statusJson, entity);
            await _db.SaveChangesAsync(ct);
        }

        return Map(entity);
    }

    public async Task<DaemonHeartbeatDto> RecordPlannedOfflineAsync(
        PlannedOfflineRequest request,
        CancellationToken ct = default)
    {
        var entity = await _db.DaemonHeartbeats
            .SingleOrDefaultAsync(d =>
                d.DeviceId == request.DeviceId
                && d.DaemonKind == request.DaemonKind,
                ct);

        var isNew = entity is null;
        if (entity is null)
        {
            entity = new DaemonHeartbeatEntity
            {
                DeviceId = request.DeviceId,
                DaemonKind = request.DaemonKind
            };
            _db.DaemonHeartbeats.Add(entity);
        }

        ApplyPlannedOffline(request, entity, isNew);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            _db.ChangeTracker.Clear();
            entity = await _db.DaemonHeartbeats
                .SingleOrDefaultAsync(d =>
                    d.DeviceId == request.DeviceId
                    && d.DaemonKind == request.DaemonKind,
                    ct);

            if (entity is null)
            {
                throw;
            }

            ApplyPlannedOffline(request, entity, isNew: false);
            await _db.SaveChangesAsync(ct);
        }

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
            ParseSourceState(entity.ActivityWatchState),
            ParseSourceState(entity.KeyStatsState),
            entity.CollectionPaused,
            entity.StatusJson,
            entity.ReceivedAt,
            entity.PlannedOfflineAt,
            entity.OfflineReason);
    }

    private void ApplyPlannedOffline(
        PlannedOfflineRequest request,
        DaemonHeartbeatEntity entity,
        bool isNew)
    {
        // planned_offline 只写 planned 标记，不刷新 received_at（received_at 语义 = 最近普通心跳）。
        // 客户端时钟可能早于服务端时钟：钳制 planned_at >= received_at，保证分类器不把正常下线误判为 stale。
        var plannedAt = request.OccurredAt ?? _timeProvider.GetUtcNow();
        if (!isNew && plannedAt < entity.ReceivedAt)
        {
            plannedAt = entity.ReceivedAt;
        }

        entity.PlannedOfflineAt = plannedAt;
        entity.OfflineReason = Truncate(request.Reason, 32);

        // 新建行时用同一注入时钟统一 received_at/planned_offline_at，保证 planned_offline_at >= received_at 恒成立。
        if (isNew)
        {
            entity.ReceivedAt = plannedAt;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private void Apply(
        DaemonHeartbeatRequest request,
        string statusJson,
        DaemonHeartbeatEntity entity)
    {
        entity.Version = request.Version;
        entity.ServerUrl = request.ServerUrl;
        entity.LastSuccessfulUploadAt = request.LastSuccessfulUploadAt;
        entity.LastAttemptedUploadAt = request.LastAttemptedUploadAt;
        entity.LastError = request.LastError;
        entity.UploadQueueCount = request.UploadQueueCount;
        entity.ActivityWatchState = request.ActivityWatchState.ToString();
        entity.KeyStatsState = request.KeyStatsState.ToString();
        entity.CollectionPaused = request.CollectionPaused;
        entity.StatusJson = statusJson;
        entity.ReceivedAt = _timeProvider.GetUtcNow();
        entity.PlannedOfflineAt = null;
        entity.OfflineReason = null;
    }

    private static string NormalizeStatusJson(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return "{}";
        }

        try
        {
            using var _ = JsonDocument.Parse(statusJson);
            return statusJson;
        }
        catch (JsonException)
        {
            throw new DomainException(3010, "StatusJson 必须是有效 JSON");
        }
    }

    private static DaemonSourceState ParseSourceState(string value)
        => Enum.TryParse<DaemonSourceState>(value, ignoreCase: true, out var state)
            ? state
            : DaemonSourceState.Unknown;
}
