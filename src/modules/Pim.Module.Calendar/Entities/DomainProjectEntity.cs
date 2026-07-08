using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("domain_projects")]
public class DomainProjectEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("name")][MaxLength(255)] public string Name { get; set; } = string.Empty;
    [Column("description")] public string? Description { get; set; }
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Active";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<TaskBookEntity> TaskBooks { get; set; } = new List<TaskBookEntity>();
    public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
}
