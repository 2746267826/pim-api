using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("events")]
public class EventEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("calendar_id")] public Guid CalendarId { get; set; }
    [Column("uid")][MaxLength(255)] public string Uid { get; set; } = string.Empty;
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("description")] public string? Description { get; set; }
    [Column("location")][MaxLength(500)] public string? Location { get; set; }
    [Column("dtstart")] public DateTimeOffset DtStart { get; set; }
    [Column("dtend")] public DateTimeOffset DtEnd { get; set; }
    [Column("dtstamp")] public DateTimeOffset DtStamp { get; set; } = DateTimeOffset.UtcNow;
    [Column("rrule")] public string? RRule { get; set; }
    [Column("status")][MaxLength(20)] public string Status { get; set; } = "CONFIRMED";
    [Column("organizer")][MaxLength(255)] public string? Organizer { get; set; }
    [Column("source")][MaxLength(20)] public string Source { get; set; } = "manual";
    [Column("outlook_event_id")][MaxLength(255)] public string? OutlookEventId { get; set; }
    [Column("schedule_plan_id")] public Guid? SchedulePlanId { get; set; }
    [Column("is_all_day")] public bool IsAllDay { get; set; }
    [Column("time_zone_id")][MaxLength(100)] public string? TimeZoneId { get; set; }
    [Column("source_time_zone_id")][MaxLength(100)] public string? SourceTimeZoneId { get; set; }
    [Column("source_uid")][MaxLength(255)] public string? SourceUid { get; set; }
    [Column("source_ics_component")] public string? SourceIcsComponent { get; set; }
    [Column("external_metadata_json", TypeName = "jsonb")] public string ExternalMetadataJson { get; set; } = "{}";
    [Column("recurrence_id")][MaxLength(255)] public string? RecurrenceId { get; set; }
    [Column("exdates_json", TypeName = "jsonb")] public string ExDatesJson { get; set; } = "[]";
    [Column("recurrence_metadata_json", TypeName = "jsonb")] public string RecurrenceMetadataJson { get; set; } = "{}";
    [Column("deleted_by_operation_id")] public Guid? DeletedByOperationId { get; set; }
    [Column("deleted_by_operation_kind")][MaxLength(64)] public string? DeletedByOperationKind { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(CalendarId))]
    public CalendarEntity Calendar { get; set; } = null!;
}
