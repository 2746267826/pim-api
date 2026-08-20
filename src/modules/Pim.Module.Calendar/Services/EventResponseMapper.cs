using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public static class EventResponseMapper
{
    public static EventResponse Map(EventEntity e)
    {
        var outlookInfo = OutlookAdditionalInfoBuilder.Build(e);

        return new EventResponse(
            e.Id, e.CalendarId, e.Uid, e.Title, e.Description,
            e.Location, e.DtStart, e.DtEnd, e.RRule, e.Status, e.Source,
            null,
            e.IsAllDay, e.TimeZoneId, e.SourceTimeZoneId, e.SourceUid,
            e.RecurrenceId, e.ExDatesJson, e.RecurrenceMetadataJson,
            e.OutlookCalendarBindingId, e.OutlookEventId, e.OutlookEtag, e.OutlookEventType,
            outlookInfo,
            e.DescriptionFormat, e.ShowAs, e.Importance, e.Sensitivity,
            EventFieldCodec.DeserializeCategories(e.CategoriesJson),
            e.IsReminderOn, e.ReminderMinutesBeforeStart,
            EventFieldCodec.DeserializePerson(e.OrganizerJson, e.Organizer),
            EventFieldCodec.DeserializeAttendees(e.AttendeesJson),
            e.IsOnlineMeeting, e.OnlineMeetingProvider,
            e.OnlineMeetingUrl, e.ExternalLink,
            EventFieldCodec.DeserializeAttachments(e.AttachmentReferencesJson),
            e.IsSeriesMaster, e.IsException, e.SeriesMasterId);
    }

    public static EventResponse MapExpanded(ExpandedEvent ex)
    {
        var e = ex.Entity;
        var outlookInfo = OutlookAdditionalInfoBuilder.Build(e);

        return new EventResponse(
            ex.OccurrenceId, e.CalendarId, e.Uid, e.Title, e.Description,
            e.Location, ex.OccurrenceStart, ex.OccurrenceEnd,
            e.RRule, e.Status, e.Source,
            e.Id, e.IsAllDay, e.TimeZoneId,
            e.SourceTimeZoneId, e.SourceUid,
            ex.RecurrenceId ?? e.RecurrenceId, e.ExDatesJson, e.RecurrenceMetadataJson,
            e.OutlookCalendarBindingId, e.OutlookEventId, e.OutlookEtag, e.OutlookEventType,
            outlookInfo,
            e.DescriptionFormat, e.ShowAs, e.Importance, e.Sensitivity,
            EventFieldCodec.DeserializeCategories(e.CategoriesJson),
            e.IsReminderOn, e.ReminderMinutesBeforeStart,
            EventFieldCodec.DeserializePerson(e.OrganizerJson, e.Organizer),
            EventFieldCodec.DeserializeAttendees(e.AttendeesJson),
            e.IsOnlineMeeting, e.OnlineMeetingProvider,
            e.OnlineMeetingUrl, e.ExternalLink,
            EventFieldCodec.DeserializeAttachments(e.AttachmentReferencesJson),
            ex.IsSeriesMaster, ex.IsException, ex.SeriesMasterId);
    }
}
