using System.ComponentModel.DataAnnotations;

namespace Pim.Core.Notes;

/// <summary>
/// User-visible receipt attached to every native-note MCP mutation. <see cref="RequestId"/>
/// is the caller-supplied idempotency key; replaying the same request returns the original
/// receipt with <see cref="Replayed"/> set instead of applying the mutation twice.
/// </summary>
public sealed record NativeNoteMcpReceiptDto(
    Guid? RequestId,
    string Operation,
    Guid NoteId,
    Guid? Revision,
    string Status,
    bool Replayed,
    DateTimeOffset At,
    string Message);

public sealed record NativeNoteMcpOperationResultDto<T>(
    NativeNoteMcpReceiptDto Receipt,
    T Result);

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
    NoteProcessingStatusDto? Processing,
    IReadOnlyList<NoteDerivedTaskDto> DerivedTasks,
    IReadOnlyList<NoteDerivedArtifactDto> DerivedArtifacts,
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
    IReadOnlyList<NoteDerivedTaskDto> Tasks,
    IReadOnlyList<NoteDerivedArtifactDto> Artifacts,
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

public sealed record NativeNoteMcpLifecycleRequest(
    Guid RequestId,
    Guid? ExpectedRevision);

public sealed record NativeNoteMcpLifecycleDto(
    string Status,
    Guid Revision,
    DateTimeOffset UpdatedAt);

public sealed record NativeNoteMcpProcessingStartRequest(
    Guid RequestId,
    Guid VersionId,
    Guid? ExpectedRevision);

public sealed record NativeNoteMcpProcessingRetryRequest(
    Guid RequestId,
    Guid TaskId,
    Guid RetryId,
    Guid ExpectedRevision,
    [property: Required] IReadOnlyList<string> StepKeys,
    [property: Required] NoteDerivedGenerationConsent Consent,
    bool AcknowledgePossibleDuplicateCharge);

public sealed record NativeNoteMcpProcessingDto(NoteProcessingStatusDto Status);

public sealed record NativeNoteMcpRetryDto(NoteDerivedTaskDto Task);

public sealed record NativeNoteMcpAdoptRequest(
    Guid RequestId,
    Guid VersionId,
    Guid ExpectedHeadRevision);

public sealed record NativeNoteMcpAdoptDto(
    NoteDerivedArtifactDto Artifact,
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
