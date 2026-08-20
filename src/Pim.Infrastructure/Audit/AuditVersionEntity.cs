using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Audit;

[Table("audit_versions")]
public class AuditVersionEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("object_type")][MaxLength(80)] public string ObjectType { get; set; } = string.Empty;
    [Column("object_id")] public Guid ObjectId { get; set; }
    [Column("confirmation_id")] public Guid? ConfirmationId { get; set; }
    [Column("user_id")] public Guid? UserId { get; set; }
    [Column("source")][MaxLength(80)] public string Source { get; set; } = "pim";
    [Column("actor")][MaxLength(255)] public string Actor { get; set; } = "system";
    [Column("before_json")] public string BeforeJson { get; set; } = "{}";
    [Column("after_json")] public string AfterJson { get; set; } = "{}";
    [Column("changed_fields_json")] public string ChangedFieldsJson { get; set; } = "[]";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
