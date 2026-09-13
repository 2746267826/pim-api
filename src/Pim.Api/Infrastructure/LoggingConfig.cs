namespace Pim.Api.Infrastructure;

public static class LoggingConfig
{
    public const int DefaultRetainedFileCount = 30;
    public const long DefaultFileSizeLimitBytes = 1073741824L; // 1 GiB
    public const bool DefaultRollOnFileSizeLimit = true;

    public static int ResolveRetainedFileCount(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultRetainedFileCount;
        if (int.TryParse(rawValue, out var parsed) && parsed >= 1)
            return parsed;
        return DefaultRetainedFileCount;
    }

    public static long? ResolveFileSizeLimitBytes(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultFileSizeLimitBytes;
        var trimmed = rawValue.Trim();
        if (trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
            return null;
        if (long.TryParse(trimmed, out var parsed) && parsed > 0)
            return parsed;
        return DefaultFileSizeLimitBytes;
    }

    public static bool ResolveRollOnFileSizeLimit(string? rawValue, long? fileSizeLimitBytes = DefaultFileSizeLimitBytes)
    {
        // Serilog requires fileSizeLimitBytes != null when rollOnFileSizeLimit is true.
        // If file size is unlimited (null), rolling on size limit is not applicable.
        if (fileSizeLimitBytes == null)
            return false;
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultRollOnFileSizeLimit;
        var trimmed = rawValue.Trim();
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
