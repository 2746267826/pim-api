using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public static class EventResponseMapper
{
    public static EventResponse Map(EventEntity e)
        => Map(e, e.DtStart, e.DtEnd, e.AllDayStartDate, e.AllDayEndDateExclusive);

    public static EventResponse MapExpanded(ExpandedEvent ex)
        => Map(
            ex.Entity,
            ex.OccurrenceStart,
            ex.OccurrenceEnd,
            ex.AllDayStartDate ?? ex.Entity.AllDayStartDate,
            ex.AllDayEndDateExclusive ?? ex.Entity.AllDayEndDateExclusive,
            ex.OccurrenceId,
            ex.RecurrenceId,
            ex.IsSeriesMaster,
            ex.IsException,
            ex.SeriesMasterId,
            ex.IsCancelled,
            ex.Entity.Id);

    private static EventResponse Map(
        EventEntity e,
        DateTimeOffset start,
        DateTimeOffset end,
        DateOnly? allDayStartDate,
        DateOnly? allDayEndDateExclusive,
        Guid? occurrenceId = null,
        string? recurrenceId = null,
        bool? isSeriesMaster = null,
        bool? isException = null,
        Guid? seriesMasterId = null,
        bool? isCancelled = null,
        Guid? originalEventId = null)
    {
        var outlookInfo = OutlookAdditionalInfoBuilder.Build(e);
        var cancelled = isCancelled
            ?? string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var zone = EventTimeZoneResolver.Resolve(e.TimeZoneId, e.SourceTimeZoneId, e.OriginalStartTimeZone);
        var isAllDay = e.IsAllDay;
        var localStart = !isAllDay && zone.IsResolved
            ? EventTimeZoneResolver.FormatLocal(start, zone.Zone!)
            : null;
        var localEnd = !isAllDay && zone.IsResolved
            ? EventTimeZoneResolver.FormatLocal(end, zone.Zone!)
            : null;

        return new EventResponse(
            occurrenceId ?? e.Id, e.CalendarId, e.Uid, e.Title, e.Description,
            e.Location, start, end, e.RRule, e.Status, e.Source,
            originalEventId,
            isAllDay, zone.Id, zone.SourceId, e.SourceUid,
            recurrenceId ?? e.RecurrenceId, e.ExDatesJson, e.RecurrenceMetadataJson,
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
            isSeriesMaster ?? e.IsSeriesMaster,
            isException ?? e.IsException,
            seriesMasterId ?? e.SeriesMasterId,
            cancelled,
            IsMuted: e.IsMuted,
            AllDayStartDate: isAllDay ? allDayStartDate : null,
            AllDayEndDateExclusive: isAllDay ? allDayEndDateExclusive : null,
            TimeZoneResolution: zone.Resolution,
            TimeZoneIdType: zone.Id is null ? null : EventTimeZoneResolver.IanaIdType,
            TimeZoneSourceType: zone.SourceType,
            LocalStart: localStart,
            LocalEnd: localEnd,
            DisplayTimeZoneId: EventTimeZoneResolver.DefaultDisplayTimeZoneId,
            DisplayLocalStart: isAllDay ? null : EventTimeZoneResolver.FormatLocal(start),
            DisplayLocalEnd: isAllDay ? null : EventTimeZoneResolver.FormatLocal(end));
    }
}
