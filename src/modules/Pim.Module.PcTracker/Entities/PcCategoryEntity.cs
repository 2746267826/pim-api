using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

public class PcCategoryEntity
{
    [Key] [Column("id")] public Guid Id { get; set; }
    [Column("parent_id")] public Guid? ParentId { get; set; }
    [Column("name")] [MaxLength(64)] public string Name { get; set; } = string.Empty;
    [Column("color")] [MaxLength(7)] public string Color { get; set; } = "#64748b";
    [Column("icon")] [MaxLength(32)] public string? Icon { get; set; }
    [Column("productivity")] [MaxLength(16)] public string Productivity { get; set; } = "neutral";
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("is_builtin")] public bool IsBuiltin { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ParentId))]
    public PcCategoryEntity? Parent { get; set; }
    public ICollection<PcCategoryEntity> Children { get; set; } = new List<PcCategoryEntity>();
}
