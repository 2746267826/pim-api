using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

/// <summary>打标队列与提交：把未分类的应用/域名目标写入映射与规则表。</summary>
public sealed class ActivityLabelingService
{
    /// <summary>队列候选的最小时长（秒）。</summary>
    public const double MinimumCandidateDurationSeconds = 10 * 60;

    private readonly PimDbContext _db;

    public ActivityLabelingService(PimDbContext db)
    {
        _db = db;
    }

    // ---------- 提交 ----------

    /// <summary>提交打标。无 userId 时（如测试）mobile_app 规则以 Guid.Empty 写入。</summary>
    public Task<ActivityLabelingResponse> LabelAsync(ActivityLabelingRequest req, CancellationToken ct)
        => LabelAsync(req, null, ct);

    public async Task<ActivityLabelingResponse> LabelAsync(
        ActivityLabelingRequest req,
        Guid? userId,
        CancellationToken ct)
    {
        var targetType = NormalizeTargetType(req.TargetType);
        var target = NormalizeTarget(req.Target);
        var scope = NormalizeScope(req.Scope);

        if (targetType == "mobile_app" && scope == "keyword")
            throw new ArgumentException("手机应用不支持关键词情境规则。", nameof(req));

        var (categoryId, categoryName) = await ResolveCategoryAsync(req, ct);

        var created = targetType switch
        {
            "app" when scope == "all" => await LabelAppAsync(target, categoryName, categoryId, ct),
            "app" => await LabelAppKeywordAsync(target, categoryName, categoryId, req.Keyword, ct),
            "domain" when scope == "all" => await LabelDomainAsync(target, categoryName, categoryId, null, ct),
            "domain" => await LabelDomainAsync(target, categoryName, categoryId, req.Keyword, ct),
            "mobile_app" => await LabelMobileAppAsync(target, categoryName, userId, ct),
            _ => throw new ArgumentException($"不支持的目标类型：{req.TargetType}。", nameof(req))
        };

        return new ActivityLabelingResponse(true, categoryId, categoryName, created);
    }

    private async Task<(Guid CategoryId, string CategoryName)> ResolveCategoryAsync(
        ActivityLabelingRequest req,
        CancellationToken ct)
    {
        if (req.CategoryId is Guid categoryId)
        {
            var name = await _db.Set<PcCategoryEntity>()
                .Where(c => c.Id == categoryId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
            if (name is null)
                throw new ArgumentException($"分类（{categoryId}）不存在。", nameof(req));
            return (categoryId, name);
        }

        var categoryName = req.CategoryName?.Trim();
        if (string.IsNullOrWhiteSpace(categoryName))
            throw new ArgumentException("category_id 与 category_name 不能同时为空。", nameof(req));

        var existing = await _db.Set<PcCategoryEntity>()
            .FirstOrDefaultAsync(c => c.Name == categoryName, ct);
        if (existing is not null)
            return (existing.Id, existing.Name);

        // 自定义分类自动创建（IsBuiltin=false）
        var custom = new PcCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = categoryName,
            Color = "#64748b",
            Productivity = "neutral",
            SortOrder = 1000,
            IsBuiltin = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Set<PcCategoryEntity>().Add(custom);
        await _db.SaveChangesAsync(ct);
        return (custom.Id, custom.Name);
    }

    private async Task<string> LabelAppAsync(
        string target,
        string categoryName,
        Guid categoryId,
        CancellationToken ct)
    {
        var category = await LoadCategoryColorAsync(categoryId, ct);
        var mapping = await _db.Set<AppCategoryEntity>()
            .FirstOrDefaultAsync(e => e.AppPattern == target, ct);
        if (mapping is null)
        {
            mapping = new AppCategoryEntity
            {
                Id = Guid.NewGuid(),
                AppPattern = target,
                IsBuiltin = false,
                CreatedAt = DateTime.UtcNow
            };
            _db.Set<AppCategoryEntity>().Add(mapping);
        }

        mapping.CategoryName = categoryName;
        mapping.Color = category.Color;
        mapping.Priority = 100;

        await _db.SaveChangesAsync(ct);
        return "app_mapping";
    }

    private async Task<string> LabelAppKeywordAsync(
        string target,
        string categoryName,
        Guid categoryId,
        string? keyword,
        CancellationToken ct)
    {
        var normalizedKeyword = NormalizeKeyword(keyword);
        var conditions = JsonSerializer.Serialize(new
        {
            all = new object[]
            {
                new { field = "windowTitle", op = "contains", value = normalizedKeyword }
            }
        });
        await UpsertRuleAsync(BuildRuleName(target, normalizedKeyword), categoryName, categoryId, 500, conditions, ct);
        return "app_context_rule";
    }

    private async Task<string> LabelDomainAsync(
        string target,
        string categoryName,
        Guid categoryId,
        string? keyword,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            var conditions = JsonSerializer.Serialize(new
            {
                all = new object[]
                {
                    new { field = "domain", op = "equals", value = target }
                }
            });
            await UpsertRuleAsync(BuildRuleName(target, null), categoryName, categoryId, 400, conditions, ct);
            return "domain_rule";
        }

        var normalizedKeyword = NormalizeKeyword(keyword);
        var keywordConditions = JsonSerializer.Serialize(new
        {
            all = new object[]
            {
                new { field = "domain", op = "equals", value = target },
                new { field = "urlPath", op = "contains", value = normalizedKeyword }
            }
        });
        await UpsertRuleAsync(BuildRuleName(target, normalizedKeyword), categoryName, categoryId, 450, keywordConditions, ct);
        return "domain_rule";
    }

    private async Task<string> LabelMobileAppAsync(
        string target,
        string categoryName,
        Guid? userId,
        CancellationToken ct)
    {
        // PcTracker 模块不引用 Mobile 模块：mobile_app_category_rules 走原生 SQL（同库同表）。
        var id = Guid.NewGuid();
        var resolvedUserId = userId ?? Guid.Empty;
        const string sql = """
            INSERT INTO mobile_app_category_rules (id, user_id, rule_type, pattern, life_category, priority, is_enabled, created_at, updated_at)
            VALUES ({0}, {1}, 'package-exact', {2}, {3}, 100, true, now(), now())
            ON CONFLICT (user_id, rule_type, pattern)
            DO UPDATE SET life_category = {3}, updated_at = now()
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { id, resolvedUserId, target, categoryName }, ct);
        return "mobile_app_rule";
    }

    /// <summary>幂等规则写入：规则名已存在则 UPDATE 分类与条件，否则 INSERT。</summary>
    private async Task UpsertRuleAsync(
        string ruleName,
        string categoryName,
        Guid categoryId,
        int priority,
        string conditionsJson,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rule = await _db.Set<ActivityCategoryRuleEntity>()
            .FirstOrDefaultAsync(r => r.RuleName == ruleName, ct);
        if (rule is null)
        {
            rule = new ActivityCategoryRuleEntity
            {
                Id = Guid.NewGuid(),
                RuleName = ruleName,
                Scope = "activity",
                Source = "user",
                Status = "active",
                Priority = priority,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Set<ActivityCategoryRuleEntity>().Add(rule);
        }

        rule.CategoryName = categoryName;
        rule.CategoryId = categoryId;
        rule.Priority = priority;
        rule.ConditionsJson = conditionsJson;
        rule.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
    }

    private async Task<PcCategoryEntity> LoadCategoryColorAsync(Guid categoryId, CancellationToken ct)
    {
        return await _db.Set<PcCategoryEntity>().FindAsync(new object[] { categoryId }, ct)
            ?? throw new ArgumentException($"分类（{categoryId}）不存在。");
    }

    // ---------- 队列 ----------

    /// <summary>构建打标队列：应用候选按 app_name_normalized 聚合、域名候选按 data_json->>'url' 的 host 聚合、
    /// 手机应用候选按 mobile_usage_aggregates 聚合，均排除已有映射/规则覆盖，按时长降序取前 limit 项。
    /// 无 userId 时（匿名/测试）不生成手机应用候选。</summary>
    public async Task<ActivityLabelingQueueResponse> BuildQueueAsync(int limit, CancellationToken ct)
        => await BuildQueueAsync(limit, null, ct);

    public async Task<ActivityLabelingQueueResponse> BuildQueueAsync(int limit, Guid? userId, CancellationToken ct)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);
        var items = new List<ActivityLabelingQueueItem>();

        var appCandidates = await BuildAppCandidatesAsync(ct);
        items.AddRange(appCandidates.Select(c => new ActivityLabelingQueueItem(
            "app", c.App, c.DisplayName, (int)(c.TotalSeconds / 60), new List<string>())));

        var domainItems = await BuildDomainCandidatesAsync(ct);
        items.AddRange(domainItems);

        var mobileItems = await BuildMobileCandidatesAsync(userId, ct);
        items.AddRange(mobileItems);

        var top = items
            .OrderByDescending(i => i.Minutes)
            .Take(safeLimit)
            .ToList();

        // sample_titles 在 limit 截断后再批量查询（避免 N+1）
        await FillAppSampleTitlesAsync(top, ct);

        return new ActivityLabelingQueueResponse(top);
    }

    private sealed record AppCandidate(string App, string DisplayName, double TotalSeconds);

    private async Task<List<AppCandidate>> BuildAppCandidatesAsync(CancellationToken ct)
    {
        var aggregates = await _db.Set<AwEventEntity>()
            .Where(e => e.AppNameNormalized != null && e.AppNameNormalized != "")
            .GroupBy(e => e.AppNameNormalized!)
            .Select(g => new { App = g.Key, TotalSeconds = g.Sum(e => e.Duration) })
            .ToListAsync(ct);

        var mappedPatterns = await _db.Set<AppCategoryEntity>()
            .Select(e => e.AppPattern)
            .ToListAsync(ct);
        var mappedSet = mappedPatterns
            .Select(NormalizeAppPattern)
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var displayNames = await ResolveDisplayNamesAsync(
            aggregates.Select(a => a.App).ToList(),
            ct);

        return aggregates
            .Where(a => a.TotalSeconds >= MinimumCandidateDurationSeconds)
            .Where(a => !mappedSet.Contains(a.App))
            .Select(a => new AppCandidate(a.App, displayNames.GetValueOrDefault(a.App, a.App), a.TotalSeconds))
            .OrderByDescending(a => a.TotalSeconds)
            .ToList();
    }

    private async Task<List<ActivityLabelingQueueItem>> BuildDomainCandidatesAsync(CancellationToken ct)
    {
        var webEvents = await _db.Set<AwEventEntity>()
            .Where(e => e.EventType == "web" || e.BucketType == "web.tab.current")
            .Where(e => e.Duration > 0)
            .ToListAsync(ct);

        var domainDurations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var titlesByDomain = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var webEvent in webEvents)
        {
            var domain = ExtractDomain(webEvent.DataJson);
            if (string.IsNullOrWhiteSpace(domain))
                continue;

            domainDurations[domain] = domainDurations.GetValueOrDefault(domain) + webEvent.Duration;
            var title = !string.IsNullOrWhiteSpace(webEvent.WindowTitle)
                ? webEvent.WindowTitle
                : ExtractTitle(webEvent.DataJson);
            if (!string.IsNullOrWhiteSpace(title))
            {
                var titles = titlesByDomain.GetValueOrDefault(domain, new List<string>());
                if (!titles.Contains(title, StringComparer.Ordinal))
                    titles.Add(title);
                titlesByDomain[domain] = titles;
            }
        }

        var (exactDomains, domainSuffixes) = await LoadCoveredDomainsAsync(ct);

        return domainDurations
            .Where(pair => pair.Value >= MinimumCandidateDurationSeconds)
            .Where(pair => !IsDomainCovered(pair.Key, exactDomains, domainSuffixes))
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new ActivityLabelingQueueItem(
                "domain",
                pair.Key,
                pair.Key,
                (int)(pair.Value / 60),
                titlesByDomain.GetValueOrDefault(pair.Key, new List<string>()).Take(3).ToList()))
            .ToList();
    }

    /// <summary>加载已覆盖规则：返回精确匹配域名集合与域名后缀集合。
    /// 仅解析 all 数组中的 domain/domainSuffix 条件；仅含 urlPath/windowTitle/title 关键词条件的规则不构成排除。</summary>
    private async Task<(HashSet<string> ExactDomains, List<string> DomainSuffixes)> LoadCoveredDomainsAsync(CancellationToken ct)
    {
        var exactDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domainSuffixes = new List<string>();

        var rules = await _db.Set<ActivityCategoryRuleEntity>()
            .Where(r => r.Status == "active"
                && (r.ConditionsJson.Contains("\"domain\"") || r.ConditionsJson.Contains("\"domainSuffix\"")))
            .Select(r => r.ConditionsJson)
            .ToListAsync(ct);

        foreach (var conditions in rules)
        {
            if (string.IsNullOrWhiteSpace(conditions))
                continue;

            try
            {
                using var document = JsonDocument.Parse(conditions);
                if (!document.RootElement.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var condition in all.EnumerateArray())
                {
                    if (condition.ValueKind != JsonValueKind.Object
                        || !condition.TryGetProperty("field", out var field))
                        continue;

                    var fieldName = field.GetString();
                    if (fieldName != "domain" && fieldName != "domainSuffix")
                        continue;

                    if (!condition.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                        continue;

                    var pattern = value.GetString();
                    if (string.IsNullOrWhiteSpace(pattern))
                        continue;

                    if (fieldName == "domain")
                        exactDomains.Add(pattern);
                    else
                        domainSuffixes.Add(pattern);
                }
            }
            catch (JsonException)
            {
                // 忽略损坏的条件 JSON
            }
        }

        return (exactDomains, domainSuffixes);
    }

    /// <summary>规则覆盖判定：domain equals 命中（域名精确相等），或 domainSuffix 命中
    /// （域名等于后缀，或以 "." + 后缀结尾）。</summary>
    private static bool IsDomainCovered(
        string domain,
        IReadOnlySet<string> exactDomains,
        IReadOnlyList<string> domainSuffixes)
    {
        if (exactDomains.Contains(domain))
            return true;

        foreach (var suffix in domainSuffixes)
        {
            if (domain.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || domain.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>单次批量查询 app_signatures 显示名（避免逐个应用 N+1 查询）。</summary>
    private async Task<Dictionary<string, string>> ResolveDisplayNamesAsync(List<string> apps, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (apps.Count == 0)
            return names;

        var signatures = await _db.Set<AppSignatureEntity>()
            .Where(s => apps.Contains(s.ProcessName))
            .Select(s => new { s.ProcessName, s.DisplayName })
            .ToListAsync(ct);

        foreach (var signature in signatures)
            names[signature.ProcessName] = string.IsNullOrWhiteSpace(signature.DisplayName)
                ? signature.ProcessName
                : signature.DisplayName;

        return names;
    }

    /// <summary>为队列顶部的 app 候选批量补 sample_titles（在 limit 截断后执行，避免全量 N+1）。</summary>
    private async Task FillAppSampleTitlesAsync(List<ActivityLabelingQueueItem> top, CancellationToken ct)
    {
        var appTargets = top.Where(i => i.TargetType == "app").Select(i => i.Target).ToList();
        if (appTargets.Count == 0)
            return;

        var titleRows = await _db.Set<AwEventEntity>()
            .Where(e => appTargets.Contains(e.AppNameNormalized!)
                && e.WindowTitle != null && e.WindowTitle != "")
            .Select(e => new { e.AppNameNormalized, e.WindowTitle })
            .ToListAsync(ct);

        var titlesByApp = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in titleRows)
        {
            var titles = titlesByApp.GetValueOrDefault(row.AppNameNormalized!, new List<string>());
            if (!titles.Contains(row.WindowTitle!, StringComparer.Ordinal))
                titles.Add(row.WindowTitle!);
            titlesByApp[row.AppNameNormalized!] = titles;
        }

        for (var i = 0; i < top.Count; i++)
        {
            if (top[i].TargetType != "app")
                continue;
            top[i] = top[i] with
            {
                SampleTitles = titlesByApp.GetValueOrDefault(top[i].Target, new List<string>()).Take(3).ToList()
            };
        }
    }

    /// <summary>手机应用候选：聚合 mobile_usage_aggregates 的 usage 时长（foreground_seconds），
    /// 过滤当前 user、排除已被 mobile_app_category_rules 覆盖的 package_name，按时长降序。
    /// display_name 取聚合行自带显示名（源自 MobileAppCatalog），查不到则用 package_name。
    /// 参数化原生 SQL，禁止拼接用户输入；InMemory（测试）下跳过。</summary>
    private async Task<List<ActivityLabelingQueueItem>> BuildMobileCandidatesAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is not Guid uid
            || _db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
            return [];

        const string sql = """
            SELECT package_name AS "PackageName",
                   COALESCE(NULLIF(display_name, ''), package_name) AS "DisplayName",
                   SUM(foreground_seconds) AS "TotalSeconds"
              FROM mobile_usage_aggregates
             WHERE user_id = {0}
               AND package_name IS NOT NULL AND btrim(package_name) <> ''
               AND NOT EXISTS (
                    SELECT 1 FROM mobile_app_category_rules r
                     WHERE r.user_id = mobile_usage_aggregates.user_id AND r.is_enabled
                       AND (
                            (r.rule_type = 'package-exact' AND r.pattern = mobile_usage_aggregates.package_name)
                         OR (r.rule_type = 'package-prefix' AND mobile_usage_aggregates.package_name LIKE r.pattern || '%')
                         OR (r.rule_type IN ('package-keyword', 'keyword') AND mobile_usage_aggregates.package_name LIKE '%' || r.pattern || '%')
                         OR (r.rule_type IN ('display-keyword', 'keyword') AND mobile_usage_aggregates.display_name LIKE '%' || r.pattern || '%')
                       )
                )
             GROUP BY package_name, display_name
             ORDER BY SUM(foreground_seconds) DESC
            """;

        var rows = await _db.Database
            .SqlQueryRaw<MobileUsageAggregateRow>(sql, new object[] { uid })
            .ToListAsync(ct);

        return rows
            .Where(r => r.TotalSeconds >= MinimumCandidateDurationSeconds)
            .Select(r => new ActivityLabelingQueueItem(
                "mobile_app",
                r.PackageName,
                r.DisplayName,
                (int)(r.TotalSeconds / 60),
                new List<string>()))
            .ToList();
    }

    private sealed class MobileUsageAggregateRow
    {
        public string PackageName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long TotalSeconds { get; set; }
    }

    private static string? ExtractDomain(string dataJson)
    {
        var url = ExtractString(dataJson, "url");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
    }

    private static string? ExtractTitle(string dataJson) => ExtractString(dataJson, "title");

    private static string? ExtractString(string dataJson, string key)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (!document.RootElement.TryGetProperty(key, out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            return value.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeTargetType(string? targetType)
        => (targetType ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeTarget(string? target)
    {
        var normalized = (target ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("target 不能为空。");
        return normalized;
    }

    private static string NormalizeScope(string? scope)
        => (scope ?? string.Empty).Trim().ToLowerInvariant() is "keyword"
            ? "keyword"
            : "all";

    private static string NormalizeKeyword(string? keyword)
    {
        var normalized = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("scope=keyword 时 keyword 不能为空。");
        return normalized;
    }

    /// <summary>规则名：target/关键词截断到 48 字符，避免超过 rule_name 128 上限；
    /// 截断规则固定，保证幂等键（规则名）稳定。</summary>
    private static string BuildRuleName(string target, string? keyword)
    {
        var truncatedTarget = TruncateForRuleName(target);
        var truncatedKeyword = keyword is null ? "all" : TruncateForRuleName(keyword);
        return $"Label: {truncatedTarget} [{truncatedKeyword}]";
    }

    private static string TruncateForRuleName(string value)
        => value.Length <= 48 ? value : value[..48];

    /// <summary>app_pattern 归一化：去掉 .exe 后缀，与 AppSignatureService 匹配语义保持一致。</summary>
    private static string NormalizeAppPattern(string pattern)
        => pattern.Trim().ToLowerInvariant() is var p && p.EndsWith(".exe", StringComparison.Ordinal)
            ? p[..^4]
            : p;
}
