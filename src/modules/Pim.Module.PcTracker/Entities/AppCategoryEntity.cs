using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

public class AppCategoryEntity
{
    [Key][Column("id")] public Guid Id { get; set; }
    [Column("app_pattern")][MaxLength(128)] public string AppPattern { get; set; } = string.Empty;
    [Column("category_name")][MaxLength(64)] public string CategoryName { get; set; } = string.Empty;
    [Column("color")][MaxLength(7)] public string Color { get; set; } = "#6B5EE4";
    [Column("priority")] public int Priority { get; set; }
    [Column("is_builtin")] public bool IsBuiltin { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
