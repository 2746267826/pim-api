namespace Pim.Module.PcTracker.Services;

public static class AppNameNormalizer
{
    public static string Normalize(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return "unknown";

        var normalized = appName.Trim().ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.Ordinal)
            ? normalized[..^4]
            : normalized;
    }
}
