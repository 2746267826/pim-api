namespace Pim.Infrastructure.Notes;

public sealed record NativeNoteDeletedItem(
    Guid NoteId,
    string Title,
    DateTimeOffset DeletedAt,
    Guid CurrentVersionId);

public sealed record NativeNotePurgeResult(
    int MaterialVersions,
    int AsrTasks,
    int DerivedTasks,
    int AiCalls,
    int AiImages,
    int AiAudio);

public sealed record NativeNoteRecycleResult(
    Guid NoteId,
    string Title,
    bool Changed,
    string Message);
