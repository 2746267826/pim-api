namespace Pim.Module.PcTracker.Services;

/// <summary>pc_app_signatures 显示名三段式匹配（精确 → 补 .exe → glob 通配正则），
/// 供 ActivityLabelingService 与 PcActivityAggregationService 共用（原实现见阶段 1
/// ActivityLabelingService.ResolveDisplayNamesAsync）。</summary>
public static class AppSignatureMatcher
{
    /// <summary>对每个 app（AppNameNormalized 形态）批量解析显示名；无命中则不写入结果。
    /// signatures 为 pc_app_signatures 的 (ProcessName, DisplayName) 行，DisplayName 为空时回退 ProcessName。</summary>
    public static Dictionary<string, string> ResolveDisplayNames(
        IReadOnlyList<string> apps,
        IEnumerable<(string ProcessName, string DisplayName)> signatures)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (apps.Count == 0)
            return names;

        var signatureList = signatures
            .Select(s => (
                ProcessName: s.ProcessName ?? string.Empty,
                DisplayName: (string.IsNullOrWhiteSpace(s.DisplayName) ? s.ProcessName : s.DisplayName) ?? string.Empty))
            .ToList();

        foreach (var app in apps)
        {
            var normalized = app.ToLowerInvariant();

            // 1) 精确匹配（大小写不敏感）
            var signature = signatureList.FirstOrDefault(s => s.ProcessName.ToLowerInvariant() == normalized);

            // 2) 补 .exe 后缀匹配
            if (signature.ProcessName is null && !normalized.EndsWith(".exe", StringComparison.Ordinal))
                signature = signatureList.FirstOrDefault(s => s.ProcessName.ToLowerInvariant() == normalized + ".exe");

            // 3) glob 通配正则匹配（如 MobaXterm*.exe）
            if (signature.ProcessName is null)
            {
                foreach (var candidateName in new[] { normalized, normalized + ".exe" })
                {
                    signature = signatureList.FirstOrDefault(s =>
                    {
                        var pattern = s.ProcessName;
                        if (!pattern.Contains('*') && !pattern.Contains('?'))
                            return false;
                        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                            .Replace("\\*", ".*")
                            .Replace("\\?", ".") + "$";
                        return System.Text.RegularExpressions.Regex.IsMatch(candidateName, regex,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    });
                    if (signature.ProcessName is not null)
                        break;
                }
            }

            if (signature.ProcessName is not null)
                names[app] = signature.DisplayName;
        }

        return names;
    }
}
