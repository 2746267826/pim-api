using System.ComponentModel.DataAnnotations;

namespace Pim.Core.Notes;

/// <summary>
/// User-visible receipt attached to every native-note MCP mutation. The caller-supplied
/// requestId is never echoed; <see cref="OperationId"/> is the stable external operation
/// identity. Replaying the same request returns the original receipt with
/// <see cref="Replayed"/> set instead of applying the mutation twice.
/// </summary>
public sealed record NativeNoteMcpReceiptDto(
    string OperationId,
    string Operation,
    Guid NoteId,
    Guid? Revision,
    string Status,
    bool Replayed,
    DateTimeOffset At,
    string Message,
    int? ErrorCode = null);

public sealed record NativeNoteMcpOperationResultDto<T>(
    NativeNoteMcpReceiptDto Receipt,
    T Result);

/// <summary>Structured failure payload so a caller can reconcile a requestId after an error.</summary>
public sealed record NativeNoteMcpOperationFailureDto(NativeNoteMcpReceiptDto Receipt);

/// <summary>
/// Result of looking up a caller-supplied requestId. Internal task, attempt, step, request,
/// log and receipt identifiers are never returned; <see cref="OperationId"/> is the stable
/// external operation identity.
/// </summary>
public sealed record NativeNoteMcpOperationStatusDto(
    string OperationId,
    string Operation,
    Guid NoteId,
    string State,
    NativeNoteMcpReceiptDto Receipt);

public sealed record NativeNoteMcpNoteSummaryDto(
    Guid NoteId,
    string Title,
    Guid? CurrentVersionId,
    Guid Revision,
    string Status,
    bool HasConflict,
    int VersionCount,
    int ConflictCount,
    DateTimeOffset UpdatedAt);

public sealed record NativeNoteMcpPageDto(
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<NativeNoteMcpNoteSummaryDto> Items);

/// <summary>Owner-scoped material snapshot used by the aggregate read tools.</summary>
public sealed record NativeNoteMcpSnapshotDto(
    Guid NoteId,
    string Title,
    string Status,
    Guid Revision,
    Guid? CurrentVersionId,
    bool HasConflict,
    int TotalVersions,
    int ConflictCount,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<NoteMaterialUploadDto> Versions,
    IReadOnlyList<NoteMaterialUploadDto> ConflictCandidates);

public sealed record NativeNoteMcpDetailDto(
    Guid NoteId,
    string Title,
    string Status,
    Guid Revision,
    Guid? CurrentVersionId,
    bool HasConflict,
    int TotalVersions,
    DateTimeOffset UpdatedAt,
    NoteMaterialUploadDto? Current,
    IReadOnlyList<NoteMaterialUploadDto> Versions,
    IReadOnlyList<NoteMaterialUploadDto> ConflictCandidates,
    NativeNoteMcpProcessingDto? Processing,
    IReadOnlyList<NativeNoteMcpResultArtifactDto> DerivedArtifacts,
    NativeNoteMcpReceiptDto Receipt);

public sealed record NativeNoteMcpVersionsDto(
    Guid NoteId,
    Guid Revision,
    Guid? CurrentVersionId,
    int Total,
    bool HasConflict,
    IReadOnlyList<NoteMaterialUploadDto> Versions,
    IReadOnlyList<NoteMaterialUploadDto> ConflictCandidates,
    NativeNoteMcpReceiptDto Receipt);

public sealed record NativeNoteMcpResultsDto(
    Guid NoteId,
    Guid Revision,
    NativeNoteMcpProcessingDto? Processing,
    IReadOnlyList<NativeNoteMcpResultArtifactDto> Artifacts,
    NativeNoteMcpReceiptDto Receipt);

public sealed record NativeNoteMcpPackageRequest(
    Guid RequestId,
    [property: Required] string PackageBase64,
    Guid? ExpectedCurrentVersionId,
    Guid? ExpectedRevision,
    bool Promote = true);

public sealed record NativeNoteMcpPackageUploadDto(
    NoteMaterialUploadDto Upload,
    Guid? CurrentVersionId,
    Guid Revision,
    bool HasConflict,
    IReadOnlyList<NoteMaterialUploadDto> ConflictCandidates);

public sealed record NativeNoteMcpPromoteRequest(
    Guid RequestId,
    Guid VersionId,
    Guid? ExpectedCurrentVersionId,
    Guid? ExpectedRevision);

public sealed record NativeNoteMcpVersionMutationDto(
    NoteMaterialUploadDto Upload,
    Guid? CurrentVersionId,
    Guid Revision,
    IReadOnlyList<NoteMaterialUploadDto> ConflictCandidates);

public sealed record NativeNoteMcpCancelUploadRequest(
    Guid RequestId,
    Guid VersionId,
    Guid? ExpectedRevision);

public sealed record NativeNoteMcpCancelUploadDto(
    Guid VersionId,
    bool Removed,
    Guid? Revision);

public sealed record NativeNoteMcpRecycleRequest(
    Guid RequestId,
    Guid? ExpectedRevision);

public sealed record NativeNoteMcpRecycleDto(
    string Status,
    Guid Revision,
    DateTimeOffset UpdatedAt);

public sealed record NativeNoteMcpProcessingStartRequest(
    Guid RequestId,
    Guid VersionId,
    Guid? ExpectedRevision);

public sealed record NativeNoteMcpProcessingRetryRequest(
    Guid RequestId,
    Guid? VersionId,
    [property: Required] IReadOnlyList<string> StepKeys,
    [property: Required] NoteDerivedGenerationConsent Consent,
    bool AcknowledgePossibleDuplicateCharge);

/// <summary>
/// External, owner-scoped processing view. Internal task/attempt/step/request/log identifiers
/// stay on the server; callers receive logical domain states and stable external identifiers.
/// </summary>
public sealed record NativeNoteMcpProcessingDto(
    Guid NoteId,
    Guid VersionId,
    string State,
    bool AutoProcessingEnabled,
    bool AutoStarted,
    bool ManualStartAvailable,
    bool RecycleStopped,
    bool ResumePending,
    string Scope,
    bool IncrementalScopeSupported,
    string? PendingReason,
    NativeNoteMcpProcessingChainDto Asr,
    NativeNoteMcpProcessingChainDto Organization,
    IReadOnlyList<NativeNoteMcpProcessingArtifactDto> Artifacts,
    IReadOnlyList<NativeNoteMcpIssueDto> Issues,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NativeNoteMcpProcessingChainDto(
    string Domain,
    string State,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    bool HasResult,
    IReadOnlyList<string> Errors,
    string? WaitingReason,
    IReadOnlyList<NativeNoteMcpProcessingItemDto> Items);

public sealed record NativeNoteMcpProcessingItemDto(string Key, string State, string? Error);

public sealed record NativeNoteMcpProcessingArtifactDto(
    Guid ArtifactId,
    string Kind,
    Guid? HeadVersionId);

public sealed record NativeNoteMcpIssueDto(
    string Code,
    string Message,
    string? Domain);

/// <summary>Logical derived-result view with no generation task, request or log identifiers.</summary>
public sealed record NativeNoteMcpResultArtifactDto(
    Guid ArtifactId,
    string Kind,
    Guid? HeadVersionId,
    bool HasConflict,
    IReadOnlyList<NativeNoteMcpResultVersionDto> Versions);

public sealed record NativeNoteMcpResultVersionDto(
    Guid VersionId,
    string Author,
    Guid? ParentVersionId,
    Guid SourceVersionId,
    string ContentSha256,
    bool HasConflict,
    DateTimeOffset CreatedAt,
    bool IsHead);

public sealed record NativeNoteMcpRetryDto(NativeNoteMcpProcessingDto Processing);

public sealed record NativeNoteMcpAdoptRequest(
    Guid RequestId,
    Guid VersionId,
    Guid ExpectedHeadRevision);

public sealed record NativeNoteMcpAdoptDto(
    NativeNoteMcpResultArtifactDto Artifact,
    Guid VersionId,
    bool HasConflict);

public sealed record NativeNoteMcpSuggestionConfirmRequest(
    Guid RequestId,
    Guid ExpectedRevision);

public sealed record NativeNoteMcpSuggestionDismissRequest(
    Guid RequestId,
    Guid ExpectedRevision);

public sealed record NativeNoteMcpSuggestionExtractionRequest(
    Guid RequestId,
    Guid ArtifactId,
    Guid VersionId,
    [property: Required, MaxLength(64)] string ExpectedContentSha256,
    [property: Required] NoteDerivedGenerationConsent Consent,
    bool AcknowledgePossibleCharge);

/// <summary>External suggestion views. Extraction/request/source-task identifiers stay internal.</summary>
public sealed record NativeNoteMcpSuggestionDto(
    Guid Id,
    int Ordinal,
    string Title,
    string Kind,
    string? Description,
    int Priority,
    DateTimeOffset? Due,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? TimeZoneId,
    Guid? TaskBookId,
    Guid? CalendarId,
    string Status,
    Guid Revision,
    Guid? TaskId,
    Guid? EventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DismissedAt);

public sealed record NativeNoteMcpSuggestionListDto(
    int Total,
    IReadOnlyList<NativeNoteMcpSuggestionDto> Items);

public sealed record NativeNoteMcpSuggestionExtractionDto(
    string Status,
    int AttemptCount,
    int SuggestionCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
