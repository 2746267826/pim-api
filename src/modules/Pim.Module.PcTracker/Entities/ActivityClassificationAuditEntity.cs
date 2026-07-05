using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_activity_classification_audits")]
public class ActivityClassificationAuditEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("operation")]
    [MaxLength(64)]
    public string Operation { get; set; } = string.Empty;

    [Column("rule_id")]
    public Guid? RuleId { get; set; }

    [Column("suggestion_id")]
    public Guid? SuggestionId { get; set; }

    [Column("range_mode")]
    [MaxLength(16)]
    public string RangeMode { get; set; } = string.Empty;

    [Column("date_from")]
    [MaxLength(16)]
    public string? DateFrom { get; set; }

    [Column("date_to")]
    [MaxLength(16)]
    public string? DateTo { get; set; }

    [Column("affected_record_count")]
    public int AffectedRecordCount { get; set; }

    [Column("affected_duration_seconds")]
    public double AffectedDurationSeconds { get; set; }

    [Column("affected_record_keys", TypeName = "jsonb")]
    public string AffectedRecordKeysJson { get; set; } = "[]";

    [Column("created_by_user_id")]
    public Guid? CreatedByUserId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
