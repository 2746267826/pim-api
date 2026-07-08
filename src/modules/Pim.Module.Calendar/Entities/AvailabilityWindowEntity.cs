using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("availability_windows")]
public class AvailabilityWindowEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("starts_at")] public DateTimeOffset StartsAt { get; set; }
    [Column("ends_at")] public DateTimeOffset EndsAt { get; set; }
    [Column("kind")][MaxLength(40)] public string Kind { get; set; } = "available";
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "manual";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
}
