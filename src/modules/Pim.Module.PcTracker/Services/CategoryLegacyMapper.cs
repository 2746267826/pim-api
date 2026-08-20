namespace Pim.Module.PcTracker.Services;

public static class CategoryLegacyMapper
{
    public const string ProgrammingTinkering = "编程/折腾";
    public const string Learning = "学习";
    public const string Video = "视频";
    public const string Chat = "聊天";
    public const string Documents = "文档";
    public const string Gaming = "游戏";
    public const string Other = "其他";

    public static readonly string[] UnifiedCategoryNames =
        [ProgrammingTinkering, Learning, Video, Chat, Documents, Gaming, Other];

    public static readonly IReadOnlyDictionary<string, string> UnifiedColors =
        new Dictionary<string, string>
        {
            [ProgrammingTinkering] = "#6B5EE4",
            [Learning] = "#14b8a6",
            [Video] = "#F97316",
            [Chat] = "#3B82F6",
            [Documents] = "#F59E0B",
            [Gaming] = "#F43F5E",
            [Other] = "#64748b"
        };

    public static readonly IReadOnlyDictionary<string, string> UnifiedIcons =
        new Dictionary<string, string>
        {
            [ProgrammingTinkering] = "💻",
            [Learning] = "📚",
            [Video] = "📺",
            [Chat] = "💬",
            [Documents] = "📄",
            [Gaming] = "🎮",
            [Other] = "📋"
        };

    private static readonly IReadOnlyDictionary<string, string> LegacyToUnified =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["编程"] = ProgrammingTinkering,
            ["前端"] = ProgrammingTinkering,
            ["后端"] = ProgrammingTinkering,
            ["终端"] = ProgrammingTinkering,
            ["运维"] = ProgrammingTinkering,
            ["设计"] = ProgrammingTinkering,
            ["技术学习"] = Learning,
            ["外语学习"] = Learning,
            ["阅读"] = Learning,
            ["视频"] = Video,
            ["沟通"] = Chat,
            ["即时消息"] = Chat,
            ["邮件"] = Chat,
            ["社交"] = Chat,
            ["会议"] = Chat,
            ["文档"] = Documents,
            ["办公"] = Documents,
            ["文件"] = Documents,
            ["浏览"] = Documents,
            ["游戏"] = Gaming,
            ["单机游戏"] = Gaming,
            ["网络游戏"] = Gaming
        };

    /// <summary>旧分类名 → 统一 7 大类名。未知/空值 → 其他。</summary>
    public static string MapToUnified(string? legacy)
    {
        if (string.IsNullOrWhiteSpace(legacy))
            return Other;
        var trimmed = legacy.Trim();
        // 已是统一大类名（幂等），直接返回；否则查旧名映射
        if (Array.IndexOf(UnifiedCategoryNames, trimmed) >= 0)
            return trimmed;
        return LegacyToUnified.TryGetValue(trimmed, out var unified) ? unified : Other;
    }
}
