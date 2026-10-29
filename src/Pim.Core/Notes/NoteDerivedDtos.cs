using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Pim.Core.Notes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteDerivedArtifactKind
{
    CompleteRecord,
    IndependentAnalysis
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteDerivedVersionAuthor
{
    Model,
    User
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteDerivedStepKind
{
    ImageEvidence,
    RecordSynthesis,
    AnalysisSynthesis
}

public sealed record NoteDerivedGenerationConsent(
    Guid ModelId,
    Guid ModelRevision,
    string Endpoint,
    bool Confirmed);

public sealed record StartNoteDerivedTaskRequest(
    Guid TaskId,
    Guid ExpectedUserId,
    Guid VersionId,
    Guid? AsrTaskId,
    NoteDerivedGenerationConsent Consent);

public sealed record RetryNoteDerivedTaskRequest(
    Guid RetryId,
    Guid ExpectedUserId,
    Guid ExpectedRevision,
    IReadOnlyList<string> StepKeys,
    NoteDerivedGenerationConsent Consent,
    bool AcknowledgePossibleDuplicateCharge);

public sealed record NoteDerivedLimitsDto(
    int MaxImages,
    int MaxImagesPerVisionCall,
    int MaxImageBytes,
    int MaxVisionCallBytes,
    int MaxVisionCalls,
    int MaxEvidenceCharsPerStep,
    int MaxEvidenceSteps,
    int MaxSynthesisInputChars,
    int MaxDerivedContentCharacters);

public sealed record NoteDerivedIssueDto(
    string Code,
    string Message,
    string? SourcePath = null,
    string? BlockId = null,
    string? StepKey = null);

public sealed record NoteDerivedCoverageDto(
    int TextBlocks,
    int TranscriptSegments,
    int CompletedTranscriptSegments,
    int Photos,
    int CompletedPhotos,
    int InkPreviews,
    int CompletedInkPreviews,
    int VisionBatches,
    int CompletedVisionBatches,
    bool Complete,
    IReadOnlyList<NoteDerivedIssueDto> Issues);

public sealed record NoteDerivedArtifactRefDto(
    Guid ArtifactId,
    NoteDerivedArtifactKind Kind,
    Guid? HeadVersionId);

public sealed record NoteDerivedAttemptDto(
    Guid RequestId,
    Guid? LogId,
    string Status,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record NoteDerivedStepDto(
    string Key,
    NoteDerivedStepKind Kind,
    string Status,
    IReadOnlyList<NoteDerivedAttemptDto> Attempts);

public sealed record NoteDerivedStepsDto(
    int Total,
    int Offset,
    IReadOnlyList<NoteDerivedStepDto> Items);

public sealed record NoteDerivedTaskDto(
    Guid TaskId,
    Guid Revision,
    Guid NoteId,
    Guid SourceVersionId,
    Guid? AsrTaskId,
    Guid? AsrRevision,
    string InputSha256,
    string ManifestSha256,
    string Phase,
    NoteDerivedGenerationConsent Destination,
    NoteDerivedCoverageDto Coverage,
    IReadOnlyList<NoteDerivedArtifactRefDto> Artifacts,
    IReadOnlyList<NoteDerivedIssueDto> Issues,
    IReadOnlyList<NoteDerivedStepDto> Steps,
    string PlanVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool StopRequested,
    bool BackgroundProcessingAvailable = false);

public sealed record NoteDerivedVersionSummaryDto(
    Guid VersionId,
    NoteDerivedVersionAuthor Author,
    Guid SourceVersionId,
    Guid? ParentVersionId,
    Guid? SourceCompleteRecordVersionId,
    Guid? GenerationTaskId,
    string ContentSha256,
    bool HasConflict,
    DateTimeOffset CreatedAt,
    bool IsHead);

public sealed record NoteDerivedArtifactDto(
    Guid ArtifactId,
    Guid NoteId,
    NoteDerivedArtifactKind Kind,
    Guid Revision,
    Guid? HeadVersionId,
    IReadOnlyList<NoteDerivedVersionSummaryDto> Versions);

public sealed record NoteDerivedVersionDto(
    Guid VersionId,
    Guid ArtifactId,
    NoteDerivedArtifactKind Kind,
    NoteDerivedVersionAuthor Author,
    Guid? ParentVersionId,
    Guid? ObservedHeadVersionId,
    Guid? SourceCompleteRecordVersionId,
    Guid SourceVersionId,
    Guid? GenerationTaskId,
    string ContentMarkdown,
    string ContentSha256,
    string InputSha256,
    string ManifestSha256,
    string PlanVersion,
    string PromptVersion,
    Guid? AiCallLogId,
    Guid? ModelProfileId,
    Guid? ModelRevision,
    string? ModelEndpoint,
    NoteDerivedCoverageDto? Coverage,
    bool HasConflict,
    DateTimeOffset CreatedAt,
    bool IsHead);

public sealed record NoteDerivedMutationDto(
    NoteDerivedArtifactDto Artifact,
    Guid VersionId,
    bool HasConflict,
    bool IsHead);

public sealed record SaveNoteDerivedManualRevisionRequest(
    Guid RevisionId,
    Guid ExpectedHeadRevision,
    Guid? ExpectedHeadVersionId,
    [property: Required, MaxLength(1_000_000)] string ContentMarkdown);

public sealed record SelectNoteDerivedVersionRequest(
    Guid ExpectedHeadRevision,
    Guid VersionId);

public sealed record NoteDerivedDestinationDto(
    Guid ModelId,
    Guid ModelRevision,
    string DisplayName,
    string Model,
    string Endpoint,
    IReadOnlyList<string> Capabilities,
    bool BackgroundProcessingAvailable);

public sealed record NoteDerivedDestinationsDto(
    int Total,
    IReadOnlyList<NoteDerivedDestinationDto> Items);
