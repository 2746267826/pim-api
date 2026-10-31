using Pim.Core.Ai;

namespace Pim.Core.Notes;

public sealed record StartNoteAsrRequest(Guid TaskId, Guid ExpectedUserId, Guid VersionId,
    AiAsrDestinationConsent? Consent, string Language = "auto", NoteAsrRouteConsent? Route = null,
    Guid? ParentTaskId = null, string? ReprocessReason = null);

public sealed record RetryNoteAsrRequest(Guid RetryId, Guid ExpectedUserId, Guid ExpectedRevision,
    IReadOnlyList<int> SegmentIndexes, AiAsrDestinationConsent? Consent, bool AcknowledgePossibleDuplicateCharge);

public sealed record NoteAsrTaskDto(Guid TaskId, Guid Revision, Guid NoteId, Guid VersionId,
    string InputSha256, string ManifestSha256, string Phase, string Language,
    AiAsrDestinationConsent? Destination, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int TotalSegments, int CompletedSegments, int FailedSegments, long TotalSamples, long CompletedSamples,
    int SampleRate, bool StopRequested, bool BackgroundProcessingAvailable = false,
    string? Route = null, Guid? AttemptId = null, Guid RouteConfigRevision = default,
    string RouteConsentVersion = "", string? ExecutionMode = null);

public sealed record NoteAsrAttemptDto(Guid RequestId, Guid? LogId, string Status, string? ErrorMessage,
    Guid? EvidenceId = null, string? ReceiptKind = null, string? ProviderHandle = null);

public sealed record NoteAsrSegmentDto(int Index, Guid RecordingId, int RecordingSegment, int ChunkIndex,
    int SourceFileIndex, string SourcePath, string SourceSha256, long SourceOffsetSamples,
    long RecordingOffsetSamples, long TimelineOffsetSamples, long Samples, string Status,
    string? Transcript, IReadOnlyList<NoteAsrAttemptDto> Attempts, Guid? EvidenceId = null,
    string? ReceiptKind = null, string? ProviderHandle = null, string? CoverageJson = null);

public sealed record NoteAsrSegmentsDto(int Total, int Offset, IReadOnlyList<NoteAsrSegmentDto> Items);

public sealed record NoteAsrSourceDto(Guid NoteId, Guid VersionId, string Title, DateTimeOffset CreatedAt,
    bool IsCurrent, bool HasConflict, int Recordings, long TotalSamples, int SampleRate);
public sealed record NoteAsrSourcesDto(int Total, int Offset, IReadOnlyList<NoteAsrSourceDto> Items);

public sealed record NoteAsrDestinationDto(Guid ModelId, Guid ModelRevision, string DisplayName, string Model,
    string Endpoint, string PresetId, string PresetVersion, IReadOnlyList<string> Languages,
    bool BackgroundProcessingAvailable);

public sealed record NoteAsrDestinationsDto(int Total, IReadOnlyList<NoteAsrDestinationDto> Items);
