using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("ai_request_logs")]
public sealed class AiRequestLogEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("module")]
    [MaxLength(128)]
    public string Module { get; set; } = string.Empty;

    [Column("purpose")]
    [MaxLength(128)]
    public string Purpose { get; set; } = string.Empty;

    [Column("source_object_type")]
    [MaxLength(128)]
    public string SourceObjectType { get; set; } = string.Empty;

    [Column("source_object_id")]
    [MaxLength(256)]
    public string SourceObjectId { get; set; } = string.Empty;

    [Column("provider")]
    [MaxLength(32)]
    public string Provider { get; set; } = "litellm";

    [Column("model")]
    [MaxLength(128)]
    public string Model { get; set; } = string.Empty;

    [Column("litellm_request_id")]
    [MaxLength(128)]
    public string? LiteLlmRequestId { get; set; }

    [Column("correlation_id")]
    [MaxLength(128)]
    public string CorrelationId { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = string.Empty;

    [Column("attempt_number")]
    public int AttemptNumber { get; set; }

    [Column("max_attempts")]
    public int MaxAttempts { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Column("request_messages_json", TypeName = "jsonb")]
    public string RequestMessagesJson { get; set; } = "[]";

    [Column("request_payload_json", TypeName = "jsonb")]
    public string RequestPayloadJson { get; set; } = "{}";

    [Column("response_raw_json", TypeName = "jsonb")]
    public string ResponseRawJson { get; set; } = "{}";

    [Column("response_text")]
    public string? ResponseText { get; set; }

    [Column("parsed_output_json", TypeName = "jsonb")]
    public string? ParsedOutputJson { get; set; }

    [Column("schema_name")]
    [MaxLength(128)]
    public string? SchemaName { get; set; }

    [Column("schema_version")]
    [MaxLength(32)]
    public string? SchemaVersion { get; set; }

    [Column("schema_json_snapshot", TypeName = "jsonb")]
    public string? SchemaJsonSnapshot { get; set; }

    [Column("schema_validation_errors_json", TypeName = "jsonb")]
    public string SchemaValidationErrorsJson { get; set; } = "[]";

    [Column("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [Column("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [Column("total_tokens")]
    public int? TotalTokens { get; set; }

    [Column("estimated_cost")]
    public decimal? EstimatedCost { get; set; }

    [Column("currency")]
    [MaxLength(16)]
    public string? Currency { get; set; }

    [Column("input_chars")]
    public int InputChars { get; set; }

    [Column("output_chars")]
    public int OutputChars { get; set; }

    [Column("input_hash")]
    [MaxLength(128)]
    public string InputHash { get; set; } = string.Empty;

    [Column("output_hash")]
    [MaxLength(128)]
    public string OutputHash { get; set; } = string.Empty;

    [Column("error_code")]
    [MaxLength(128)]
    public string? ErrorCode { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("metadata_json", TypeName = "jsonb")]
    public string MetadataJson { get; set; } = "{}";
}
