using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("calendars")]
public class CalendarEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("name")][MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Column("color")][MaxLength(7)] public string Color { get; set; } = "#3B82F6";
    [Column("kind")][MaxLength(20)] public string Kind { get; set; } = "calendar";
    [Column("is_default")] public bool IsDefault { get; set; }
    [Column("source"), MaxLength(32)] public string Source { get; set; } = "manual";
    [Column("is_visible")] public bool IsVisible { get; set; } = true;
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
    [Column("deleted_by_operation_id")] public Guid? DeletedByOperationId { get; set; }
    [Column("deleted_by_operation_kind")][MaxLength(64)] public string? DeletedByOperationKind { get; set; }

    public ICollection<EventEntity> Events { get; set; } = new List<EventEntity>();
    public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
}
