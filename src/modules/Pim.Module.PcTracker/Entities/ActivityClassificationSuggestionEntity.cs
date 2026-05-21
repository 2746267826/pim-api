using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_activity_classification_suggestions")]
public class ActivityClassificationSuggestionEntity
{
    [Key][Column("id")] public Guid Id { get; set; }
    [Column("cluster_key")][MaxLength(256)] public string ClusterKey { get; set; } = string.Empty;
    [Column("sample_count")] public int SampleCount { get; set; }
    [Column("total_duration_seconds")] public double TotalDurationSeconds { get; set; }
    [Column("sample_records_json", TypeName = "jsonb")] public string SampleRecordsJson { get; set; } = "[]";
    [Column("sanitized_context_json", TypeName = "jsonb")] public string SanitizedContextJson { get; set; } = "{}";
    [Column("current_category")][MaxLength(64)] public string? CurrentCategory { get; set; }
    [Column("suggested_category")][MaxLength(64)] public string? SuggestedCategory { get; set; }
    [Column("suggested_project_tag")][MaxLength(128)] public string? SuggestedProjectTag { get; set; }
    [Column("suggested_rules_json", TypeName = "jsonb")] public string? SuggestedRulesJson { get; set; }
    [Column("user_feedback")] public string? UserFeedback { get; set; }
    [Column("llm_response_json", TypeName = "jsonb")] public string? LlmResponseJson { get; set; }
    [Column("status")][MaxLength(16)] public string Status { get; set; } = "pending";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
