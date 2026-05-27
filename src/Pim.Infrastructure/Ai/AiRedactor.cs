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

    private static readonly HashSet<string> NonSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "max_tokens",
        "prompt_tokens",
        "completion_tokens",
        "total_tokens"
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
        if (text is null)
        {
            return null;
        }

        var redacted = SensitiveKeyValueRegex().Replace(text, match =>
        {
            var key = match.Groups["key"].Value;
            if (!IsSensitiveKey(key))
            {
                return match.Value;
            }

            return match.Groups["boundary"].Value
                + match.Groups["prefix"].Value
                + "[REDACTED]"
                + match.Groups["quote"].Value;
        });

        return TokenLikeValueRegex().Replace(redacted, "[REDACTED]");
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer, string? propertyName)
    {
        if (propertyName is not null && IsSensitiveKey(propertyName))
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

    private static bool IsSensitiveKey(string key)
    {
        if (NonSecretKeys.Contains(key))
        {
            return false;
        }

        if (SensitiveKeys.Contains(key))
        {
            return true;
        }

        var normalized = NormalizeKey(key);
        return normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal);
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c is '_' or '-' or '.' || char.IsWhiteSpace(c))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?i)(bearer\s+[a-z0-9._\-+/=]+|sk-[a-z0-9_\-]{8,}|eyJ[a-z0-9_\-]+\.[a-z0-9_\-]+\.[a-z0-9_\-]+)")]
    private static partial Regex TokenLikeValueRegex();

    [GeneratedRegex(@"(?i)(?<boundary>^|[\s,{[(])(?<prefix>[""']?(?<key>[a-z0-9_.\-]*(?:api[_\-.]?key|token|secret|password|authorization|private[_\-.]?key)[a-z0-9_.\-]*)[""']?\s*(?:=|:)\s*(?<quote>[""']?))(?<value>[^\s,""'}]+)(?<endquote>[""']?)")]
    private static partial Regex SensitiveKeyValueRegex();
}
