using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

/// <summary>
/// Owner-scoped durable ASR intent and state. The provider response remains in ai_request_logs;
/// this row is the queue/worker truth so a process restart never requires an in-memory job.
/// </summary>
[Table("ai_asr_jobs")]
public sealed class AiAsrJobEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("client_request_id")] public Guid ClientRequestId { get; set; }
    [Column("log_id")] public Guid LogId { get; set; }
    [Column("input_hash"), MaxLength(128)] public string InputHash { get; set; } = "";
    [Column("status"), MaxLength(32)] public string Status { get; set; } = "queued";
    [Column("model_profile_id")] public Guid ModelProfileId { get; set; }
    [Column("model_revision")] public Guid ModelRevision { get; set; }
    [Column("preset_id"), MaxLength(64)] public string PresetId { get; set; } = "";
    [Column("preset_version"), MaxLength(64)] public string PresetVersion { get; set; } = "";
    [Column("model"), MaxLength(128)] public string Model { get; set; } = "";
    [Column("endpoint"), MaxLength(2048)] public string Endpoint { get; set; } = "";
    [Column("language"), MaxLength(32)] public string Language { get; set; } = "auto";
    [Column("prompt_text")] public string PromptText { get; set; } = "";
    [Column("prompt_language"), MaxLength(32)] public string PromptLanguage { get; set; } = "auto";
    [Column("prompt_sha256"), MaxLength(64)] public string PromptSha256 { get; set; } = "";
    [Column("prompt_revision"), MaxLength(64)] public string PromptRevision { get; set; } = "";
    [Column("prompt_template_sha256"), MaxLength(64)] public string PromptTemplateSha256 { get; set; } = "";
    [Column("prompt_template_revision"), MaxLength(64)] public string PromptTemplateRevision { get; set; } = "";
    [Column("max_output_tokens")] public int MaxOutputTokens { get; set; }
    [Column("timeout_seconds")] public int TimeoutSeconds { get; set; }
    [Column("audio_name"), MaxLength(200)] public string AudioName { get; set; } = "";
    [Column("audio_format"), MaxLength(16)] public string AudioFormat { get; set; } = "";
    [Column("audio_size_bytes")] public long AudioSizeBytes { get; set; }
    [Column("audio_sha256"), MaxLength(64)] public string AudioSha256 { get; set; } = "";
    [Column("consent_json", TypeName = "jsonb")] public string ConsentJson { get; set; } = "{}";
    [Column("estimated_audio_seconds")] public double? EstimatedAudioSeconds { get; set; }
    [Column("estimated_completion_tokens")] public int? EstimatedCompletionTokens { get; set; }
    [Column("output_limit_exceeded")] public bool OutputLimitExceeded { get; set; }
    [Column("abort_source"), MaxLength(64)] public string AbortSource { get; set; } = "none";
    [Column("outcome_known")] public bool OutcomeKnown { get; set; }
    [Column("gateway_status")] public int? GatewayStatus { get; set; }
    [Column("gateway_name"), MaxLength(200)] public string? GatewayName { get; set; }
    [Column("gateway_evidence_source"), MaxLength(64)] public string? GatewayEvidenceSource { get; set; }
    [Column("gateway_observed_at")] public DateTimeOffset? GatewayObservedAt { get; set; }
    [Column("upstream_status")] public int? UpstreamStatus { get; set; }
    [Column("provider_attempted")] public bool ProviderAttempted { get; set; }
    [Column("response_body_complete")] public bool ResponseBodyComplete { get; set; }
    [Column("error_code"), MaxLength(128)] public string? ErrorCode { get; set; }
    [Column("error_message"), MaxLength(2000)] public string? ErrorMessage { get; set; }
    [Column("next_action"), MaxLength(500)] public string? NextAction { get; set; }
    [Column("attempt_number")] public int AttemptNumber { get; set; }
    [Column("max_attempts")] public int MaxAttempts { get; set; } = 1;
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("started_at")] public DateTimeOffset? StartedAt { get; set; }
    [Column("cancel_requested_at")] public DateTimeOffset? CancelRequestedAt { get; set; }
    [Column("finished_at")] public DateTimeOffset? FinishedAt { get; set; }
    [Column("execution_expires_at")] public DateTimeOffset? ExecutionExpiresAt { get; set; }
}
