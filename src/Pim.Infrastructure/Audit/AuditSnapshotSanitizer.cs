using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pim.Infrastructure.Audit;

/// <summary>
/// Server-side redaction for audit snapshot JSON before it is returned to clients.
/// Provider concurrency tokens, provider event ids, raw payloads and other sensitive
/// keys are removed recursively; the stored audit evidence is never modified.
/// </summary>
public static partial class AuditSnapshotSanitizer
{
    private static readonly Regex SensitiveKey = SensitiveKeyRegex();

    // Mirrors the client-side LEGACY_SENSITIVE_KEY_PATTERN in
    // src/client-web/src/utils/eventFieldDiff.ts so the API no longer ships raw values.
    [GeneratedRegex(
        "(metadata|raw|body|header|secret|token|password|etag|change[_-]?key|outlook.*id|graph|ical[-_]?uid|recurrence[-_]?id|source[-_]?ics[-_]?component|delta[-_]?link)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();

    /// <summary>Removes sensitive keys from a JSON document; non-JSON or empty input yields "{}".</summary>
    public static string SanitizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return "{}";
        }

        if (node is null)
        {
            return "{}";
        }

        var cleaned = Redact(node);
        return cleaned?.ToJsonString() ?? "{}";
    }

    private static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var clone = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key is null || SensitiveKey.IsMatch(key))
                    {
                        continue;
                    }

                    var redacted = Redact(value);
                    if (redacted is not null)
                    {
                        clone[key] = redacted;
                    }
                }

                return clone;

            case JsonArray arr:
                var array = new JsonArray();
                foreach (var item in arr)
                {
                    var redacted = Redact(item);
                    if (redacted is not null)
                    {
                        array.Add(redacted);
                    }
                }

                return array;

            default:
                return node is null ? null : node.DeepClone();
        }
    }
}
