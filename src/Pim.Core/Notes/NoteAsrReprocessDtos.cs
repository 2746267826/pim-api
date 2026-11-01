using Pim.Core.Ai;

namespace Pim.Core.Notes;

public static class NoteAsrReprocessReasonKind
{
    public const string Manual = "manual";
    public const string Quality = "quality";
    public static bool Known(string? value) => value is Manual or Quality;
}

public sealed record NoteAsrReprocessReason(string Kind, string? Text = null);

/// <summary>
/// A new full-task execution request. Route remains nullable so omitted input reuses the source
/// task's route, but the caller must still confirm the current route-config revision.
/// </summary>
public sealed record NoteAsrReprocessRoute(
    string? Route,
    Guid ExpectedConfigRevision,
    string ExpectedConsentVersion,
    string? ExecutionMode = null,
    bool Confirmed = false,
    bool BatchScopeConfirmed = false);

public sealed record ReprocessNoteAsrRequest(
    Guid ClientTaskId,
    Guid ExpectedUserId,
    Guid ExpectedRevision,
    NoteAsrReprocessReason Reason,
    NoteAsrReprocessRoute Route,
    /// <summary>Required only for a cloud route; local-only reprocessing must omit it.</summary>
    AiAsrDestinationConsent? Consent = null);

public sealed record NoteAsrReprocessResultDto(NoteAsrTaskDto Task, Guid ParentTaskId, string Reason);

public sealed record NoteAsrReprocessHistoryItemDto(
    Guid ParentTaskId,
    Guid TaskId,
    Guid Revision,
    Guid VersionId,
    string Reason,
    string Phase,
    string Route,
    string ExecutionMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NoteAsrReprocessHistoryDto(
    Guid ParentTaskId,
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<NoteAsrReprocessHistoryItemDto> Items);
