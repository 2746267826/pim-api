namespace Pim.Core.Notes;

/// <summary>
/// Submits a task to MiMo Batch using only trusted server-side records. When <c>ClientTaskId</c> equals
/// <c>SourceTaskId</c> the already prepared batch-mode task is submitted as-is (the ordinary first
/// submission). A different <c>ClientTaskId</c> means an explicit full reprocess that creates a child
/// task, which is only valid for a terminal source. The client never supplies audio or JSONL.
/// </summary>
public sealed record StartNoteAsrBatchFromTaskRequest(
    Guid ClientTaskId,
    Guid SourceTaskId,
    Guid ExpectedSourceRevision,
    Guid ExpectedUserId,
    NoteAsrReprocessRoute Route,
    NoteAsrBatchTarget Target,
    NoteAsrReprocessReason Reason);

/// <summary>
/// Provider target frozen into the new consent snapshot. This shape is compatible with the
/// provider's OpenAI-compatible file upload and batch creation calls.
/// </summary>
public sealed record NoteAsrBatchTarget(
    Guid ModelId,
    Guid ModelRevision,
    string ConsentRef,
    bool Confirmed,
    bool BatchScopeConfirmed,
    string CompletionWindow = "24h");

public sealed record NoteAsrBatchSubmissionResultDto(
    NoteAsrBatchJobDto Job,
    NoteAsrTaskDto Task,
    Guid SourceTaskId,
    string RetryScope);

/// <summary>
/// Submits only the original failed/missing segments to a new child attempt. Successful segments
/// are inherited as trusted child evidence and are never re-sent to the provider.
/// </summary>
public sealed record RetryFailedNoteAsrBatchRequest(
    Guid ClientTaskId,
    Guid SourceJobId,
    Guid ExpectedUserId,
    NoteAsrReprocessRoute Route,
    NoteAsrBatchTarget Target,
    NoteAsrReprocessReason Reason);
