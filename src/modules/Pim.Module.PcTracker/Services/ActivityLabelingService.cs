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
        await UpsertRuleAsync($"Label: {target} [{normalizedKeyword}]", categoryName, categoryId, 500, conditions, ct);
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
            await UpsertRuleAsync($"Label: {target} [all]", categoryName, categoryId, 400, conditions, ct);
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
        await UpsertRuleAsync($"Label: {target} [{normalizedKeyword}]", categoryName, categoryId, 450, keywordConditions, ct);
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

    /// <summary>构建打标队列：应用候选按 app_name_normalized 聚合、域名候选按 data_json->>'url' 的 host 聚合，
    /// 排除已有映射/规则覆盖，按时长降序取前 limit 项。mobile_app 候选留待阶段 2。</summary>
    public async Task<ActivityLabelingQueueResponse> BuildQueueAsync(int limit, CancellationToken ct)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);
        var items = new List<ActivityLabelingQueueItem>();

        var appItems = await BuildAppCandidatesAsync(ct);
        items.AddRange(appItems);

        var domainItems = await BuildDomainCandidatesAsync(ct);
        items.AddRange(domainItems);

        var top = items
            .OrderByDescending(i => i.Minutes)
            .Take(safeLimit)
            .ToList();

        return new ActivityLabelingQueueResponse(top);
    }

    private async Task<List<ActivityLabelingQueueItem>> BuildAppCandidatesAsync(CancellationToken ct)
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

        var candidates = aggregates
            .Where(a => a.TotalSeconds >= MinimumCandidateDurationSeconds)
            .Where(a => !mappedSet.Contains(a.App))
            .Select(a => new
            {
                a.App,
                a.TotalSeconds,
                DisplayName = displayNames.GetValueOrDefault(a.App, a.App)
            })
            .OrderByDescending(a => a.TotalSeconds)
            .ToList();

        var result = new List<ActivityLabelingQueueItem>();
        foreach (var candidate in candidates)
        {
            var titles = await _db.Set<AwEventEntity>()
                .Where(e => e.AppNameNormalized == candidate.App
                    && e.WindowTitle != null && e.WindowTitle != "")
                .Select(e => e.WindowTitle!)
                .Distinct()
                .Take(3)
                .ToListAsync(ct);
            result.Add(new ActivityLabelingQueueItem(
                "app",
                candidate.App,
                candidate.DisplayName,
                (int)(candidate.TotalSeconds / 60),
                titles));
        }

        return result;
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

        var coveredDomains = await LoadCoveredDomainsAsync(ct);

        return domainDurations
            .Where(pair => pair.Value >= MinimumCandidateDurationSeconds)
            .Where(pair => !coveredDomains.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new ActivityLabelingQueueItem(
                "domain",
                pair.Key,
                pair.Key,
                (int)(pair.Value / 60),
                titlesByDomain.GetValueOrDefault(pair.Key, new List<string>()).Take(3).ToList()))
            .ToList();
    }

    private async Task<HashSet<string>> LoadCoveredDomainsAsync(CancellationToken ct)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = await _db.Set<ActivityCategoryRuleEntity>()
            .Where(r => r.Status == "active" && r.ConditionsJson.Contains("\"domain\""))
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
                        || !condition.TryGetProperty("field", out var field)
                        || field.GetString() != "domain"
                        || !condition.TryGetProperty("value", out var value)
                        || value.ValueKind != JsonValueKind.String)
                        continue;

                    covered.Add(value.GetString()!);
                }
            }
            catch (JsonException)
            {
                // 忽略损坏的条件 JSON
            }
        }

        return covered;
    }

    private async Task<Dictionary<string, string>> ResolveDisplayNamesAsync(List<string> apps, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var displayName = await _db.Set<AppSignatureEntity>()
                .Where(s => s.ProcessName == app)
                .Select(s => s.DisplayName)
                .FirstOrDefaultAsync(ct);
            names[app] = string.IsNullOrWhiteSpace(displayName) ? app : displayName;
        }

        return names;
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

    /// <summary>app_pattern 归一化：去掉 .exe 后缀，与 AppSignatureService 匹配语义保持一致。</summary>
    private static string NormalizeAppPattern(string pattern)
        => pattern.Trim().ToLowerInvariant() is var p && p.EndsWith(".exe", StringComparison.Ordinal)
            ? p[..^4]
            : p;
}
