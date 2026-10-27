using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

internal static class AiCallAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static AiTextCallResultDto Result(AiRequestLogEntity row, bool replayed) => new(row.Id, row.Status switch
    {
        "succeeded" => AiRequestStatus.Succeeded, "running" => AiRequestStatus.Running, "blocked" => AiRequestStatus.Blocked,
        "timed_out" => AiRequestStatus.TimedOut, "cancelled" => AiRequestStatus.Cancelled,
        "incomplete" => AiRequestStatus.Incomplete, _ => AiRequestStatus.Failed
    }, row.ResponseText, row.ErrorMessage, replayed);
    internal static int? Count(long? value) => value is null or < 0 or > int.MaxValue ? null : (int)value.Value;
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Safe(string? value, string? key, out bool redacted)
    {
        var result = value ?? "";
        if (!string.IsNullOrEmpty(key))
        {
            result = result.Replace(key, "[REDACTED_CREDENTIAL]", StringComparison.Ordinal);
            var encoded = JsonEncodedText.Encode(key).ToString();
            if (encoded != key) result = result.Replace(encoded, "[REDACTED_CREDENTIAL]", StringComparison.Ordinal);
        }
        redacted = result != (value ?? ""); return result;
    }
    internal static string ValidJson(string body)
    { try { using var json = JsonDocument.Parse(body); return body; } catch (JsonException) { return JsonSerializer.Serialize(new { body }, Json); } }

    internal static string SafeJson(string value, string? key, out bool redacted)
    {
        if (string.IsNullOrEmpty(key) || !value.Contains(key, StringComparison.Ordinal) &&
            !value.Contains(JsonEncodedText.Encode(key).ToString(), StringComparison.Ordinal))
        { redacted = false; return value; }
        var changed = false;
        using var doc = JsonDocument.Parse(value); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            void Write(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        writer.WriteStartObject();
                        foreach (var property in element.EnumerateObject())
                        { writer.WritePropertyName(Safe(property.Name, key, out var propertyRedacted)); changed |= propertyRedacted; Write(property.Value); }
                        writer.WriteEndObject(); break;
                    case JsonValueKind.Array:
                        writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item); writer.WriteEndArray(); break;
                    case JsonValueKind.String:
                        writer.WriteStringValue(Safe(element.GetString(), key, out var stringRedacted)); changed |= stringRedacted; break;
                    default:
                        if (element.GetRawText() == key) { writer.WriteStringValue("[REDACTED_CREDENTIAL]"); changed = true; }
                        else element.WriteTo(writer); break;
                }
            }
            Write(doc.RootElement);
        }
        redacted = changed; return changed ? Encoding.UTF8.GetString(stream.ToArray()) : value;
    }
}
