using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Microsoft.Extensions.Logging;

namespace Pim.Module.PcTracker.Services;

public static class ActivityClassifier
{
    private const int DeferredRulePriorityThreshold = 100;
    private const double DeferredRuleConfidenceThreshold = 0.65;
    private const string ProgrammingCategory = "\u7f16\u7a0b";
    private const string ProgrammingColor = "#6B5EE4";
    private const string LearningCategory = "\u5b66\u4e60";
    private const string LearningColor = "#14b8a6";
    private const string TerminalCategory = "\u7ec8\u7aef";
    private const string TerminalColor = "#E05A7A";
    private const string CommunicationCategory = "\u6c9f\u901a";
    private const string CommunicationColor = "#F5935A";
    private const string OfficeCategory = "\u529e\u516c";
    private const string OfficeColor = "#F59E0B";
    private const string FilesCategory = "\u6587\u4ef6";
    private const string FilesColor = "#3B82F6";
    private const string EntertainmentCategory = "\u5a31\u4e50";
    private const string EntertainmentColor = "#EC4899";

    public static ActivityClassificationResult Classify(
        ActivityClassificationContext context,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        ILogger? logger = null)
    {
        var activeRules = (rules ?? Array.Empty<ActivityCategoryRuleEntity>())
            .Where(rule => string.Equals(rule.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Where(CanClassifyActivity)
            .OrderByDescending(rule => rule.Priority)
            .ToArray();

        if (TryClassifyWithRules(context, activeRules.Where(rule => !IsDeferredFallbackRule(rule)), out var result, logger))
            return result;

        var heuristicResult = ClassifyWithHeuristics(context);
        if (heuristicResult is not null)
            return heuristicResult;

        return TryClassifyWithRules(context, activeRules.Where(IsDeferredFallbackRule), out result, logger)
            ? result
            : ActivityClassificationResult.Fallback();
    }

    private static bool TryClassifyWithRules(
        ActivityClassificationContext context,
        IEnumerable<ActivityCategoryRuleEntity> rules,
        out ActivityClassificationResult result,
        ILogger? logger = null)
    {
        foreach (var rule in rules)
        {
            if (!ActivityClassificationRuleEvaluator.Matches(rule.ConditionsJson, context, logger))
                continue;

            result = new ActivityClassificationResult(
                string.IsNullOrWhiteSpace(rule.CategoryName)
                    ? ActivityClassificationResult.Fallback().CategoryName
                    : rule.CategoryName,
                rule.Color,
                rule.ProjectTag,
                rule.Confidence,
                "rule",
                rule.Explanation ?? string.Empty,
                rule.Id);
            return true;
        }

        result = ActivityClassificationResult.Fallback();
        return false;
    }

    private static bool IsDeferredFallbackRule(ActivityCategoryRuleEntity rule)
    {
        return string.Equals(rule.Source, "builtin", StringComparison.OrdinalIgnoreCase)
            && rule.Priority <= DeferredRulePriorityThreshold
            && rule.Confidence <= DeferredRuleConfidenceThreshold;
    }

    private static bool CanClassifyActivity(ActivityCategoryRuleEntity rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Scope))
            return true;

        return string.Equals(rule.Scope, "activity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Scope, "both", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Scope, "app", StringComparison.OrdinalIgnoreCase);
    }

    private static ActivityClassificationResult? ClassifyWithHeuristics(ActivityClassificationContext context)
    {
        var domain = NormalizeDomain(context.Domain);
        if (IsDocumentationSignal(domain, context.UrlPath, context.Title, context.WindowTitle))
        {
            return new ActivityClassificationResult(
                LearningCategory,
                LearningColor,
                InferDocumentationProjectTag(domain, context.Title, context.WindowTitle),
                0.72,
                "heuristic",
                "Documentation or API activity.");
        }

        if (IsCodeHostingDomain(domain))
        {
            return Programming(
                DeriveRepositoryProjectTag(context.UrlPath),
                0.82,
                "Code hosting activity.");
        }

        if (IsLocalhost(domain))
            return Programming(null, 0.8, "Local development activity.");

        var title = JoinForSearch(context.Title, context.WindowTitle);
        if (ContainsAny(title, MeetingTitleSignals))
            return Communication(0.78, "Meeting, calendar, or mail activity.");

        var appName = AppNameNormalizer.Normalize(context.AppNameNormalized ?? context.AppName);
        if (ContainsAny(appName, CodingApps))
            return Programming(null, 0.78, "Coding app activity.");

        if (ContainsAny(appName, TerminalApps))
        {
            return new ActivityClassificationResult(
                TerminalCategory,
                TerminalColor,
                null,
                0.75,
                "heuristic",
                "Terminal app activity.");
        }

        if (ContainsAny(appName, CommunicationApps))
            return Communication(0.74, "Communication app activity.");

        if (ContainsAny(appName, OfficeApps))
        {
            return new ActivityClassificationResult(
                OfficeCategory,
                OfficeColor,
                null,
                0.72,
                "heuristic",
                "Office app activity.");
        }

        if (ContainsAny(appName, FileApps))
        {
            return new ActivityClassificationResult(
                FilesCategory,
                FilesColor,
                null,
                0.72,
                "heuristic",
                "File manager activity.");
        }

        if (ContainsAny(appName, EntertainmentApps))
        {
            return new ActivityClassificationResult(
                EntertainmentCategory,
                EntertainmentColor,
                null,
                0.7,
                "heuristic",
                "Entertainment app activity.");
        }

        return null;
    }

    private static ActivityClassificationResult Programming(string? projectTag, double confidence, string explanation) =>
        new(
            ProgrammingCategory,
            ProgrammingColor,
            projectTag,
            confidence,
            "heuristic",
            explanation);

    private static ActivityClassificationResult Communication(double confidence, string explanation) =>
        new(
            CommunicationCategory,
            CommunicationColor,
            null,
            confidence,
            "heuristic",
            explanation);

    private static string? DeriveRepositoryProjectTag(string? urlPath)
    {
        if (string.IsNullOrWhiteSpace(urlPath))
            return null;

        var path = urlPath.Split(['?', '#'], 2)[0];
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length >= 2 ? segments[1] : null;
    }

    private static string? InferDocumentationProjectTag(string domain, params string?[] textParts)
    {
        if (domain.Equals("docs.activitywatch.net", StringComparison.OrdinalIgnoreCase))
            return "ActivityWatch";

        var text = JoinForSearch(textParts);
        return text.Contains("activitywatch", StringComparison.OrdinalIgnoreCase)
            ? "ActivityWatch"
            : null;
    }

    private static string NormalizeDomain(string? domain) =>
        (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsCodeHostingDomain(string domain) =>
        domain is "github.com" or "www.github.com" or "gitlab.com" or "www.gitlab.com"
        || domain.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith(".gitlab.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalhost(string domain) =>
        domain is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    private static bool IsDocumentationSignal(string domain, string? urlPath, string? title, string? windowTitle)
    {
        if (domain.StartsWith("docs.", StringComparison.OrdinalIgnoreCase)
            || domain.Contains(".readthedocs.", StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith(".readthedocs.io", StringComparison.OrdinalIgnoreCase)
            || domain.Contains("developer.", StringComparison.OrdinalIgnoreCase)
            || domain.Contains("developers.", StringComparison.OrdinalIgnoreCase))
            return true;

        var searchable = JoinForSearch(urlPath, title, windowTitle);
        return ContainsAny(searchable, DocumentationSignals);
    }

    private static bool ContainsAny(string value, IReadOnlyCollection<string> needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string JoinForSearch(params string?[] values) =>
        string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static readonly string[] DocumentationSignals =
    [
        " docs",
        "documentation",
        "api reference",
        "rest api",
        "/docs",
        "/api/",
        "developer guide",
        "manual"
    ];

    private static readonly string[] MeetingTitleSignals =
    [
        "meeting",
        "calendar",
        "mail",
        "email",
        "inbox",
        "outlook",
        "gmail",
        "zoom",
        "teams meeting"
    ];

    private static readonly string[] CodingApps =
    [
        "code",
        "visual studio",
        "rider",
        "devenv",
        "idea",
        "webstorm",
        "pycharm",
        "phpstorm",
        "goland",
        "clion",
        "cursor",
        "codex",
        "zed"
    ];

    private static readonly string[] TerminalApps =
    [
        "windowsterminal",
        "terminal",
        "powershell",
        "pwsh",
        "cmd",
        "conhost",
        "wezterm",
        "alacritty",
        "mintty"
    ];

    private static readonly string[] CommunicationApps =
    [
        "wechat",
        "dingtalk",
        "qq",
        "telegram",
        "slack",
        "discord",
        "teams",
        "outlook",
        "thunderbird",
        "zoom"
    ];

    private static readonly string[] OfficeApps =
    [
        "winword",
        "word",
        "excel",
        "powerpnt",
        "powerpoint",
        "notion",
        "obsidian",
        "typora",
        "onenote"
    ];

    private static readonly string[] FileApps =
    [
        "explorer",
        "everything",
        "totalcommander",
        "files",
        "directory opus"
    ];

    private static readonly string[] EntertainmentApps =
    [
        "spotify",
        "netflix",
        "youtube",
        "steam",
        "vlc",
        "potplayer",
        "bilibili",
        "music"
    ];
}
