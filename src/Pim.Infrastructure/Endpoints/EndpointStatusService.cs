using System.Collections.Concurrent;
using Pim.Core.Endpoints;

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

    private readonly ConcurrentDictionary<string, EndpointState> _states = new(StringComparer.OrdinalIgnoreCase);

    public bool CanCacheOffline(string operationKind)
        => OfflineCacheableKinds.Contains((operationKind ?? string.Empty).Trim());

    public Task<IReadOnlyList<EndpointStatusDto>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<EndpointStatusDto> result = _states.Values
            .OrderByDescending(endpoint => endpoint.LastHeartbeatAt ?? DateTimeOffset.MinValue)
            .ThenBy(endpoint => endpoint.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(MapStatus)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<EndpointStatusDto> UpsertHeartbeatAsync(
        string deviceId,
        EndpointHeartbeatRequest request,
        CancellationToken ct = default)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var now = DateTimeOffset.UtcNow;

        var state = _states.AddOrUpdate(
            normalizedDeviceId,
            _ => EndpointState.FromHeartbeat(normalizedDeviceId, request, now),
            (_, existing) =>
            {
                existing.ApplyHeartbeat(request, now);
                return existing;
            });

        return Task.FromResult(MapStatus(state));
    }

    public Task<EndpointCollectionQualityDto> GetCollectionQualityAsync(
        string deviceId,
        CancellationToken ct = default)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var state = _states.GetOrAdd(
            normalizedDeviceId,
            id => EndpointState.CreateUnknown(id, InferPlatform(id)));

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

        return Task.FromResult(new EndpointCollectionQualityDto(
            state.DeviceId,
            state.Platform,
            state.UploadStatus,
            issueCount,
            DateTimeOffset.UtcNow));
    }

    public Task<EndpointNotificationActionResponse> HandleNotificationActionAsync(
        string deviceId,
        EndpointNotificationActionRequest request,
        CancellationToken ct = default)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var state = _states.GetOrAdd(
            normalizedDeviceId,
            id => EndpointState.CreateUnknown(id, InferPlatform(id)));

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            return Task.FromResult(new EndpointNotificationActionResponse(
                "Rejected",
                null,
                "Notification action is required."));
        }

        if (CanExecuteDirectly(request.RiskLevel))
        {
            return Task.FromResult(new EndpointNotificationActionResponse(
                "Executed",
                null,
                "Low-risk notification action executed online."));
        }

        state.OnlineOnlyBlockedCount++;
        return Task.FromResult(new EndpointNotificationActionResponse(
            "OpenDetailRequired",
            BuildDetailUrl(request),
            "High-risk notification action requires the Web confirmation detail."));
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

    private static EndpointStatusDto MapStatus(EndpointState state)
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

    private sealed class EndpointState
    {
        private EndpointState(
            string deviceId,
            string platform,
            string? appVersion,
            string uploadStatus,
            int collectionCacheCount,
            DateTimeOffset? lastHeartbeatAt)
        {
            DeviceId = deviceId;
            Platform = platform;
            AppVersion = appVersion;
            UploadStatus = uploadStatus;
            CollectionCacheCount = collectionCacheCount;
            LastHeartbeatAt = lastHeartbeatAt;
        }

        public string DeviceId { get; }
        public string Platform { get; private set; }
        public string? AppVersion { get; private set; }
        public string UploadStatus { get; private set; }
        public int CollectionCacheCount { get; private set; }
        public int OnlineOnlyBlockedCount { get; set; }
        public DateTimeOffset? LastHeartbeatAt { get; private set; }

        public static EndpointState CreateUnknown(string deviceId, string platform)
            => new(deviceId, platform, null, "Unknown", 0, null);

        public static EndpointState FromHeartbeat(
            string deviceId,
            EndpointHeartbeatRequest request,
            DateTimeOffset now)
            => new(
                deviceId,
                NormalizePlatform(request.Platform),
                request.AppVersion,
                NormalizeUploadStatus(request.UploadStatus),
                Math.Max(0, request.CollectionCacheCount ?? 0),
                now);

        public void ApplyHeartbeat(EndpointHeartbeatRequest request, DateTimeOffset now)
        {
            Platform = NormalizePlatform(request.Platform);
            AppVersion = request.AppVersion;
            UploadStatus = NormalizeUploadStatus(request.UploadStatus);
            CollectionCacheCount = Math.Max(0, request.CollectionCacheCount ?? 0);
            LastHeartbeatAt = now;
        }

        private static string NormalizeUploadStatus(string? uploadStatus)
            => string.IsNullOrWhiteSpace(uploadStatus) ? "Unknown" : uploadStatus.Trim();
    }
}
