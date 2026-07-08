using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("report_artifacts")]
public class ReportArtifactEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("kind")][MaxLength(40)] public string Kind { get; set; } = "Daily";
    [Column("project_id")] public Guid? ProjectId { get; set; }
    [Column("risk_level")][MaxLength(80)] public string RiskLevel { get; set; } = "L0AutomaticArtifact";
    [Column("inputs_json", TypeName = "jsonb")] public string InputsJson { get; set; } = "{}";
    [Column("metrics_json", TypeName = "jsonb")] public string MetricsJson { get; set; } = "{}";
    [Column("content_markdown")] public string ContentMarkdown { get; set; } = string.Empty;
    [Column("generated_at")] public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Active";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ReportSuggestionEntity> Suggestions { get; set; } = new List<ReportSuggestionEntity>();
}
