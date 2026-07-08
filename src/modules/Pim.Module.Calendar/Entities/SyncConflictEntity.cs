using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("sync_conflicts")]
public class SyncConflictEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("provider")][MaxLength(40)] public string Provider { get; set; } = "outlook";
    [Column("object_type")][MaxLength(80)] public string ObjectType { get; set; } = "event";
    [Column("object_id")] public Guid ObjectId { get; set; }
    [Column("graph_event_id")][MaxLength(255)] public string? GraphEventId { get; set; }
    [Column("conflict_kind")][MaxLength(120)] public string ConflictKind { get; set; } = "both_sides_changed";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "open";
    [Column("pim_snapshot_json", TypeName = "jsonb")] public string PimSnapshotJson { get; set; } = "{}";
    [Column("external_snapshot_json", TypeName = "jsonb")] public string ExternalSnapshotJson { get; set; } = "{}";
    [Column("resolved_confirmation_id")] public Guid? ResolvedConfirmationId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
