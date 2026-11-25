using System.Globalization;
using System.Text.RegularExpressions;

namespace Pim.Module.Mcp.Services;

internal static class McpTimeRange
{
    public const int MaxSpanDays = 366;
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(MaxSpanDays);

    private static readonly Regex DateOnlyPattern = new(
        @"^\d{4}-\d{2}-\d{2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DateTimePattern = new(
        @"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:[Zz]|[+-]\d{2}:?\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string value, out DateTimeOffset instant, out string error)
    {
        instant = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            error = $"invalid iso8601 '{value}'";
            return false;
        }

        if (DateOnlyPattern.IsMatch(value))
        {
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                error = $"invalid iso8601 '{value}'";
                return false;
            }

            instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return true;
        }

        if (!DateTimePattern.IsMatch(value))
        {
            error = $"invalid iso8601 '{value}'";
            return false;
        }

        var iso = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            ? value[..^1] + "+00:00"
            : value;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant))
        {
            error = $"invalid iso8601 '{value}'";
            return false;
        }

        return true;
    }

    public static string ToUtcQueryValue(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        var seconds = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fractionalTicks = utc.Ticks % TimeSpan.TicksPerSecond;
        if (fractionalTicks == 0)
            return seconds + "Z";

        var fraction = fractionalTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return $"{seconds}.{fraction}Z";
    }

    public static (DateTimeOffset Start, DateTimeOffset End) CenteredWindow(DateTimeOffset center)
    {
        var start = center.AddDays(-(MaxSpanDays / 2));
        return (start, start.Add(MaxSpan));
    }
}
