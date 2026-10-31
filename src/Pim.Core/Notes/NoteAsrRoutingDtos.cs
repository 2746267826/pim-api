namespace Pim.Core.Notes;

public static class NoteAsrRoute
{
    public const string Local = "local";
    public const string CloudAsr = "cloud-asr";
    public const string CloudMultimodal = "cloud-multimodal";
    public static bool Known(string? value) => value is Local or CloudAsr or CloudMultimodal;
    public static bool IsCloud(string value) => value is CloudAsr or CloudMultimodal;
}

public static class NoteAsrExecutionMode
{
    public const string Realtime = "realtime";
    public const string Batch = "batch";
    public static bool Known(string? value) => value is Realtime or Batch;
}

public sealed record NoteAsrRouteConfigDto(Guid Revision, string DefaultRoute, string? LocalModelId,
    string? LocalModelRevision, bool CloudConfigured, string ConsentVersion, DateTimeOffset UpdatedAt,
    Guid? CloudModelId = null);

public sealed record UpdateNoteAsrRouteConfigRequest(Guid ExpectedRevision, string? DefaultRoute,
    string? LocalModelId, string? LocalModelRevision, Guid? CloudModelId, string? ConsentVersion);

/// <summary>A route choice is immutable once persisted; task null keeps the legacy single-destination behavior.</summary>
public sealed record NoteAsrRouteConsent(string Route, Guid ConfigRevision, string ConsentVersion, bool Confirmed,
    string ExecutionMode = NoteAsrExecutionMode.Realtime, bool BatchScopeConfirmed = false)
{
    public override string ToString() => $"NoteAsrRouteConsent {{ Route = {Route}, ConfigRevision = {ConfigRevision}, Confirmed = {Confirmed} }}";
}

public sealed record NoteAsrNodeDto(Guid NodeId, string NodeIdExternal, bool Enabled, string ModelId,
    string ModelRevision, bool ModelVerified, IReadOnlyList<string> Capabilities,
    DateTimeOffset LastHeartbeatAt, bool Claimable, DateTimeOffset CreatedAt,
    string? RuntimeId = null, string? RuntimeVersion = null, string? ResourceSnapshotJson = null,
    DateTimeOffset? SampledAt = null);

public sealed record RegisterNoteAsrNodeRequest(Guid NodeId, string NodeIdExternal, bool Enabled,
    string ModelId, string ModelRevision, bool ModelVerified, IReadOnlyList<string> Capabilities,
    string? RuntimeId = null, string? RuntimeVersion = null, string? ResourceSnapshotJson = null,
    DateTimeOffset? SampledAt = null);

public sealed record NoteAsrNodeHeartbeatRequest(Guid NodeId, string ModelId, string ModelRevision,
    bool ModelVerified, IReadOnlyList<string> Capabilities, string? RuntimeId = null,
    string? RuntimeVersion = null, string? ResourceSnapshotJson = null, DateTimeOffset? SampledAt = null);

public sealed record ClaimNoteAsrTaskRequest(Guid NodeId, string IdempotencyKey, int LeaseSeconds = 120);

public sealed record NoteAsrLeaseDto(Guid LeaseId, Guid LeaseToken, Guid AttemptId, long FencingToken,
    DateTimeOffset ExpiresAt, string Route, Guid OwnerId = default, DateTimeOffset ServerNow = default,
    string ServerOrigin = "", bool StopRequested = false);

public sealed record NoteAsrClaimDto(Guid? LeaseId, string Status, NoteAsrLeaseDto? Lease,
    NoteAsrExecutionDto? Execution = null);

public sealed record NoteAsrExecutionDto(Guid AttemptId, Guid OwnerId, Guid TaskId, Guid VersionId, Guid NoteId,
    string InputSha256, string Route, string Language, string ServerOrigin, Guid ConfigRevision, string ConsentVersion,
    string? LocalModelId, string? LocalModelRevision, int ExpectedSegments,
    IReadOnlyList<NoteAsrSegmentInputDto> Segments, DateTimeOffset ServerNow);

public sealed record NoteAsrSegmentInputDto(int Index, Guid RecordingId, int RecordingSegment, int ChunkIndex,
    int SourceFileIndex, string SourceSha256, long SourceOffsetSamples, long Samples, string ContentEndpoint);

public sealed record NoteAsrLeaseHeartbeatRequest(Guid LeaseId, Guid LeaseToken, long FencingToken);

public sealed record NoteAsrAckRequest(Guid LeaseId, Guid LeaseToken, long FencingToken,
    string Kind, string Result, int SegmentIndex, string? ProviderHandle, string? ErrorCode, string? ErrorMessage,
    string? ResultText = null, string? CoverageJson = null);

public sealed record NoteAsrAckDto(Guid LeaseId, string Status, bool Duplicate, long FencingToken);

public sealed record NoteAsrAttemptDetailDto(Guid AttemptId, Guid OwnerId, Guid TaskId, Guid NoteId,
    Guid VersionId, string InputSha256, string Route, string State, bool StopRequested, Guid ConfigRevision,
    string ConsentVersion, Guid? ModelId, Guid? ModelRevision, string? PresetId, string? PresetVersion,
    string? Model, string? Endpoint, string? PromptTemplateRevision, string? PromptRevision,
    string? ExecutionMode, string? ProviderHandle, string? LocalModelId,
    string? LocalModelRevision, string Language, long FencingToken, int ExpectedSegments,
    int CompletedSegments, int FailedSegments, string? LastErrorCode, string? LastErrorMessage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateNoteAsrAttemptRequest(Guid TaskId, Guid VersionId, int ExpectedSegments,
    string Language, NoteAsrRouteConsent? Route);
