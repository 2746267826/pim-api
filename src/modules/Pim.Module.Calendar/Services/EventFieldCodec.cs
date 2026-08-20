using System.Text.Json;
using Pim.Module.Calendar.DTOs;

namespace Pim.Module.Calendar.Services;

public static class EventFieldCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeCategories(IReadOnlyList<string>? value)
    {
        if (value is null || value.Count == 0)
            return "[]";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in value)
        {
            var trimmed = item?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && seen.Add(trimmed))
                result.Add(trimmed);
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public static IReadOnlyList<string> DeserializeCategories(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<string>();

        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(value, JsonOptions);
            if (items is null)
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var item in items)
            {
                var trimmed = item?.Trim() ?? string.Empty;
                if (trimmed.Length > 0 && seen.Add(trimmed))
                    result.Add(trimmed);
            }

            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string? SerializePerson(EventPersonDto? value)
    {
        if (value is null)
            return null;

        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static EventPersonDto? DeserializePerson(string? json, string? legacyOrganizer)
    {
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var person = JsonSerializer.Deserialize<EventPersonDto>(json, JsonOptions);
                if (person is not null)
                    return person;
            }
            catch
            {
            }
        }

        if (!string.IsNullOrEmpty(legacyOrganizer))
            return new EventPersonDto(legacyOrganizer, null);

        return null;
    }

    public static string SerializeAttendees(IReadOnlyList<EventAttendeeDto>? value)
    {
        if (value is null || value.Count == 0)
            return "[]";

        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static IReadOnlyList<EventAttendeeDto> DeserializeAttendees(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<EventAttendeeDto>();

        try
        {
            var items = JsonSerializer.Deserialize<List<EventAttendeeDto>>(value, JsonOptions);
            return (IReadOnlyList<EventAttendeeDto>?)items ?? Array.Empty<EventAttendeeDto>();
        }
        catch
        {
            return Array.Empty<EventAttendeeDto>();
        }
    }

    public static string SerializeAttachments(IReadOnlyList<EventAttachmentReferenceDto>? value)
    {
        if (value is null || value.Count == 0)
            return "[]";

        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static IReadOnlyList<EventAttachmentReferenceDto> DeserializeAttachments(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<EventAttachmentReferenceDto>();

        try
        {
            var items = JsonSerializer.Deserialize<List<EventAttachmentReferenceDto>>(value, JsonOptions);
            return (IReadOnlyList<EventAttachmentReferenceDto>?)items ?? Array.Empty<EventAttachmentReferenceDto>();
        }
        catch
        {
            return Array.Empty<EventAttachmentReferenceDto>();
        }
    }
}
