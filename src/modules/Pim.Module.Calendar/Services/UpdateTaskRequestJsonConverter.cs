using System.Text.Json;
using System.Text.Json.Serialization;
using Pim.Module.Calendar.DTOs;

namespace Pim.Module.Calendar.Services;

/// <summary>Tracks JSON presence without turning malformed values into destructive clears.</summary>
public sealed class UpdateTaskRequestJsonConverter : JsonConverter<UpdateTaskRequest>
{
    public override UpdateTaskRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Task update must be a JSON object.");

        var fields = new Dictionary<string, JsonElement>(options.PropertyNameCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            fields[property.Name] = property.Value;

        var provided = new HashSet<string>(StringComparer.Ordinal);
        T? Read<T>(string name)
        {
            if (!fields.TryGetValue(options.PropertyNamingPolicy?.ConvertName(name) ?? name, out var value))
                return default;
            provided.Add(name);
            return value.Deserialize<T>(options);
        }

        return new UpdateTaskRequest(
            Read<Guid?>("CalendarId"), Read<string>("Title"), Read<string>("Description"),
            Read<int?>("Priority"), Read<string>("EstimatedDuration"), Read<string>("MinimumSegment"),
            Read<DateTimeOffset?>("Due"), Read<DateTimeOffset?>("DtStart"), Read<string>("Status"),
            Read<DateTimeOffset?>("PlannedEnd"), Read<Guid?>("TaskBookId"), Read<int?>("PercentComplete"))
        {
            ProvidedFields = provided,
        };
    }

    public override void Write(Utf8JsonWriter writer, UpdateTaskRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        void Field<T>(string name, T fieldValue)
        {
            if (!value.WasProvided(name)) return;
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
            JsonSerializer.Serialize(writer, fieldValue, options);
        }

        Field(nameof(value.CalendarId), value.CalendarId);
        Field(nameof(value.Title), value.Title);
        Field(nameof(value.Description), value.Description);
        Field(nameof(value.Priority), value.Priority);
        Field(nameof(value.EstimatedDuration), value.EstimatedDuration);
        Field(nameof(value.MinimumSegment), value.MinimumSegment);
        Field(nameof(value.Due), value.Due);
        Field(nameof(value.DtStart), value.DtStart);
        Field(nameof(value.Status), value.Status);
        Field(nameof(value.PlannedEnd), value.PlannedEnd);
        Field(nameof(value.TaskBookId), value.TaskBookId);
        Field(nameof(value.PercentComplete), value.PercentComplete);
        writer.WriteEndObject();
    }
}
