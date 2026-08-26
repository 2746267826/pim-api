namespace Pim.Module.PcTracker.Services;

public static class AppNameNormalizer
{
    public static string Normalize(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return "unknown";

        var trimmed = appName.Trim();
        // 长度保护：超过256截断，避免后续 GroupBy 性能问题
        if (trimmed.Length > 256)
            trimmed = trimmed[..256];
        var normalized = trimmed.ToLowerInvariant();
        // 去除 .exe 后缀（大小写不敏感，已转小写）
        if (normalized.EndsWith(".exe", StringComparison.Ordinal))
            normalized = normalized[..^4];
        // 去除首尾空白后若为空则回退 unknown
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    /// <summary>
    /// 尝试归一化并返回是否成功（用于 Stryker 分支覆盖）
    /// 阈值：空/空白 => false, 否则 true
    /// </summary>
    public static bool TryNormalize(string? appName, out string normalized)
    {
        normalized = Normalize(appName);
        return !string.Equals(normalized, "unknown", StringComparison.Ordinal);
    }
}
