using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("report_suggestions")]
public class ReportSuggestionEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("report_id")] public Guid ReportId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("action")][MaxLength(120)] public string Action { get; set; } = string.Empty;
    [Column("summary")] public string Summary { get; set; } = string.Empty;
    [Column("changed_fields_json", TypeName = "jsonb")] public string ChangedFieldsJson { get; set; } = "[]";
    [Column("payload_json", TypeName = "jsonb")] public string PayloadJson { get; set; } = "{}";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Open";
    [Column("confirmation_id")] public Guid? ConfirmationId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(ReportId))]
    public ReportArtifactEntity Report { get; set; } = null!;
}
