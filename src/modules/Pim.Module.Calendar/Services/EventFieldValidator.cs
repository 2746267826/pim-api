using Pim.Core.Exceptions;
using Pim.Module.Calendar.DTOs;

namespace Pim.Module.Calendar.Services;

public static class EventFieldValidator
{
    private static readonly HashSet<string> ValidDescriptionFormats = new(StringComparer.OrdinalIgnoreCase) { "plain", "html" };
    private static readonly HashSet<string> ValidShowAs = new(StringComparer.OrdinalIgnoreCase) { "free", "tentative", "busy", "oof", "workingElsewhere" };
    private static readonly HashSet<string> ValidImportance = new(StringComparer.OrdinalIgnoreCase) { "low", "normal", "high" };
    private static readonly HashSet<string> ValidSensitivity = new(StringComparer.OrdinalIgnoreCase) { "normal", "personal", "private", "confidential" };
    private static readonly HashSet<string> ValidAttendeeTypes = new(StringComparer.OrdinalIgnoreCase) { "required", "optional", "resource" };
    private static readonly HashSet<string> ValidOnlineMeetingProviders = new(StringComparer.OrdinalIgnoreCase) { "teams", "zoom", "meet", "other" };
    private static readonly HashSet<string> ValidAttachmentKinds = new(StringComparer.OrdinalIgnoreCase) { "pimFile", "outlook" };

    public static CreateEventRequest ValidateAndNormalize(CreateEventRequest request)
    {
        request = NormalizeEnumStrings(request);
        ValidateEnumField(request.DescriptionFormat, ValidDescriptionFormats, "DescriptionFormat");
        ValidateEnumField(request.ShowAs, ValidShowAs, "ShowAs");
        ValidateEnumField(request.Importance, ValidImportance, "Importance");
        ValidateEnumField(request.Sensitivity, ValidSensitivity, "Sensitivity");
        ValidateEnumField(request.OnlineMeetingProvider, ValidOnlineMeetingProviders, "OnlineMeetingProvider");
        ValidateAttendees(request.Attendees);
        ValidateAttachments(request.AttachmentReferences);
        ValidateReminderMinutes(request.IsReminderOn, request.ReminderMinutesBeforeStart);
        return request;
    }

    public static UpdateEventRequest ValidateAndNormalize(UpdateEventRequest request)
    {
        request = NormalizeEnumStrings(request);
        ValidateEnumField(request.DescriptionFormat, ValidDescriptionFormats, "DescriptionFormat");
        ValidateEnumField(request.ShowAs, ValidShowAs, "ShowAs");
        ValidateEnumField(request.Importance, ValidImportance, "Importance");
        ValidateEnumField(request.Sensitivity, ValidSensitivity, "Sensitivity");
        ValidateEnumField(request.OnlineMeetingProvider, ValidOnlineMeetingProviders, "OnlineMeetingProvider");
        ValidateAttendees(request.Attendees);
        ValidateAttachments(request.AttachmentReferences);
        ValidateReminderMinutes(request.IsReminderOn, request.ReminderMinutesBeforeStart);
        return request;
    }

    public static OutlookEventDraft ValidateAndNormalize(OutlookEventDraft draft)
    {
        draft = NormalizeEnumStrings(draft);
        ValidateEnumField(draft.DescriptionFormat, ValidDescriptionFormats, "DescriptionFormat");
        ValidateEnumField(draft.ShowAs, ValidShowAs, "ShowAs");
        ValidateEnumField(draft.Importance, ValidImportance, "Importance");
        ValidateEnumField(draft.Sensitivity, ValidSensitivity, "Sensitivity");
        ValidateEnumField(draft.OnlineMeetingProvider, ValidOnlineMeetingProviders, "OnlineMeetingProvider");
        ValidateAttendees(draft.Attendees);
        ValidateAttachments(draft.AttachmentReferences);
        ValidateReminderMinutes(draft.IsReminderOn, draft.ReminderMinutesBeforeStart);
        return draft;
    }

    private static CreateEventRequest NormalizeEnumStrings(CreateEventRequest request)
    {
        return request with
        {
            DescriptionFormat = NormalizeString(request.DescriptionFormat),
            ShowAs = NormalizeString(request.ShowAs),
            Importance = NormalizeString(request.Importance),
            Sensitivity = NormalizeString(request.Sensitivity),
            OnlineMeetingProvider = NormalizeString(request.OnlineMeetingProvider),
            OnlineMeetingUrl = NormalizeString(request.OnlineMeetingUrl),
            ExternalLink = NormalizeString(request.ExternalLink),
            Organizer = NormalizeOrganizer(request.Organizer),
        };
    }

    private static UpdateEventRequest NormalizeEnumStrings(UpdateEventRequest request)
    {
        return request with
        {
            DescriptionFormat = NormalizeString(request.DescriptionFormat),
            ShowAs = NormalizeString(request.ShowAs),
            Importance = NormalizeString(request.Importance),
            Sensitivity = NormalizeString(request.Sensitivity),
            OnlineMeetingProvider = NormalizeString(request.OnlineMeetingProvider),
            OnlineMeetingUrl = NormalizeString(request.OnlineMeetingUrl),
            ExternalLink = NormalizeString(request.ExternalLink),
            Organizer = NormalizeOrganizer(request.Organizer),
        };
    }

    private static OutlookEventDraft NormalizeEnumStrings(OutlookEventDraft draft)
    {
        return draft with
        {
            DescriptionFormat = NormalizeString(draft.DescriptionFormat),
            ShowAs = NormalizeString(draft.ShowAs),
            Importance = NormalizeString(draft.Importance),
            Sensitivity = NormalizeString(draft.Sensitivity),
            OnlineMeetingProvider = NormalizeString(draft.OnlineMeetingProvider),
            OnlineMeetingUrl = NormalizeString(draft.OnlineMeetingUrl),
            ExternalLink = NormalizeString(draft.ExternalLink),
            Organizer = NormalizeOrganizer(draft.Organizer),
        };
    }

    private static string? NormalizeString(string? value)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static EventPersonDto? NormalizeOrganizer(EventPersonDto? organizer)
    {
        if (organizer is null)
            return null;
        var name = NormalizeString(organizer.Name);
        var email = NormalizeString(organizer.Email);
        if (name is null && email is null)
            return null;
        return organizer with { Name = name, Email = email };
    }

    private static void ValidateEnumField(string? value, HashSet<string> validValues, string fieldName)
    {
        if (value is null)
            return;

        if (!validValues.Contains(value))
            throw new DomainException(02009, $"{fieldName} 值无效：{value}");
    }

    private static void ValidateAttendees(IReadOnlyList<EventAttendeeDto>? attendees)
    {
        if (attendees is null)
            return;

        foreach (var attendee in attendees)
        {
            if (!ValidAttendeeTypes.Contains(attendee.Type))
                throw new DomainException(02009, $"Attendee.Type 值无效：{attendee.Type}");
        }
    }

    private static void ValidateAttachments(IReadOnlyList<EventAttachmentReferenceDto>? attachments)
    {
        if (attachments is null)
            return;

        foreach (var attachment in attachments)
        {
            if (!ValidAttachmentKinds.Contains(attachment.Kind))
                throw new DomainException(02009, $"Attachment.Kind 值无效：{attachment.Kind}");
        }
    }

    private static void ValidateReminderMinutes(bool? isReminderOn, int? reminderMinutes)
    {
        if (reminderMinutes.HasValue && reminderMinutes.Value < 0)
            throw new DomainException(02009, "ReminderMinutesBeforeStart 不能为负数");
    }
}
