using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_tracker_events")]
public class TrackerEventEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("timestamp")] public DateTimeOffset Timestamp { get; set; }
    [Column("duration")] public double Duration { get; set; }
    [Column("event_type")][MaxLength(16)] public string EventType { get; set; } = "window";
    [Column("exe_path")] public string? ExePath { get; set; }
    [Column("app_name")][MaxLength(256)] public string? AppName { get; set; }
    [Column("display_name")][MaxLength(256)] public string? DisplayName { get; set; }
    [Column("window_title")] public string? WindowTitle { get; set; }
    [Column("command_line")] public string? CommandLine { get; set; }
    [Column("is_idle")] public bool IsIdle { get; set; }
    [Column("is_media_active")] public bool IsMediaActive { get; set; }
    [Column("url")] public string? Url { get; set; }
    [Column("domain")][MaxLength(512)] public string? Domain { get; set; }
    [Column("page_path")] public string? PagePath { get; set; }
    [Column("audible")] public bool? Audible { get; set; }
    [Column("incognito")] public bool? Incognito { get; set; }
    [Column("tab_count")] public int? TabCount { get; set; }
    [Column("page_visit_count")] public int PageVisitCount { get; set; }
    [Column("page_visit_duration")] public double PageVisitDuration { get; set; }
    [Column("browser")][MaxLength(16)] public string? Browser { get; set; }
    [Column("instance_id")][MaxLength(128)] public string? InstanceId { get; set; }
    [Column("raw_json", TypeName = "jsonb")] public string RawJson { get; set; } = "{}";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("date", TypeName = "date")] public DateTime Date { get; set; }
}
