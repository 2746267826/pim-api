using System.Text.Json.Serialization;

namespace Pim.Module.PcTracker.DTOs;

public record TrackerEventDto(
    string Timestamp,
    double Duration,
    string EventType,
    string? ExePath,
    string? AppName,
    string? DisplayName,
    string? WindowTitle,
    string? CommandLine,
    bool IsIdle,
    bool IsMediaActive,
    string? Url,
    string? Domain,
    string? PagePath,
    bool? Audible,
    bool? Incognito,
    int? TabCount,
    int PageVisitCount,
    double PageVisitDuration,
    object? RawJson,
    string Date,
    string? Browser = null,
    string? InstanceId = null
);

public record TrackerEventsUploadRequest(
    string DeviceId,
    List<TrackerEventDto> Events
);

public record TrackerHealthRequest(
    string DeviceId,
    string Status,
    double UptimeSeconds,
    bool HookActive,
    long PollCount,
    long SessionsCreated,
    long EventsUploaded,
    long UploadFailures,
    string? LastError,
    bool BrowserConnected,
    double? BrowserHeartbeatAgeSeconds
);

public record BrowserHeartbeatDto(
    string Url,
    string Title,
    bool Audible,
    bool Incognito,
    int TabCount,
    string Timestamp,
    string? Browser = null,
    string? InstanceId = null
);
