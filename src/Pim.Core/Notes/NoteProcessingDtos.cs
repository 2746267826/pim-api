using System.Text.Json.Serialization;

namespace Pim.Core.Notes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteProcessingState
{
    WaitingForMaterial,
    PendingProcessing,
    Processing,
    PartiallyCompleted,
    Completed,
    NeedsAttention
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteProcessingChainState
{
    NotApplicable,
    WaitingForMaterial,
    WaitingForAsr,
    PendingStart,
    Queued,
    Running,
    Succeeded,
    NeedsAttention,
    Stopped
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteProcessingTaskKind
{
    Asr,
    Derived
}

public sealed record StartNoteProcessingRequest(Guid NoteId, Guid VersionId);

public sealed record NoteProcessingIssueDto(
    string Code,
    string Message,
    NoteProcessingTaskKind? Task = null);

public sealed record NoteProcessingChainDto(
    NoteProcessingTaskKind Kind,
    NoteProcessingChainState State,
    Guid? TaskId,
    bool TaskCreated,
    Guid? Revision,
    string? Phase,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    bool HasResult,
    IReadOnlyList<string> Errors,
    string? WaitingReason);

public sealed record NoteProcessingArtifactDto(
    Guid ArtifactId,
    NoteDerivedArtifactKind Kind,
    Guid? HeadVersionId);

public sealed record NoteProcessingStatusDto(
    Guid ProcessingId,
    Guid NoteId,
    Guid VersionId,
    NoteProcessingState State,
    bool AutoProcessingEnabled,
    bool AutoStarted,
    bool ManualStartAvailable,
    string Scope,
    bool IncrementalScopeSupported,
    string? PendingReason,
    NoteProcessingChainDto Asr,
    NoteProcessingChainDto Derived,
    IReadOnlyList<NoteProcessingArtifactDto> Artifacts,
    IReadOnlyList<NoteProcessingIssueDto> Issues,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
