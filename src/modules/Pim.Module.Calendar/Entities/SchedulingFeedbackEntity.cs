using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("scheduling_feedback")]
public class SchedulingFeedbackEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("plan_options", TypeName = "jsonb")] public string PlanOptions { get; set; } = "[]";
    [Column("selected_index")] public int SelectedIndex { get; set; }
    [Column("context", TypeName = "jsonb")] public string? Context { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
