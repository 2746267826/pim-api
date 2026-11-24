using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pim.Module.NoteTaskSuggestions.DTOs;

/// <summary>
/// Preserves the original start/end text for event suggestions. A plain
/// <see cref="DateTimeOffset"/> binding silently applies the server's local offset
/// to values such as 2026-11-21T19:00:00, which makes the declared IANA time zone
/// impossible to honor. The service resolves the raw value explicitly.
/// </summary>
public sealed class UpdateNoteTaskSuggestionRequestJsonConverter
    : JsonConverter<UpdateNoteTaskSuggestionRequest>
{
    public override UpdateNoteTaskSuggestionRequest Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("建议更新必须是 JSON 对象");

        var fields = new Dictionary<string, JsonElement>(
            options.PropertyNameCaseInsensitive
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            fields[property.Name] = property.Value;

        T? Read<T>(string name)
        {
            var key = options.PropertyNamingPolicy?.ConvertName(name) ?? name;
            if (!fields.TryGetValue(key, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return default;
            return value.Deserialize<T>(options);
        }

        string? ReadRawTime(string name)
        {
            var key = options.PropertyNamingPolicy?.ConvertName(name) ?? name;
            if (!fields.TryGetValue(key, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new JsonException($"{name} 必须是 ISO-8601 时间或 null");
            return value.GetString();
        }

        var startRaw = ReadRawTime(nameof(UpdateNoteTaskSuggestionRequest.Start));
        var endRaw = ReadRawTime(nameof(UpdateNoteTaskSuggestionRequest.End));
        return new UpdateNoteTaskSuggestionRequest(
            Read<Guid?>(nameof(UpdateNoteTaskSuggestionRequest.ExpectedRevision)) ?? Guid.Empty,
            Read<string>(nameof(UpdateNoteTaskSuggestionRequest.Title)) ?? "",
            Read<string>(nameof(UpdateNoteTaskSuggestionRequest.Description)),
            Read<int?>(nameof(UpdateNoteTaskSuggestionRequest.Priority)) ?? 0,
            Read<DateTimeOffset?>(nameof(UpdateNoteTaskSuggestionRequest.Due)),
            Read<Guid?>(nameof(UpdateNoteTaskSuggestionRequest.TaskBookId)),
            Read<Guid?>(nameof(UpdateNoteTaskSuggestionRequest.CalendarId)),
            ParseExplicitOffset(startRaw),
            ParseExplicitOffset(endRaw),
            Read<string>(nameof(UpdateNoteTaskSuggestionRequest.TimeZoneId)))
        {
            StartRaw = startRaw,
            EndRaw = endRaw
        };
    }

    public override void Write(
        Utf8JsonWriter writer, UpdateNoteTaskSuggestionRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(Name(options, nameof(UpdateNoteTaskSuggestionRequest.ExpectedRevision)),
            value.ExpectedRevision);
        writer.WriteString(Name(options, nameof(UpdateNoteTaskSuggestionRequest.Title)), value.Title);
        WriteNullable(writer, options, nameof(UpdateNoteTaskSuggestionRequest.Description),
            value.Description);
        writer.WriteNumber(Name(options, nameof(UpdateNoteTaskSuggestionRequest.Priority)),
            value.Priority);
        WriteDateTime(writer, options, nameof(UpdateNoteTaskSuggestionRequest.Due), value.Due);
        WriteNullable(writer, options, nameof(UpdateNoteTaskSuggestionRequest.TaskBookId),
            value.TaskBookId);
        WriteNullable(writer, options, nameof(UpdateNoteTaskSuggestionRequest.CalendarId),
            value.CalendarId);
        WriteDateTime(writer, options, nameof(UpdateNoteTaskSuggestionRequest.Start), value.Start);
        WriteDateTime(writer, options, nameof(UpdateNoteTaskSuggestionRequest.End), value.End);
        WriteNullable(writer, options, nameof(UpdateNoteTaskSuggestionRequest.TimeZoneId),
            value.TimeZoneId);
        writer.WriteEndObject();
    }

    private static DateTimeOffset? ParseExplicitOffset(string? value) =>
        value is not null &&
        Services.NoteTaskSuggestionTimeZone.HasExplicitOffset(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;

    private static string Name(JsonSerializerOptions options, string name) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;

    private static void WriteDateTime(
        Utf8JsonWriter writer, JsonSerializerOptions options, string name, DateTimeOffset? value)
    {
        if (!value.HasValue) return;
        writer.WriteString(Name(options, name), value.Value.ToUniversalTime());
    }

    private static void WriteNullable<T>(
        Utf8JsonWriter writer, JsonSerializerOptions options, string name, T? value)
    {
        if (value is null) return;
        writer.WritePropertyName(Name(options, name));
        JsonSerializer.Serialize(writer, value, options);
    }
}
