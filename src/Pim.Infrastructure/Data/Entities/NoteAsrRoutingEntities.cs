using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("note_asr_route_configs")]
public sealed class NoteAsrRouteConfigEntity
{
    [Key, Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("default_route"), MaxLength(32)] public string DefaultRoute { get; set; } = "local";
    [Column("local_model_id"), MaxLength(200)] public string? LocalModelId { get; set; }
    [Column("local_model_revision"), MaxLength(128)] public string? LocalModelRevision { get; set; }
    [Column("cloud_model_id")] public Guid? CloudModelId { get; set; }
    [Column("consent_version"), MaxLength(64)] public string ConsentVersion { get; set; } = "";
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("note_asr_nodes")]
public sealed class NoteAsrNodeEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("node_id"), MaxLength(128)] public string NodeId { get; set; } = "";
    [Column("enabled")] public bool Enabled { get; set; }
    [Column("model_id"), MaxLength(200)] public string ModelId { get; set; } = "";
    [Column("model_revision"), MaxLength(128)] public string ModelRevision { get; set; } = "";
    [Column("model_verified")] public bool ModelVerified { get; set; }
    [Column("capabilities_json")] public string CapabilitiesJson { get; set; } = "[]";
    [Column("runtime_id"), MaxLength(128)] public string? RuntimeId { get; set; }
    [Column("runtime_version"), MaxLength(128)] public string? RuntimeVersion { get; set; }
    [Column("resource_snapshot_json")] public string ResourceSnapshotJson { get; set; } = "{}";
    [Column("sampled_at")] public DateTimeOffset? SampledAt { get; set; }
    [Column("last_heartbeat_at")] public DateTimeOffset LastHeartbeatAt { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("note_asr_attempts")]
public sealed class NoteAsrAttemptEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("task_id")] public Guid TaskId { get; set; }
    [Column("note_id")] public Guid NoteId { get; set; }
    [Column("version_id")] public Guid VersionId { get; set; }
    [Column("input_sha256"), MaxLength(64)] public string InputSha256 { get; set; } = "";
    [Column("route"), MaxLength(32)] public string Route { get; set; } = "local";
    [Column("state"), MaxLength(32)] public string State { get; set; } = "Queued";
    [Column("stop_requested")] public bool StopRequested { get; set; }
    [Column("config_revision")] public Guid ConfigRevision { get; set; }
    [Column("consent_version"), MaxLength(64)] public string ConsentVersion { get; set; } = "";
    [Column("model_id")] public Guid? ModelId { get; set; }
    [Column("model_revision")] public Guid? ModelRevision { get; set; }
    [Column("preset_id"), MaxLength(64)] public string? PresetId { get; set; }
    [Column("preset_version"), MaxLength(64)] public string? PresetVersion { get; set; }
    [Column("model"), MaxLength(128)] public string? Model { get; set; }
    [Column("endpoint"), MaxLength(2048)] public string? Endpoint { get; set; }
    [Column("prompt_template"), MaxLength(4000)] public string? PromptTemplate { get; set; }
    [Column("prompt_template_sha256"), MaxLength(64)] public string? PromptTemplateSha256 { get; set; }
    [Column("prompt_template_revision"), MaxLength(64)] public string? PromptTemplateRevision { get; set; }
    [Column("prompt_rendered"), MaxLength(4200)] public string? PromptRendered { get; set; }
    [Column("prompt_sha256"), MaxLength(64)] public string? PromptSha256 { get; set; }
    [Column("prompt_revision"), MaxLength(64)] public string? PromptRevision { get; set; }
    [Column("execution_mode"), MaxLength(32)] public string? ExecutionMode { get; set; }
    [Column("provider_handle"), MaxLength(256)] public string? ProviderHandle { get; set; }
    [Column("local_model_id"), MaxLength(200)] public string? LocalModelId { get; set; }
    [Column("local_model_revision"), MaxLength(128)] public string? LocalModelRevision { get; set; }
    [Column("language"), MaxLength(32)] public string Language { get; set; } = "auto";
    [Column("fencing_token")] public long FencingToken { get; set; }
    [Column("expected_segments")] public int ExpectedSegments { get; set; }
    [Column("completed_segments")] public int CompletedSegments { get; set; }
    [Column("failed_segments")] public int FailedSegments { get; set; }
    [Column("last_error_code"), MaxLength(64)] public string? LastErrorCode { get; set; }
    [Column("last_error_message"), MaxLength(2000)] public string? LastErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("note_asr_leases")]
public sealed class NoteAsrLeaseEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("attempt_id")] public Guid AttemptId { get; set; }
    [Column("node_id")] public Guid NodeId { get; set; }
    [Column("lease_token")] public Guid LeaseToken { get; set; }
    [Column("fencing_token")] public long FencingToken { get; set; }
    [Column("status"), MaxLength(32)] public string Status { get; set; } = "Active";
    [Column("claimed_at")] public DateTimeOffset ClaimedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
    [Column("ended_at")] public DateTimeOffset? EndedAt { get; set; }
}

[Table("note_asr_ack_receipts")]
public sealed class NoteAsrAckReceiptEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("attempt_id")] public Guid AttemptId { get; set; }
    [Column("lease_id")] public Guid LeaseId { get; set; }
    [Column("fencing_token")] public long FencingToken { get; set; }
    [Column("kind"), MaxLength(32)] public string Kind { get; set; } = "";
    [Column("result"), MaxLength(32)] public string Result { get; set; } = "";
    [Column("segment_index")] public int SegmentIndex { get; set; }
    [Column("provider_handle"), MaxLength(256)] public string? ProviderHandle { get; set; }
    [Column("result_text")] public string? ResultText { get; set; }
    [Column("coverage_json")] public string? CoverageJson { get; set; }
    [Column("error_code"), MaxLength(64)] public string? ErrorCode { get; set; }
    [Column("error_message"), MaxLength(2000)] public string? ErrorMessage { get; set; }
    [Column("received_at")] public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Immutable successful-segment evidence for local/Batch execution. It is deliberately separate from
/// AiRequestLogs so a local receipt is never presented as a provider SDK cloud call.
/// </summary>
[Table("note_asr_segment_evidence")]
public sealed class NoteAsrSegmentEvidenceEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("attempt_id")] public Guid AttemptId { get; set; }
    [Column("task_id")] public Guid TaskId { get; set; }
    [Column("version_id")] public Guid VersionId { get; set; }
    [Column("route"), MaxLength(32)] public string Route { get; set; } = "";
    [Column("fencing_token")] public long FencingToken { get; set; }
    [Column("segment_index")] public int SegmentIndex { get; set; }
    [Column("recording_id")] public Guid RecordingId { get; set; }
    [Column("recording_segment")] public int RecordingSegment { get; set; }
    [Column("chunk_index")] public int ChunkIndex { get; set; }
    [Column("source_file_index")] public int SourceFileIndex { get; set; }
    [Column("source_sha256"), MaxLength(64)] public string SourceSha256 { get; set; } = "";
    [Column("source_offset_samples")] public long SourceOffsetSamples { get; set; }
    [Column("samples")] public long Samples { get; set; }
    [Column("receipt_kind"), MaxLength(32)] public string ReceiptKind { get; set; } = "";
    [Column("provider_handle"), MaxLength(256)] public string ProviderHandle { get; set; } = "";
    [Column("result_text")] public string ResultText { get; set; } = "";
    [Column("result_sha256"), MaxLength(64)] public string ResultSha256 { get; set; } = "";
    [Column("coverage_json")] public string? CoverageJson { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("note_asr_batch_jobs")]
public sealed class NoteAsrBatchJobEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("task_id")] public Guid TaskId { get; set; }
    [Column("attempt_id")] public Guid? AttemptId { get; set; }
    [Column("intent_json")] public string IntentJson { get; set; } = "{}";
    [Column("status"), MaxLength(32)] public string Status { get; set; } = "Creating";
    [Column("status_raw"), MaxLength(64)] public string? StatusRaw { get; set; }
    [Column("batch_id"), MaxLength(128)] public string? BatchId { get; set; }
    [Column("input_file_id"), MaxLength(128)] public string? InputFileId { get; set; }
    [Column("output_file_id"), MaxLength(128)] public string? OutputFileId { get; set; }
    [Column("error_file_id"), MaxLength(128)] public string? ErrorFileId { get; set; }
    [Column("input_expire_at")] public DateTimeOffset? InputExpireAt { get; set; }
    [Column("batch_expires_at")] public DateTimeOffset? BatchExpiresAt { get; set; }
    [Column("model"), MaxLength(128)] public string Model { get; set; } = "";
    [Column("base_url"), MaxLength(2048)] public string BaseUrl { get; set; } = "";
    [Column("profile_revision")] public Guid ProfileRevision { get; set; }
    [Column("credential_secret")] public string CredentialSecret { get; set; } = "";
    [Column("consent_ref"), MaxLength(128)] public string ConsentRef { get; set; } = "";
    [Column("completion_window"), MaxLength(32)] public string CompletionWindow { get; set; } = "24h";
    [Column("expected_custom_ids_json")] public string ExpectedCustomIdsJson { get; set; } = "[]";
    [Column("total_items")] public int? TotalItems { get; set; }
    [Column("completed_items")] public int? CompletedItems { get; set; }
    [Column("failed_items")] public int? FailedItems { get; set; }
    [Column("result_json")] public string? ResultJson { get; set; }
    [Column("output_imported_at")] public DateTimeOffset? OutputImportedAt { get; set; }
    [Column("last_error_code"), MaxLength(64)] public string? LastErrorCode { get; set; }
    [Column("last_error_message"), MaxLength(2000)] public string? LastErrorMessage { get; set; }
    [Column("last_polled_at")] public DateTimeOffset? LastPolledAt { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
