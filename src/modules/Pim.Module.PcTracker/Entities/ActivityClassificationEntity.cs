using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_activity_classifications")]
public class ActivityClassificationEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("record_key")]
    [MaxLength(256)]
    public string RecordKey { get; set; } = string.Empty;

    [Column("record_type")]
    [MaxLength(32)]
    public string RecordType { get; set; } = string.Empty;

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("source_event_ids", TypeName = "jsonb")]
    public string SourceEventIdsJson { get; set; } = "[]";

    [Column("record_key_version")]
    [MaxLength(32)]
    public string RecordKeyVersion { get; set; } = "pc-fallback-v1";

    [Column("record_key_stability")]
    [MaxLength(16)]
    public string RecordKeyStability { get; set; } = "low";

    [Column("source_type")]
    [MaxLength(32)]
    public string SourceType { get; set; } = "fallback";

    [Column("source_bucket_ids", TypeName = "jsonb")]
    public string SourceBucketIdsJson { get; set; } = "[]";

    [Column("interpretation_version")]
    [MaxLength(32)]
    public string InterpretationVersion { get; set; } = "interpreted-aw-v1";

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [Column("ended_at")]
    public DateTimeOffset EndedAt { get; set; }

    [Column("category_name")]
    [MaxLength(64)]
    public string CategoryName { get; set; } = "其他";

    [Column("category_color")]
    [MaxLength(7)]
    public string CategoryColor { get; set; } = "#64748b";

    [Column("project_tag")]
    [MaxLength(128)]
    public string? ProjectTag { get; set; }

    [Column("confidence")]
    public double Confidence { get; set; } = 0.2;

    [Column("source")]
    [MaxLength(32)]
    public string Source { get; set; } = "fallback";

    [Column("source_rule_id")]
    public Guid? SourceRuleId { get; set; }

    [Column("explanation")]
    public string Explanation { get; set; } = "No rule or heuristic matched.";

    [Column("classifier_version")]
    [MaxLength(32)]
    public string ClassifierVersion { get; set; } = "local-v1";

    [Column("classified_at")]
    public DateTimeOffset ClassifiedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("audit_id")]
    public Guid? AuditId { get; set; }
}
