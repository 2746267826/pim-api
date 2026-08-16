namespace Pim.Core.Operations;

public sealed record DaemonHeartbeatRequest(
    string DeviceId,
    string DaemonKind,
    string Version,
    string ServerUrl,
    DateTimeOffset? LastSuccessfulUploadAt,
    DateTimeOffset? LastAttemptedUploadAt,
    string? LastError,
    int? UploadQueueCount,
    DaemonSourceState ActivityWatchState,
    DaemonSourceState KeyStatsState,
    bool CollectionPaused,
    string StatusJson);

public sealed record DaemonHeartbeatDto(
    string DeviceId,
    string DaemonKind,
    string Version,
    string ServerUrl,
    DateTimeOffset? LastSuccessfulUploadAt,
    DateTimeOffset? LastAttemptedUploadAt,
    string? LastError,
    int? UploadQueueCount,
    DaemonSourceState ActivityWatchState,
    DaemonSourceState KeyStatsState,
    bool CollectionPaused,
    string StatusJson,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? PlannedOfflineAt,
    string? OfflineReason);

public sealed record PlannedOfflineRequest(
    string DeviceId,
    string DaemonKind,
    string? Reason,
    DateTimeOffset? OccurredAt);

public interface IDaemonHeartbeatService
{
    Task<DaemonHeartbeatDto> UpsertAsync(DaemonHeartbeatRequest request, CancellationToken ct = default);
    Task<DaemonHeartbeatDto> RecordPlannedOfflineAsync(PlannedOfflineRequest request, CancellationToken ct = default);
    Task<DaemonHeartbeatDto?> GetLatestAsync(string deviceId, CancellationToken ct = default);
    Task<DaemonHeartbeatDto?> GetLatestWindowsAsync(CancellationToken ct = default);
}
