namespace Pim.Core.Notes;

public sealed class NoteAsrBatchOptions
{
    /// <summary>MiMo Batch audio support is not verified; submission stays blocked unless explicitly enabled.</summary>
    public bool AllowUnverifiedAudioBatch { get; set; }
    public int MaxJsonlBytes { get; set; } = 128 * 1024 * 1024;
}

public sealed record NoteAsrBatchConsent(Guid ModelId, Guid ModelRevision, string ConsentRef,
    bool Confirmed, bool BatchScopeConfirmed);

public sealed record StartNoteAsrBatchRequest(Guid TaskId, Guid? AttemptId, Guid ModelId,
    string ConsentRef, bool BatchScopeConfirmed, string CompletionWindow = "24h");

public sealed record NoteAsrBatchJobDto(Guid Id, Guid TaskId, Guid? AttemptId, string Status, string? StatusRaw,
    string? BatchId, string? InputFileId, string? OutputFileId, string? ErrorFileId,
    DateTimeOffset? InputExpireAt, DateTimeOffset? BatchExpiresAt, string Model, string BaseUrl,
    string ConsentRef, string CompletionWindow, int? TotalItems, int? CompletedItems, int? FailedItems,
    string? ResultJson, bool OutputImported, string? LastErrorCode, string? LastErrorMessage,
    DateTimeOffset? LastPolledAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string AudioCapability = "NOT-VERIFIED");
