namespace Pim.Module.Mobile.DTOs;

public static class DiagnosticPlatforms
{
    public const string Android = "android";
    public const string Windows = "windows";
    public const string Web = "web";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Android, Windows, Web],
        StringComparer.Ordinal);
}

public static class DiagnosticEventKinds
{
    public const string Crash = "crash";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Manual = "manual";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Crash, Error, Warning, Manual],
        StringComparer.Ordinal);
}

public static class DiagnosticSeverities
{
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Fatal = "fatal";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Warning, Error, Fatal],
        StringComparer.Ordinal);
}

public static class DiagnosticLimits
{
    public const int MaxEventsPerBatch = 50;
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxEventsPerUserPerUtcDay = 1_000;
    public const int MaxEventsInOverview = 200;
    public const int MaxEventsInExport = 2_000;
    public const int MaxExportDays = 30;
    public const int MaxExportBytes = 8 * 1024 * 1024;
    public const int MaxMessageLength = 2_000;
    public const int MaxStackTraceLength = 12_000;
    public const int MaxAttributes = 16;
    public const int MaxAttributeKeyLength = 64;
    public const int MaxAttributeValueLength = 512;
}

public sealed record DiagnosticEventUploadRequest(
    string DeviceId,
    string Platform,
    string? ClientBatchId,
    IReadOnlyList<DiagnosticEventUploadItem> Events);

public sealed record DiagnosticEventUploadItem(
    string ClientEventId,
    string Kind,
    string Severity,
    DateTimeOffset OccurredAtUtc,
    string AppVersion,
    string OsVersion,
    string Message,
    string? StackTrace = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record DiagnosticUploadItemResult(
    string ClientEventId,
    string Outcome,
    string Code);

public sealed record DiagnosticUploadResult(
    int AcceptedCount,
    int SkippedCount,
    int RejectedCount,
    IReadOnlyList<DiagnosticUploadItemResult> Items);

public sealed record DiagnosticEventDto(
    Guid Id,
    string DeviceId,
    string Platform,
    string Kind,
    string Severity,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    string AppVersion,
    string OsVersion,
    string Message);

public sealed record DiagnosticDeviceSummaryDto(
    string DeviceId,
    string Platform,
    int EventCount,
    DateTimeOffset LastEventAtUtc);

public sealed record DiagnosticOverviewDto(
    int TotalCount,
    int Last24HoursCount,
    IReadOnlyList<DiagnosticDeviceSummaryDto> Devices,
    IReadOnlyList<DiagnosticEventDto> Items);

public sealed record DiagnosticExportEventDto(
    Guid Id,
    string DeviceId,
    string Platform,
    string Kind,
    string Severity,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    string AppVersion,
    string OsVersion,
    string Message,
    string? StackTrace,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record DiagnosticExportBundle(byte[] Content, string FileName);
