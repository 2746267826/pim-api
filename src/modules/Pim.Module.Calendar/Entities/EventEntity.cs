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
    [Column("description_format")][MaxLength(10)] public string? DescriptionFormat { get; set; }
    [Column("show_as")][MaxLength(20)] public string? ShowAs { get; set; }
    [Column("importance")][MaxLength(20)] public string? Importance { get; set; }
    [Column("sensitivity")][MaxLength(20)] public string? Sensitivity { get; set; }
    [Column("categories", TypeName = "jsonb")] public string CategoriesJson { get; set; } = "[]";
    [Column("is_reminder_on")] public bool IsReminderOn { get; set; }
    [Column("reminder_minutes_before_start")] public int? ReminderMinutesBeforeStart { get; set; }
    [Column("organizer_json", TypeName = "jsonb")] public string? OrganizerJson { get; set; }
    [Column("attendees", TypeName = "jsonb")] public string AttendeesJson { get; set; } = "[]";
    [Column("is_online_meeting")] public bool IsOnlineMeeting { get; set; }
    [Column("online_meeting_provider")][MaxLength(50)] public string? OnlineMeetingProvider { get; set; }
    [Column("online_meeting_url")] public string? OnlineMeetingUrl { get; set; }
    [Column("external_link")] public string? ExternalLink { get; set; }
    [Column("attachment_references", TypeName = "jsonb")] public string AttachmentReferencesJson { get; set; } = "[]";
    [Column("source")][MaxLength(20)] public string Source { get; set; } = "manual";
    [Column("outlook_event_id")][MaxLength(255)] public string? OutlookEventId { get; set; }
    [Column("outlook_connection_id")] public Guid? OutlookConnectionId { get; set; }
    [Column("outlook_calendar_binding_id")] public Guid? OutlookCalendarBindingId { get; set; }
    [Column("outlook_series_master_id"), MaxLength(512)] public string? OutlookSeriesMasterId { get; set; }
    [Column("outlook_event_type"), MaxLength(32)] public string? OutlookEventType { get; set; }
    [Column("original_start_time_zone"), MaxLength(128)] public string? OriginalStartTimeZone { get; set; }
    [Column("original_end_time_zone"), MaxLength(128)] public string? OriginalEndTimeZone { get; set; }
    [Column("all_day_start_date")] public DateOnly? AllDayStartDate { get; set; }
    [Column("all_day_end_date_exclusive")] public DateOnly? AllDayEndDateExclusive { get; set; }
    [Column("graph_recurrence_json", TypeName = "jsonb")] public string GraphRecurrenceJson { get; set; } = "{}";
    [Column("last_seen_sync_generation")] public Guid? LastSeenSyncGeneration { get; set; }
    [Column("outlook_sync_state"), MaxLength(32)] public string? OutlookSyncState { get; set; }
    [Column("schedule_plan_id")] public Guid? SchedulePlanId { get; set; }
    [Column("is_all_day")] public bool IsAllDay { get; set; }
    [Column("time_zone_id")][MaxLength(100)] public string? TimeZoneId { get; set; }
    [Column("source_time_zone_id")][MaxLength(100)] public string? SourceTimeZoneId { get; set; }
    [Column("source_uid")][MaxLength(255)] public string? SourceUid { get; set; }
    [Column("outlook_change_key")][MaxLength(255)] public string? OutlookChangeKey { get; set; }
    [Column("outlook_etag")][MaxLength(255)] public string? OutlookEtag { get; set; }
    [Column("source_ics_component")] public string? SourceIcsComponent { get; set; }
    [Column("external_metadata_json", TypeName = "jsonb")] public string ExternalMetadataJson { get; set; } = "{}";
    [Column("recurrence_id")][MaxLength(255)] public string? RecurrenceId { get; set; }
    [Column("is_series_master")] public bool IsSeriesMaster { get; set; }
    [Column("is_exception")] public bool IsException { get; set; }
    [Column("series_master_id")] public Guid? SeriesMasterId { get; set; }
    [Column("exdates_json", TypeName = "jsonb")] public string ExDatesJson { get; set; } = "[]";
    [Column("recurrence_metadata_json", TypeName = "jsonb")] public string RecurrenceMetadataJson { get; set; } = "{}";
    [Column("deleted_by_operation_id")] public Guid? DeletedByOperationId { get; set; }
    [Column("deleted_by_operation_kind")][MaxLength(64)] public string? DeletedByOperationKind { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(CalendarId))]
    public CalendarEntity Calendar { get; set; } = null!;

    [ForeignKey(nameof(OutlookCalendarBindingId))]
    public OutlookCalendarBindingEntity? OutlookCalendarBinding { get; set; }

    [ForeignKey(nameof(SeriesMasterId))]
    public EventEntity? SeriesMaster { get; set; }
}
