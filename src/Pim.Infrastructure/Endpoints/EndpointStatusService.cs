using Microsoft.EntityFrameworkCore;
using Pim.Core.Endpoints;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Endpoints;

public sealed class EndpointStatusService
{
    private static readonly HashSet<string> OfflineCacheableKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "pc-activity",
        "android-location",
        "collection-upload",
        "window-context",
        "browser-context",
        "input-activity",
        "device-state",
        "upload-retry",
        "mobile-sensor",
        "location-sample"
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public EndpointStatusService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public bool CanCacheOffline(string operationKind)
        => OfflineCacheableKinds.Contains((operationKind ?? string.Empty).Trim());

    public async Task<IReadOnlyList<EndpointStatusDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var states = await _db.EndpointStatuses
            .AsNoTracking()
            .Where(endpoint => endpoint.UserId == userId)
            .ToListAsync(ct);

        return states
            .OrderByDescending(endpoint => endpoint.LastHeartbeatAt ?? DateTimeOffset.MinValue)
            .ThenBy(endpoint => endpoint.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(MapStatus)
            .ToList();
    }

    public async Task<EndpointStatusDto> UpsertHeartbeatAsync(
        string deviceId,
        EndpointHeartbeatRequest request,
        CancellationToken ct = default)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var now = DateTimeOffset.UtcNow;
        var userId = UserId;

        var state = await _db.EndpointStatuses
            .FirstOrDefaultAsync(endpoint =>
                endpoint.UserId == userId && endpoint.DeviceId == normalizedDeviceId, ct);
        if (state is null)
        {
            state = new EndpointStatusEntity
            {
                UserId = userId,
                DeviceId = normalizedDeviceId,
                CreatedAt = now
            };
            _db.EndpointStatuses.Add(state);
        }

        ApplyHeartbeat(state, request, now);
        await _db.SaveChangesAsync(ct);
        return MapStatus(state);
    }

    public async Task<EndpointCollectionQualityDto> GetCollectionQualityAsync(
        string deviceId,
        CancellationToken ct = default)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var state = await GetOrCreateStateAsync(normalizedDeviceId, InferPlatform(normalizedDeviceId), ct);

        var issueCount = 0;
        if (!string.Equals(state.UploadStatus, "Healthy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state.UploadStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            issueCount++;
        }

        if (state.CollectionCacheCount > 0)
        {
            issueCount++;
        }

        return new EndpointCollectionQualityDto(
            state.DeviceId,
            state.Platform,
            state.UploadStatus,
            issueCount,
            DateTimeOffset.UtcNow);
    }

    public async Task<EndpointNotificationActionResponse> HandleNotificationActionAsync(
        string deviceId,
        EndpointNotificationActionRequest request,
        CancellationToken ct = default)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var state = await GetOrCreateStateAsync(normalizedDeviceId, InferPlatform(normalizedDeviceId), ct);

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            var rejected = new EndpointNotificationActionResponse(
                "Rejected",
                null,
                "Notification action is required.");
            await RecordNotificationActionAsync(state, request, rejected, ct);
            return rejected;
        }

        EndpointNotificationActionResponse response;
        if (CanExecuteDirectly(request.RiskLevel))
        {
            response = new EndpointNotificationActionResponse(
                "Executed",
                null,
                "Low-risk notification action executed online.");
        }
        else
        {
            state.OnlineOnlyBlockedCount++;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            response = new EndpointNotificationActionResponse(
                "OpenDetailRequired",
                BuildDetailUrl(request),
                "High-risk notification action requires the Web confirmation detail.");
        }

        await RecordNotificationActionAsync(state, request, response, ct);
        return response;
    }

    private async Task<EndpointStatusEntity> GetOrCreateStateAsync(
        string deviceId,
        string platform,
        CancellationToken ct)
    {
        var userId = UserId;
        var state = await _db.EndpointStatuses
            .FirstOrDefaultAsync(endpoint => endpoint.UserId == userId && endpoint.DeviceId == deviceId, ct);
        if (state is not null)
        {
            return state;
        }

        var now = DateTimeOffset.UtcNow;
        state = new EndpointStatusEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            Platform = platform,
            UploadStatus = "Unknown",
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.EndpointStatuses.Add(state);
        await _db.SaveChangesAsync(ct);
        return state;
    }

    private async Task RecordNotificationActionAsync(
        EndpointStatusEntity state,
        EndpointNotificationActionRequest request,
        EndpointNotificationActionResponse response,
        CancellationToken ct)
    {
        _db.EndpointNotificationActions.Add(new EndpointNotificationActionEntity
        {
            UserId = state.UserId,
            DeviceId = state.DeviceId,
            Action = (request.Action ?? string.Empty).Trim(),
            RiskLevel = (request.RiskLevel ?? string.Empty).Trim(),
            Result = response.Result,
            DetailUrl = response.DetailUrl,
            Message = response.Message,
            ConfirmationId = request.ConfirmationId,
            RelatedObjectType = request.RelatedObjectType,
            RelatedObjectId = request.RelatedObjectId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private static bool CanExecuteDirectly(string riskLevel)
        => string.Equals(riskLevel, "Low", StringComparison.OrdinalIgnoreCase)
            || string.Equals(riskLevel, "L0AutomaticArtifact", StringComparison.OrdinalIgnoreCase)
            || string.Equals(riskLevel, "L1LowRiskAction", StringComparison.OrdinalIgnoreCase);

    private static string BuildDetailUrl(EndpointNotificationActionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConfirmationId))
        {
            return $"/confirmations/{Uri.EscapeDataString(request.ConfirmationId)}";
        }

        if (!string.IsNullOrWhiteSpace(request.RelatedObjectType)
            && !string.IsNullOrWhiteSpace(request.RelatedObjectId))
        {
            return $"/audit/{Uri.EscapeDataString(request.RelatedObjectType)}/{Uri.EscapeDataString(request.RelatedObjectId)}";
        }

        return "/confirmations";
    }

    private static EndpointStatusDto MapStatus(EndpointStatusEntity state)
        => new(
            state.DeviceId,
            state.Platform,
            state.AppVersion,
            state.UploadStatus,
            state.CollectionCacheCount,
            state.OnlineOnlyBlockedCount,
            state.LastHeartbeatAt);

    private static string NormalizeDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id is required.", nameof(deviceId));
        }

        return deviceId.Trim();
    }

    private static string NormalizePlatform(string? platform)
    {
        var value = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return value is "android" or "windows" ? value : "windows";
    }

    private static string InferPlatform(string deviceId)
        => deviceId.Contains("android", StringComparison.OrdinalIgnoreCase)
            ? "android"
            : "windows";

    private static void ApplyHeartbeat(
        EndpointStatusEntity state,
        EndpointHeartbeatRequest request,
        DateTimeOffset now)
    {
        state.Platform = NormalizePlatform(request.Platform);
        state.AppVersion = request.AppVersion;
        state.UploadStatus = NormalizeUploadStatus(request.UploadStatus);
        state.CollectionCacheCount = Math.Max(0, request.CollectionCacheCount ?? 0);
        state.LastHeartbeatAt = now;
        state.UpdatedAt = now;
    }

    private static string NormalizeUploadStatus(string? uploadStatus)
        => string.IsNullOrWhiteSpace(uploadStatus) ? "Unknown" : uploadStatus.Trim();
}
