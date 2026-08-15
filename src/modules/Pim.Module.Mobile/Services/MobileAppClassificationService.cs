using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileAppClassificationService
{
    public const string RuleTypePackageExact = "package-exact";
    public const string RuleTypePackagePrefix = "package-prefix";
    public const string RuleTypeKeyword = "keyword";
    public const string RuleTypeDisplayKeyword = "display-keyword";
    public const string RuleTypePackageKeyword = "package-keyword";

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public MobileAppClassificationService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public Task<MobileAppClassificationResult> ClassifyAsync(
        string packageName,
        CancellationToken ct = default)
        => ClassifyAsync(new MobileAppClassificationInput(packageName), ct);

    public async Task<MobileAppClassificationResult> ClassifyAsync(
        MobileAppClassificationInput input,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var packageName = NormalizePackageName(input.PackageName);
        var latestMetadata = await LoadLatestMetadataAsync(userId, packageName, ct);
        var displayName = ResolveDisplayName(null, latestMetadata, input, packageName);

        var overrideEntity = await _db.Set<MobileAppCatalogOverrideEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.UserId == userId && o.PackageName == packageName, ct);

        if (overrideEntity is not null)
        {
            return new MobileAppClassificationResult(
                packageName,
                ResolveDisplayName(overrideEntity.DisplayNameOverride, latestMetadata, input, packageName),
                NormalizeLifeCategory(overrideEntity.LifeCategory),
                overrideEntity.IsSystemNoise,
                overrideEntity.HideShortEvents,
                "user-override",
                latestMetadata is not null);
        }

        var userRules = await LoadEnabledRulesAsync(userId, ct);
        if (TryClassifyWithRules(packageName, displayName, userRules, out var ruleResult))
            return BuildRuleResult(packageName, displayName, latestMetadata, input, ruleResult);

        var androidCategory = FirstNonBlank(input.AndroidCategory, latestMetadata?.Category);
        var installerPackage = FirstNonBlank(input.InstallerPackage, latestMetadata?.InstallerPackage);
        var isSystemApp = input.IsSystemApp ?? latestMetadata?.IsSystemApp ?? false;
        var systemNoise = DetectSystemNoise(packageName, androidCategory, isSystemApp);

        if (TryMapAndroidCategory(androidCategory, installerPackage, out var androidLifeCategory))
        {
            return new MobileAppClassificationResult(
                packageName,
                displayName,
                systemNoise ? MobileLifeCategories.ToolsSystem : androidLifeCategory,
                systemNoise,
                systemNoise,
                "android-metadata",
                latestMetadata is not null);
        }

        if (TryClassifyBuiltInPackage(packageName, out var builtInPackage))
            return BuildBuiltInResult(packageName, displayName, builtInPackage, systemNoise, "built-in-package");

        if (TryClassifyBuiltInPrefix(packageName, out var builtInPrefix))
            return BuildBuiltInResult(packageName, displayName, builtInPrefix, systemNoise, "built-in-prefix");

        if (TryClassifyBuiltInKeyword(packageName, displayName, out var builtInKeyword))
            return BuildBuiltInResult(packageName, displayName, builtInKeyword, systemNoise, "built-in-keyword");

        if (TryMapRawMetadata(latestMetadata?.RawJson, out var rawMetadataCategory))
        {
            return new MobileAppClassificationResult(
                packageName,
                displayName,
                systemNoise ? MobileLifeCategories.ToolsSystem : rawMetadataCategory,
                systemNoise,
                systemNoise,
                "android-metadata",
                latestMetadata is not null);
        }

        if (systemNoise)
        {
            return new MobileAppClassificationResult(
                packageName,
                displayName,
                MobileLifeCategories.ToolsSystem,
                true,
                true,
                "built-in-system-noise",
                latestMetadata is not null);
        }

        return new MobileAppClassificationResult(
            packageName,
            displayName,
            MobileLifeCategories.Uncategorized,
            false,
            false,
            "fallback",
            latestMetadata is not null);
    }

    private async Task<MobileAppCatalogEntity?> LoadLatestMetadataAsync(
        Guid userId,
        string packageName,
        CancellationToken ct)
        => await _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(app => app.UserId == userId && app.PackageName == packageName)
            .OrderByDescending(app => app.UpdatedAt)
            .ThenByDescending(app => app.LastUpdateTimeUtc)
            .ThenByDescending(app => app.CreatedAt)
            .ThenBy(app => app.DeviceId)
            .FirstOrDefaultAsync(ct);

    private async Task<List<MobileAppCategoryRuleEntity>> LoadEnabledRulesAsync(Guid userId, CancellationToken ct)
        => await _db.Set<MobileAppCategoryRuleEntity>()
            .AsNoTracking()
            .Where(rule => rule.UserId == userId && rule.IsEnabled)
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.Pattern)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);

    private static bool TryClassifyWithRules(
        string packageName,
        string displayName,
        IReadOnlyList<MobileAppCategoryRuleEntity> rules,
        out MobileAppCategoryRuleEntity matchedRule)
    {
        foreach (var ruleType in UserRuleOrder)
        {
            foreach (var rule in rules.Where(rule => string.Equals(rule.RuleType, ruleType, StringComparison.OrdinalIgnoreCase)))
            {
                if (!RuleMatches(rule, packageName, displayName))
                    continue;

                matchedRule = rule;
                return true;
            }
        }

        matchedRule = new MobileAppCategoryRuleEntity();
        return false;
    }

    private MobileAppClassificationResult BuildRuleResult(
        string packageName,
        string displayName,
        MobileAppCatalogEntity? latestMetadata,
        MobileAppClassificationInput input,
        MobileAppCategoryRuleEntity rule)
    {
        var androidCategory = FirstNonBlank(input.AndroidCategory, latestMetadata?.Category);
        var isSystemNoise = rule.IsSystemNoise
            ?? DetectSystemNoise(packageName, androidCategory, input.IsSystemApp ?? latestMetadata?.IsSystemApp ?? false);

        return new MobileAppClassificationResult(
            packageName,
            FirstNonBlank(rule.DisplayNameOverride, displayName) ?? packageName,
            NormalizeLifeCategory(rule.LifeCategory),
            isSystemNoise,
            isSystemNoise,
            $"user-rule:{NormalizeRuleType(rule.RuleType)}",
            latestMetadata is not null);
    }

    private static MobileAppClassificationResult BuildBuiltInResult(
        string packageName,
        string displayName,
        BuiltInClassification classification,
        bool systemNoise,
        string source)
    {
        var lifeCategory = systemNoise ? MobileLifeCategories.ToolsSystem : classification.LifeCategory;
        return new MobileAppClassificationResult(
            packageName,
            displayName,
            lifeCategory,
            systemNoise || classification.IsSystemNoise,
            systemNoise || classification.IsSystemNoise,
            source);
    }

    private static bool RuleMatches(MobileAppCategoryRuleEntity rule, string packageName, string displayName)
    {
        var pattern = NormalizePattern(rule.Pattern);
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        return NormalizeRuleType(rule.RuleType) switch
        {
            RuleTypePackageExact => string.Equals(packageName, NormalizePackageName(pattern), StringComparison.Ordinal),
            RuleTypePackagePrefix => packageName.StartsWith(NormalizePackageName(pattern), StringComparison.Ordinal),
            RuleTypeDisplayKeyword => ContainsIgnoreCase(displayName, pattern),
            RuleTypePackageKeyword => ContainsIgnoreCase(packageName, pattern),
            RuleTypeKeyword => ContainsIgnoreCase(packageName, pattern) || ContainsIgnoreCase(displayName, pattern),
            _ => false
        };
    }

    private static string ResolveDisplayName(
        string? overrideDisplayName,
        MobileAppCatalogEntity? latestMetadata,
        MobileAppClassificationInput input,
        string packageName)
        => FirstNonBlank(
            overrideDisplayName,
            latestMetadata?.DisplayName,
            input.DisplayName,
            BuiltInFriendlyNames.GetValueOrDefault(packageName),
            packageName) ?? packageName;

    private static bool TryMapAndroidCategory(
        string? category,
        string? installerPackage,
        out string lifeCategory)
    {
        var normalizedCategory = NormalizePattern(category);
        if (!string.IsNullOrWhiteSpace(normalizedCategory)
            && AndroidCategoryMap.TryGetValue(normalizedCategory, out lifeCategory!))
            return true;

        var normalizedInstaller = NormalizePackageNameOrEmpty(installerPackage);
        if (InstallerPackageMap.TryGetValue(normalizedInstaller, out lifeCategory!))
            return true;

        lifeCategory = MobileLifeCategories.Uncategorized;
        return false;
    }

    private static bool TryMapRawMetadata(string? rawJson, out string lifeCategory)
    {
        lifeCategory = MobileLifeCategories.Uncategorized;
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson == "{}")
            return false;

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            foreach (var propertyName in RawMetadataCategoryPropertyNames)
            {
                if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                    continue;

                return TryMapAndroidCategory(property.GetString(), null, out lifeCategory);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryClassifyBuiltInPackage(string packageName, out BuiltInClassification classification)
        => BuiltInPackageRules.TryGetValue(packageName, out classification);

    private static bool TryClassifyBuiltInPrefix(string packageName, out BuiltInClassification classification)
    {
        foreach (var rule in BuiltInPrefixRules)
        {
            if (!packageName.StartsWith(rule.Prefix, StringComparison.Ordinal))
                continue;

            classification = rule.Classification;
            return true;
        }

        classification = default;
        return false;
    }

    private static bool TryClassifyBuiltInKeyword(
        string packageName,
        string displayName,
        out BuiltInClassification classification)
    {
        foreach (var rule in BuiltInKeywordRules)
        {
            if (!ContainsIgnoreCase(packageName, rule.Keyword) && !ContainsIgnoreCase(displayName, rule.Keyword))
                continue;

            classification = rule.Classification;
            return true;
        }

        classification = default;
        return false;
    }

    private static bool DetectSystemNoise(string packageName, string? androidCategory, bool isSystemApp)
    {
        var normalizedCategory = NormalizePattern(androidCategory);
        if (normalizedCategory is "launcher" or "home" or "input" or "ime" or "keyboard" or "system")
            return true;

        if (SystemNoiseExactPackages.Contains(packageName))
            return true;

        if (SystemNoisePackageSubstrings.Any(value => packageName.Contains(value, StringComparison.Ordinal)))
            return true;

        if (SystemNoisePrefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.Ordinal)))
            return true;

        return isSystemApp && AndroidSystemPrefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string NormalizePackageName(string value)
    {
        var normalized = NormalizePackageNameOrEmpty(value);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Package name is required.", nameof(value));

        return normalized;
    }

    private static string NormalizePackageNameOrEmpty(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizePattern(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeRuleType(string? value)
        => NormalizePattern(value);

    /// <summary>分类名归一化：null/空串 → Uncategorized；否则原值保留
    /// （All 集合、ToolsSystem、任意自定义分类名均原样返回，不兜底「其他」，
    /// 否则自定义分类打 mobile_app 会在读取时静默失效）。</summary>
    private static string NormalizeLifeCategory(string? lifeCategory)
        => string.IsNullOrWhiteSpace(lifeCategory)
            ? MobileLifeCategories.Uncategorized
            : lifeCategory!;

    private static bool ContainsIgnoreCase(string value, string pattern)
        => value.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonBlank(params string?[] values)
        => values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static readonly string[] UserRuleOrder =
    [
        RuleTypePackageExact,
        RuleTypePackagePrefix,
        RuleTypeKeyword,
        RuleTypeDisplayKeyword,
        RuleTypePackageKeyword
    ];

    private static readonly Dictionary<string, string> BuiltInFriendlyNames = new(StringComparer.Ordinal)
    {
        ["com.tencent.mm"] = "微信",
        ["com.tencent.mobileqq"] = "QQ",
        ["com.eg.android.alipaygphone"] = "支付宝",
        ["com.taobao.taobao"] = "淘宝",
        ["com.jingdong.app.mall"] = "京东",
        ["com.ss.android.ugc.aweme"] = "抖音",
        ["com.smile.gifmaker"] = "快手",
        ["tv.danmaku.bili"] = "哔哩哔哩",
        ["com.netease.cloudmusic"] = "网易云音乐",
        ["com.tencent.qqmusic"] = "QQ音乐",
        ["com.autonavi.minimap"] = "高德地图",
        ["com.baidu.baidumap"] = "百度地图"
    };

    private static readonly Dictionary<string, BuiltInClassification> BuiltInPackageRules = new(StringComparer.Ordinal)
    {
        ["com.tencent.mm"] = new(MobileLifeCategories.Chat),
        ["com.tencent.mobileqq"] = new(MobileLifeCategories.Chat),
        ["com.sina.weibo"] = new(MobileLifeCategories.Chat),
        ["com.eg.android.alipaygphone"] = new(MobileLifeCategories.Other),
        ["com.tencent.mm.plugin.brandservice"] = new(MobileLifeCategories.Chat),
        ["com.taobao.taobao"] = new(MobileLifeCategories.Other),
        ["com.jingdong.app.mall"] = new(MobileLifeCategories.Other),
        ["com.sankuai.meituan"] = new(MobileLifeCategories.Other),
        ["me.ele"] = new(MobileLifeCategories.Other),
        ["com.ss.android.ugc.aweme"] = new(MobileLifeCategories.Video),
        ["com.smile.gifmaker"] = new(MobileLifeCategories.Video),
        ["tv.danmaku.bili"] = new(MobileLifeCategories.Video),
        ["com.netease.cloudmusic"] = new(MobileLifeCategories.Other),
        ["com.tencent.qqmusic"] = new(MobileLifeCategories.Other),
        ["com.zhihu.android"] = new(MobileLifeCategories.Learning),
        ["com.youdao.dict"] = new(MobileLifeCategories.Learning),
        ["com.autonavi.minimap"] = new(MobileLifeCategories.Other),
        ["com.baidu.baidumap"] = new(MobileLifeCategories.Other),
        ["com.android.systemui"] = new(MobileLifeCategories.ToolsSystem, IsSystemNoise: true),
        ["com.android.launcher"] = new(MobileLifeCategories.ToolsSystem, IsSystemNoise: true),
        ["com.google.android.inputmethod.latin"] = new(MobileLifeCategories.ToolsSystem, IsSystemNoise: true),
        ["com.android.quicksearchbox"] = new(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)
    };

    private static readonly (string Prefix, BuiltInClassification Classification)[] BuiltInPrefixRules =
    [
        ("com.tencent.tim", new BuiltInClassification(MobileLifeCategories.Chat)),
        ("com.android.", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)),
        ("android.", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)),
        ("com.miui.home", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)),
        ("com.huawei.android.launcher", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)),
        ("com.oppo.launcher", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true))
    ];

    private static readonly (string Keyword, BuiltInClassification Classification)[] BuiltInKeywordRules =
    [
        ("browser", new BuiltInClassification(MobileLifeCategories.Other)),
        ("search", new BuiltInClassification(MobileLifeCategories.Other)),
        ("map", new BuiltInClassification(MobileLifeCategories.Other)),
        ("music", new BuiltInClassification(MobileLifeCategories.Other)),
        ("camera", new BuiltInClassification(MobileLifeCategories.Other)),
        ("pay", new BuiltInClassification(MobileLifeCategories.Other)),
        ("game", new BuiltInClassification(MobileLifeCategories.Game)),
        ("launcher", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)),
        ("inputmethod", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true)),
        ("keyboard", new BuiltInClassification(MobileLifeCategories.ToolsSystem, IsSystemNoise: true))
    ];

    private static readonly Dictionary<string, string> AndroidCategoryMap = new(StringComparer.Ordinal)
    {
        ["0"] = MobileLifeCategories.Game,
        ["1"] = MobileLifeCategories.Other,
        ["2"] = MobileLifeCategories.Video,
        ["3"] = MobileLifeCategories.Other,
        ["4"] = MobileLifeCategories.Chat,
        ["5"] = MobileLifeCategories.Learning,
        ["6"] = MobileLifeCategories.Other,
        ["7"] = MobileLifeCategories.Documents,
        ["communication"] = MobileLifeCategories.Chat,
        ["social"] = MobileLifeCategories.Chat,
        ["video"] = MobileLifeCategories.Video,
        ["entertainment"] = MobileLifeCategories.Video,
        ["game"] = MobileLifeCategories.Game,
        ["games"] = MobileLifeCategories.Game,
        ["audio"] = MobileLifeCategories.Other,
        ["music"] = MobileLifeCategories.Other,
        ["news"] = MobileLifeCategories.Learning,
        ["magazines"] = MobileLifeCategories.Learning,
        ["books"] = MobileLifeCategories.Learning,
        ["education"] = MobileLifeCategories.Learning,
        ["learning"] = MobileLifeCategories.Learning,
        ["productivity"] = MobileLifeCategories.Documents,
        ["business"] = MobileLifeCategories.Documents,
        ["tools"] = MobileLifeCategories.ToolsSystem,
        ["system"] = MobileLifeCategories.ToolsSystem,
        ["launcher"] = MobileLifeCategories.ToolsSystem,
        ["browser"] = MobileLifeCategories.Other,
        ["maps"] = MobileLifeCategories.Other,
        ["navigation"] = MobileLifeCategories.Other,
        ["travel"] = MobileLifeCategories.Other,
        ["shopping"] = MobileLifeCategories.Other,
        ["food"] = MobileLifeCategories.Other,
        ["finance"] = MobileLifeCategories.Other,
        ["payment"] = MobileLifeCategories.Other,
        ["health"] = MobileLifeCategories.Other,
        ["fitness"] = MobileLifeCategories.Other,
        ["photography"] = MobileLifeCategories.Other,
        ["camera"] = MobileLifeCategories.Other,
        ["lifestyle"] = MobileLifeCategories.Other
    };

    private static readonly Dictionary<string, string> InstallerPackageMap = new(StringComparer.Ordinal)
    {
        ["com.android.packageinstaller"] = MobileLifeCategories.ToolsSystem,
        ["com.google.android.packageinstaller"] = MobileLifeCategories.ToolsSystem,
        ["com.google.android.permissioncontroller"] = MobileLifeCategories.ToolsSystem
    };

    private static readonly string[] RawMetadataCategoryPropertyNames =
    [
        "category",
        "appCategory",
        "applicationCategory",
        "playCategory"
    ];

    private static readonly HashSet<string> SystemNoiseExactPackages = new(StringComparer.Ordinal)
    {
        "android",
        "com.android.systemui",
        "com.android.launcher",
        "com.android.launcher3",
        "com.android.quicksearchbox",
        "com.google.android.googlequicksearchbox",
        "com.google.android.inputmethod.latin",
        "com.google.android.packageinstaller",
        "com.google.android.permissioncontroller",
        "com.miui.home",
        "com.huawei.android.launcher",
        "com.oppo.launcher",
        "com.bbk.launcher2",
        "net.oneplus.launcher"
    };

    private static readonly string[] SystemNoisePrefixes =
    [
        "com.android.inputmethod",
        "com.google.android.inputmethod",
        "com.sohu.inputmethod",
        "com.baidu.input",
        "com.iflytek.inputmethod"
    ];

    private static readonly string[] SystemNoisePackageSubstrings =
    [
        "launcher",
        "inputmethod",
        ".ime",
        "keyboard",
        "quicksearchbox",
        "systemui"
    ];

    private static readonly string[] AndroidSystemPrefixes =
    [
        "android",
        "com.android.",
        "com.google.android.gms",
        "com.google.android.ext",
        "com.google.android.packageinstaller",
        "com.google.android.permissioncontroller"
    ];

    private readonly record struct BuiltInClassification(string LifeCategory, bool IsSystemNoise = false);
}

public sealed record MobileAppClassificationInput(
    string PackageName,
    string? DisplayName = null,
    string? AndroidCategory = null,
    string? InstallerPackage = null,
    bool? IsSystemApp = null);

public sealed record MobileAppClassificationResult(
    string PackageName,
    string DisplayName,
    string LifeCategory,
    bool IsSystemNoise,
    bool HideShortEvents,
    string Source,
    bool HasMetadata = false);
