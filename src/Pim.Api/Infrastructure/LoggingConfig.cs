namespace Pim.Api.Infrastructure;

public static class LoggingConfig
{
    public const int DefaultRetainedFileCount = 30;

    public static int ResolveRetainedFileCount(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultRetainedFileCount;
        if (int.TryParse(rawValue, out var parsed) && parsed >= 1)
            return parsed;
        return DefaultRetainedFileCount;
    }
}
