using System.Text.RegularExpressions;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Diagnostics;

public static class DiagnosticRedaction
{
    public const string Marker = "[已脱敏]";
    public const string PathMarker = "<path>";

    private static readonly HashSet<string> SensitiveAttributeNames = new(
        [
            "authorization",
            "access_token",
            "accesstoken",
            "refresh_token",
            "refreshtoken",
            "token",
            "api_key",
            "apikey",
            "client_secret",
            "clientsecret",
            "secret",
            "password",
            "graphEventId",
            "changeKey",
            "signature",
            "cookie",
            "set-cookie",
            "private_key",
            "privatekey",
            "credential",
            "credentials"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Regex JsonSecretPattern = new(
        "(?i)(\"(?:authorization|access_token|refresh_token|token|api_key|apikey|client_secret|secret|password|graphEventId|changeKey|signature|cookie|set-cookie|private_key|privatekey|credential|credentials)\"\\s*:\\s*)\"[^\"]*\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuerySecretPattern = new(
        "(?i)([?&](?:access_token|refresh_token|token|api_key|apikey|client_secret|secret|password|graphEventId|changeKey|signature|sig|cookie|private_key|credential)=)[^&\\s#]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeaderSecretPattern = new(
        "(?i)\\b(authorization|x-pim-ops-key|x-api-key|x-goog-api-key|access[-_]?token|refresh[-_]?token|token|api[-_]?key|client[-_]?secret|graphEventId|changeKey|password|signature|secret|cookie|set-cookie|private[-_]?key|credential|credentials)\\b(\\s*[:=]\\s*)[^\\s,;}\\]]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerPattern = new(
        "(?i)\\b(bearer|basic)\\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KnownTokenPattern = new(
        "(?i)\\b(?:sk|rk|gsk|hf|xai)[-_][A-Za-z0-9_-]{16,}\\b|\\bAKIA[0-9A-Z]{16}\\b|\\bgh[pousr]_[A-Za-z0-9]{20,}\\b|\\bxox[baprs]-[A-Za-z0-9-]{10,}\\b|\\bAIza[0-9A-Za-z_-]{30,}\\b|\\beyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlCredentialsPattern = new(
        "(?i)(https?://)[^/@\\s:]+:[^/@\\s]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PrivateKeyPattern = new(
        "-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex[] PathPatterns =
    [
        new(@"(?i)\b[A-Z]:\\(?:Users\\[^\\\s]+\\AppData\\Local\\Temp|Windows\\Temp|Temp)\\[^\r\n\t""'<>|]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b[A-Z]:\\Users\\[^\\\s]+\\[^\r\n\t""'<>|]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\w)/(?:tmp|var/tmp|private/var/folders|var/folders)/[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\w)/(?:data/user/0|data/data|storage/emulated/0)/[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\w)/home/[^/\s]+/[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\w)/(?:data/pim|app|opt|var/log|root)(?:/[^\s""'<>]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\w)/Users/[^/\s]+/[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b[A-Z]:\\[^\r\n\t""'<>|]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\bfile:///[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.CultureInvariant)
    ];

    public static string RedactText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace("\0", string.Empty, StringComparison.Ordinal);
        text = JsonSecretPattern.Replace(text, $"$1\"{Marker}\"");
        text = QuerySecretPattern.Replace(text, $"$1{Marker}");
        text = BearerPattern.Replace(text, $"$1 {Marker}");
        text = HeaderSecretPattern.Replace(text, $"$1$2{Marker}");
        text = KnownTokenPattern.Replace(text, Marker);
        text = PrivateKeyPattern.Replace(text, Marker);
        text = UrlCredentialsPattern.Replace(text, $"$1{Marker}@");

        foreach (var pattern in PathPatterns)
        {
            text = pattern.Replace(text, PathMarker);
        }

        return Truncate(text.Trim(), maxLength);
    }

    public static IReadOnlyDictionary<string, string> RedactAttributes(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in attributes)
        {
            if (result.Count >= DiagnosticLimits.MaxAttributes)
            {
                break;
            }

            var key = Truncate(rawKey?.Trim() ?? string.Empty, 64);
            if (key.Length == 0 || IsSensitiveAttributeName(key))
            {
                continue;
            }

            if (!string.Equals(RedactText(key, 64), key, StringComparison.Ordinal))
            {
                continue;
            }

            result[key] = RedactText(rawValue, 512);
        }

        return result;
    }

    public static bool IsSensitiveAttributeName(string name)
    {
        if (SensitiveAttributeNames.Contains(name))
        {
            return true;
        }

        var normalized = new string(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("grapheventid", StringComparison.Ordinal)
            || normalized.Contains("changekey", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("signature", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
