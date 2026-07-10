using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("outlook_calendar_bindings")]
public sealed class OutlookCalendarBindingEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("connection_id")] public Guid ConnectionId { get; set; }
    [Column("pim_calendar_id")] public Guid PimCalendarId { get; set; }
    [Column("graph_calendar_id"), MaxLength(512)] public string GraphCalendarId { get; set; } = string.Empty;
    [Column("graph_group_id"), MaxLength(512)] public string? GraphGroupId { get; set; }
    [Column("graph_group_name"), MaxLength(255)] public string? GraphGroupName { get; set; }
    [Column("name"), MaxLength(255)] public string Name { get; set; } = string.Empty;
    [Column("color"), MaxLength(64)] public string? Color { get; set; }
    [Column("owner_name"), MaxLength(255)] public string? OwnerName { get; set; }
    [Column("owner_address"), MaxLength(320)] public string? OwnerAddress { get; set; }
    [Column("is_default_calendar")] public bool IsDefaultCalendar { get; set; }
    [Column("can_edit")] public bool CanEdit { get; set; }
    [Column("can_view_private_items")] public bool CanViewPrivateItems { get; set; }
    [Column("is_selected")] public bool IsSelected { get; set; } = true;
    [Column("remote_state"), MaxLength(32)] public string RemoteState { get; set; } = "active";
    [Column("sync_strategy"), MaxLength(32)] public string SyncStrategy { get; set; } = "window-reconcile";
    [Column("delta_link")] public string? DeltaLink { get; set; }
    [Column("baseline_window_start")] public DateTimeOffset? BaselineWindowStart { get; set; }
    [Column("baseline_window_end")] public DateTimeOffset? BaselineWindowEnd { get; set; }
    [Column("last_full_baseline_at")] public DateTimeOffset? LastFullBaselineAt { get; set; }
    [Column("last_discovery_at")] public DateTimeOffset? LastDiscoveryAt { get; set; }
    [Column("last_synced_at")] public DateTimeOffset? LastSyncedAt { get; set; }
    [Column("last_successful_generation")] public Guid? LastSuccessfulGeneration { get; set; }
    [Column("last_error_code"), MaxLength(128)] public string? LastErrorCode { get; set; }
    [Column("last_error_message")] public string? LastErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
