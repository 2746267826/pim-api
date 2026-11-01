using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.NoteTaskSuggestions.Entities;

public static class NoteTaskSuggestionStatuses
{
    public const string Draft = "Draft";
    public const string Confirmed = "Confirmed";
    public const string Dismissed = "Dismissed";

    public static readonly string[] All = [Draft, Confirmed, Dismissed];

    public static bool IsValid(string? value) => value is Draft or Confirmed or Dismissed;
}

public static class NoteTaskSuggestionExtractionStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";

    public static readonly string[] All = [Queued, Running, Succeeded, Failed];
}

[Table("note_task_suggestion_extractions")]
public sealed class NoteTaskSuggestionExtractionEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("request_id")] public Guid RequestId { get; set; }
    [Column("request_fingerprint"), MaxLength(64)] public string RequestFingerprint { get; set; } = "";
    [Column("note_id")] public Guid NoteId { get; set; }
    [Column("artifact_id")] public Guid ArtifactId { get; set; }
    [Column("artifact_version_id")] public Guid ArtifactVersionId { get; set; }
    [Column("source_content_sha256"), MaxLength(64)] public string SourceContentSha256 { get; set; } = "";
    [Column("source_input_sha256"), MaxLength(64)] public string SourceInputSha256 { get; set; } = "";
    [Column("source_manifest_sha256"), MaxLength(64)] public string SourceManifestSha256 { get; set; } = "";
    [Column("source_generation_task_id")] public Guid? SourceGenerationTaskId { get; set; }
    [Column("source_content_markdown")] public string SourceContentMarkdown { get; set; } = "";
    [Column("model_profile_id")] public Guid ModelProfileId { get; set; }
    [Column("model_revision")] public Guid ModelRevision { get; set; }
    [Column("model_endpoint"), MaxLength(2048)] public string ModelEndpoint { get; set; } = "";
    [Column("prompt_version"), MaxLength(64)] public string PromptVersion { get; set; } = "";
    [Column("status"), MaxLength(32)] public string Status { get; set; } = NoteTaskSuggestionExtractionStatuses.Queued;
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("attempt_count")] public int AttemptCount { get; set; }
    [Column("suggestion_count")] public int SuggestionCount { get; set; }
    [Column("ai_call_log_id")] public Guid? AiCallLogId { get; set; }
    [Column("error_code"), MaxLength(80)] public string? ErrorCode { get; set; }
    [Column("error_message"), MaxLength(2000)] public string? ErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("started_at")] public DateTimeOffset? StartedAt { get; set; }
    [Column("finished_at")] public DateTimeOffset? FinishedAt { get; set; }
}

[Table("note_task_suggestions")]
public sealed class NoteTaskSuggestionEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("extraction_id")] public Guid ExtractionId { get; set; }
    [Column("ordinal")] public int Ordinal { get; set; }
    [Column("note_id")] public Guid NoteId { get; set; }
    [Column("artifact_id")] public Guid ArtifactId { get; set; }
    [Column("artifact_version_id")] public Guid ArtifactVersionId { get; set; }
    [Column("source_content_sha256"), MaxLength(64)] public string SourceContentSha256 { get; set; } = "";
    [Column("source_input_sha256"), MaxLength(64)] public string SourceInputSha256 { get; set; } = "";
    [Column("source_manifest_sha256"), MaxLength(64)] public string SourceManifestSha256 { get; set; } = "";
    [Column("source_generation_task_id")] public Guid? SourceGenerationTaskId { get; set; }
    [Column("title"), MaxLength(255)] public string Title { get; set; } = "";
    [Column("description"), MaxLength(20000)] public string? Description { get; set; }
    [Column("priority")] public int Priority { get; set; }
    [Column("due")] public DateTimeOffset? Due { get; set; }
    [Column("task_book_id")] public Guid? TaskBookId { get; set; }
    [Column("calendar_id")] public Guid? CalendarId { get; set; }
    [Column("status"), MaxLength(32)] public string Status { get; set; } = NoteTaskSuggestionStatuses.Draft;
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("confirm_request_id")] public Guid? ConfirmRequestId { get; set; }
    [Column("task_id")] public Guid? TaskId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("confirmed_at")] public DateTimeOffset? ConfirmedAt { get; set; }
    [Column("dismissed_at")] public DateTimeOffset? DismissedAt { get; set; }

    [ForeignKey(nameof(ExtractionId))]
    public NoteTaskSuggestionExtractionEntity? Extraction { get; set; }
}
