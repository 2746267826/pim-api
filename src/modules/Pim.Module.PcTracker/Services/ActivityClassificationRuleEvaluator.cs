using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pim.Module.PcTracker.Services;

public sealed record ActivityClassificationContext(
    string? RecordType,
    string? AppName,
    string? AppNameNormalized,
    string? Domain,
    string? UrlPath,
    string? Title,
    string? WindowTitle,
    string? FilePath,
    string? BucketType);

public static class ActivityClassificationRuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static bool Matches(string? conditionsJson, ActivityClassificationContext context)
    {
        if (string.IsNullOrWhiteSpace(conditionsJson) || context is null)
            return false;

        try
        {
            using var document = JsonDocument.Parse(conditionsJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("all", out var allConditions)
                || allConditions.ValueKind != JsonValueKind.Array
                || allConditions.GetArrayLength() == 0)
                return false;

            foreach (var condition in allConditions.EnumerateArray())
            {
                if (!MatchesCondition(condition, context))
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool MatchesCondition(JsonElement condition, ActivityClassificationContext context)
    {
        if (condition.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(condition, "field", out var field)
            || !TryGetStringProperty(condition, "op", out var op)
            || !condition.TryGetProperty("value", out var value))
            return false;

        var fieldValue = GetFieldValue(field, context);
        if (fieldValue is null)
            return false;

        return op switch
        {
            "equals" => TryGetStringValue(value, out var expected)
                && fieldValue.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "contains" => TryGetStringValue(value, out var expected)
                && !string.IsNullOrEmpty(expected)
                && fieldValue.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "containsAny" => TryGetStringValues(value, out var expectedValues)
                && expectedValues.Any(expected => !string.IsNullOrEmpty(expected)
                    && fieldValue.Contains(expected, StringComparison.OrdinalIgnoreCase)),
            "startsWith" => TryGetStringValue(value, out var expected)
                && !string.IsNullOrEmpty(expected)
                && fieldValue.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "endsWith" => TryGetStringValue(value, out var expected)
                && !string.IsNullOrEmpty(expected)
                && fieldValue.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "domainSuffix" => TryGetStringValue(value, out var expected)
                && MatchesDomainSuffix(fieldValue, expected),
            "pathPrefix" => TryGetStringValue(value, out var expected)
                && MatchesPathPrefix(fieldValue, expected),
            "regex" => TryGetStringValue(value, out var expected)
                && !string.IsNullOrEmpty(expected)
                && Regex.IsMatch(fieldValue, expected, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout),
            _ => false
        };
    }

    private static string? GetFieldValue(string field, ActivityClassificationContext context) =>
        field switch
        {
            "recordType" => context.RecordType,
            "appName" => context.AppName,
            "appNameNormalized" => context.AppNameNormalized,
            "domain" => context.Domain,
            "urlPath" => context.UrlPath,
            "title" => context.Title,
            "windowTitle" => context.WindowTitle,
            "filePath" => context.FilePath,
            "bucketType" => context.BucketType,
            _ => null
        };

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetStringValue(JsonElement element, out string value)
    {
        value = string.Empty;

        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetStringValues(JsonElement element, out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();

        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var strings = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;

            strings.Add(item.GetString() ?? string.Empty);
        }

        values = strings;
        return strings.Count > 0;
    }

    private static bool MatchesDomainSuffix(string domain, string suffix)
    {
        var normalizedDomain = domain.Trim().TrimEnd('.');
        var normalizedSuffix = suffix.Trim().Trim('.');

        if (string.IsNullOrEmpty(normalizedDomain) || string.IsNullOrEmpty(normalizedSuffix))
            return false;

        return normalizedDomain.Equals(normalizedSuffix, StringComparison.OrdinalIgnoreCase)
            || normalizedDomain.EndsWith("." + normalizedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPathPrefix(string path, string prefix)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedPrefix = NormalizePath(prefix);

        if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(normalizedPrefix))
            return false;

        if (normalizedPrefix == "/")
            return normalizedPath.StartsWith("/", StringComparison.Ordinal);

        return normalizedPath.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        var boundaryIndex = trimmed.IndexOfAny(['?', '#']);
        if (boundaryIndex >= 0)
            trimmed = trimmed[..boundaryIndex];

        if (trimmed.Length == 0)
            return string.Empty;

        var withLeadingSlash = trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed
            : "/" + trimmed;

        return withLeadingSlash.Length > 1
            ? withLeadingSlash.TrimEnd('/')
            : withLeadingSlash;
    }
}
