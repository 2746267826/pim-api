using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pim.Infrastructure.Ai;

public static partial class AiRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
        "id_token",
        "token",
        "jwt",
        "password",
        "secret",
        "client_secret",
        "clientSecret",
        "secret_key",
        "private_key",
        "x-api-key",
        "app_password",
        "nextcloud_app_password",
        "virtual_key",
        "virtual_key_secret",
        "litellm_virtual_key"
    };

    public static string RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedacted(document.RootElement, writer, propertyName: null);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { raw = RedactPlainText(json) ?? string.Empty });
        }
    }

    public static string? RedactPlainText(string? text)
    {
        return text is null ? null : TokenLikeValueRegex().Replace(text, "[REDACTED]");
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer, string? propertyName)
    {
        if (propertyName is not null && SensitiveKeys.Contains(propertyName))
        {
            writer.WriteStringValue("[REDACTED]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(property.Value, writer, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer, propertyName: null);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactPlainText(element.GetString()) ?? string.Empty);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    [GeneratedRegex(@"(?i)(bearer\s+[a-z0-9._\-+/=]+|sk-[a-z0-9_\-]{8,}|eyJ[a-z0-9_\-]+\.[a-z0-9_\-]+\.[a-z0-9_\-]+)")]
    private static partial Regex TokenLikeValueRegex();
}
