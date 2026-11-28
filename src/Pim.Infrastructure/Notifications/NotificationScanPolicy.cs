using Pim.Infrastructure.Notes;

namespace Pim.Infrastructure.Notifications;

/// <summary>
/// Maps durable note task state to the notification candidate scanned by the dispatcher.
/// Kept separate from file/database IO so the Q06 category decisions can be regression-tested
/// without constructing a full note processing pipeline.
/// </summary>
internal static class NotificationScanPolicy
{
    public static NotificationEventDraft? ForTranscription(
        NoteAsrState state,
        DateTimeOffset watermark)
    {
        if (state.UpdatedAt <= watermark) return null;
        var phase = NoteAsrTaskStore.Describe(state).Phase;
        return phase switch
        {
            "Succeeded" => NotificationEventDrafts.NoteTranscriptionCompleted(
                state.Request.TaskId, state.NoteId, state.Request.VersionId),
            "NeedsAttention" => NotificationEventDrafts.NoteTranscriptionAttention(
                state.Request.TaskId, state.NoteId, state.Revision),
            _ => null
        };
    }

    public static NotificationEventDraft? ForDerived(
        NoteDerivedTaskState state,
        DateTimeOffset watermark)
    {
        if (state.UpdatedAt <= watermark) return null;
        var phase = NoteDerivedStore.DescribePhase(state);
        return phase switch
        {
            "Succeeded" => NotificationEventDrafts.NoteDerivedCompleted(
                state.Request.TaskId, state.NoteId, state.Request.VersionId),
            "NeedsAttention" => NotificationEventDrafts.NoteDerivedAttention(
                state.Request.TaskId, state.NoteId, state.Revision),
            _ => null
        };
    }
}
