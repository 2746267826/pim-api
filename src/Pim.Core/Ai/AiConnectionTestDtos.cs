using System.Text.Json.Serialization;

namespace Pim.Core.Ai;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiConnectionTestStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Blocked,
    CapabilityMismatch,
    Running,
    Cancelled,
    Unknown
}

/// <summary>
/// Tests an already-saved model revision. It never accepts a draft credential or endpoint, so the
/// admin UI cannot exercise a configuration that was not persisted and audited first.
/// </summary>
public sealed record AiConnectionTestRequest(Guid ClientRequestId, Guid ExpectedRevision,
    AiModelCapability Capability);

/// <summary>
/// Leak-free result: no endpoint credential, raw provider body, request text or Authorization
/// header is exposed. <see cref="ResponsePreview"/> is already redacted and length-bounded.
/// </summary>
public sealed record AiConnectionTestResultDto(
    Guid ClientRequestId,
    Guid ModelId,
    Guid ModelRevision,
    AiModelCapability Capability,
    AiConnectionTestStatus Status,
    string Probe,
    string? ResponsePreview,
    string? ErrorCode,
    string? ErrorMessage,
    Guid LogId,
    long DurationMs,
    bool Replayed,
    DateTimeOffset TestedAt,
    bool ProviderAttempted);

/// <summary>
/// Latest evidence per saved model revision and capability. The UI can show a stale revision
/// explicitly instead of presenting an old pass as proof for newly edited settings.
/// </summary>
public sealed record AiConnectionTestSummaryDto(
    Guid ModelId,
    Guid ModelRevision,
    AiModelCapability Capability,
    AiConnectionTestStatus Status,
    DateTimeOffset TestedAt,
    bool ProviderAttempted,
    Guid LogId);
