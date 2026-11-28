using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// Converts persisted Outlook events and partial update requests into the complete
/// draft shape required by the writeback mapper. Keeping this in one place prevents
/// undo from restoring a lossy subset of the original event.
/// </summary>
public static class OutlookEventDraftFactory
{
    public static CreateEventRequest FromEntity(EventEntity entity) => new(
        CalendarId: entity.CalendarId,
        Title: entity.Title,
        Description: entity.Description,
        Location: entity.Location,
        DtStart: entity.DtStart,
        DtEnd: entity.DtEnd,
        RRule: entity.RRule,
        Uid: entity.Uid,
        IsAllDay: entity.IsAllDay,
        TimeZoneId: entity.TimeZoneId ?? entity.SourceTimeZoneId,
        DescriptionFormat: entity.DescriptionFormat,
        ShowAs: entity.ShowAs,
        Importance: entity.Importance,
        Sensitivity: entity.Sensitivity,
        Categories: EventFieldCodec.DeserializeCategories(entity.CategoriesJson),
        IsReminderOn: entity.IsReminderOn,
        ReminderMinutesBeforeStart: entity.ReminderMinutesBeforeStart,
        Organizer: EventFieldCodec.DeserializePerson(entity.OrganizerJson, entity.Organizer),
        Attendees: EventFieldCodec.DeserializeAttendees(entity.AttendeesJson),
        IsOnlineMeeting: entity.IsOnlineMeeting,
        OnlineMeetingProvider: entity.OnlineMeetingProvider,
        OnlineMeetingUrl: entity.OnlineMeetingUrl,
        ExternalLink: entity.ExternalLink,
        AttachmentReferences: EventFieldCodec.DeserializeAttachments(entity.AttachmentReferencesJson),
        IsSeriesMaster: entity.IsSeriesMaster,
        IsException: entity.IsException,
        SeriesMasterId: entity.SeriesMasterId,
        RecurrenceId: entity.RecurrenceId,
        IsMuted: entity.IsMuted,
        AllDayStartDate: entity.AllDayStartDate,
        AllDayEndDateExclusive: entity.AllDayEndDateExclusive);

    public static CreateEventRequest FromUpdate(EventEntity entity, UpdateEventRequest request)
    {
        var isAllDay = request.IsAllDay ?? entity.IsAllDay;
        return new CreateEventRequest(
            CalendarId: request.CalendarId,
            Title: request.Title,
            Description: request.Description,
            Location: request.Location,
            DtStart: request.DtStart,
            DtEnd: request.DtEnd,
            RRule: request.RRule,
            Uid: request.Uid ?? entity.Uid,
            IsAllDay: isAllDay,
            TimeZoneId: request.TimeZoneId
                ?? entity.TimeZoneId
                ?? entity.SourceTimeZoneId
                ?? entity.OriginalStartTimeZone,
            DescriptionFormat: request.DescriptionFormat,
            ShowAs: request.ShowAs,
            Importance: request.Importance,
            Sensitivity: request.Sensitivity,
            Categories: request.Categories,
            IsReminderOn: request.IsReminderOn,
            ReminderMinutesBeforeStart: request.ReminderMinutesBeforeStart,
            Organizer: request.Organizer,
            Attendees: request.Attendees,
            IsOnlineMeeting: request.IsOnlineMeeting,
            OnlineMeetingProvider: request.OnlineMeetingProvider,
            OnlineMeetingUrl: request.OnlineMeetingUrl,
            ExternalLink: request.ExternalLink,
            AttachmentReferences: request.AttachmentReferences,
            IsSeriesMaster: request.IsSeriesMaster ?? entity.IsSeriesMaster,
            IsException: request.IsException ?? entity.IsException,
            SeriesMasterId: request.SeriesMasterId ?? entity.SeriesMasterId,
            RecurrenceId: request.RecurrenceId ?? entity.RecurrenceId,
            IsMuted: request.IsMuted,
            AllDayStartDate: isAllDay
                ? request.AllDayStartDate ?? entity.AllDayStartDate
                : null,
            AllDayEndDateExclusive: isAllDay
                ? request.AllDayEndDateExclusive ?? entity.AllDayEndDateExclusive
                : null);
    }
}
