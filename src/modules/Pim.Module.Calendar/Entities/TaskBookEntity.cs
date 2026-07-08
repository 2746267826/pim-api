using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("task_books")]
public class TaskBookEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("domain_project_id")] public Guid? DomainProjectId { get; set; }
    [Column("name")][MaxLength(255)] public string Name { get; set; } = string.Empty;
    [Column("kind")][MaxLength(40)] public string Kind { get; set; } = "task";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Active";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(DomainProjectId))]
    public DomainProjectEntity? DomainProject { get; set; }

    public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
}
