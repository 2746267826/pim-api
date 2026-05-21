using System.Text.RegularExpressions;

namespace Pim.Module.PcTracker.Services;

public static partial class ActivityUrlSanitizer
{
    public static string? Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => LooksSensitive(segment) ? "[redacted]" : segment);

        builder.Path = string.Join('/', segments);
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool LooksSensitive(string segment)
    {
        var decoded = Uri.UnescapeDataString(segment);
        return decoded.Length >= 24
            && (OpaqueTokenRegex().IsMatch(decoded) || decoded.Count(char.IsDigit) >= 8);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{24,}$")]
    private static partial Regex OpaqueTokenRegex();
}
