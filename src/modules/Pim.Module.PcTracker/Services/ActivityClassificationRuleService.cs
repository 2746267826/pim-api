using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class ActivityClassificationRuleService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly PimDbContext _db;

    public ActivityClassificationRuleService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<List<ActivityCategoryRuleEntity>> LoadActiveAsync(CancellationToken ct)
    {
        return await _db.Set<ActivityCategoryRuleEntity>()
            .Where(rule => rule.Status == "active")
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.RuleName)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);
    }

    public async Task<List<ActivityClassificationRuleDto>> ListAsync(CancellationToken ct)
    {
        return await _db.Set<ActivityCategoryRuleEntity>()
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.RuleName)
            .Select(rule => ToDto(rule))
            .ToListAsync(ct);
    }

    public async Task<ActivityClassificationRuleDto> SaveAsync(
        SaveActivityClassificationRuleRequest request,
        CancellationToken ct)
    {
        await ValidateAsync(request, ensureUniqueRuleName: true, ct);
        var categoryId = await ResolveCategoryIdAsync(request, ct);
        var rule = ToEntity(request, categoryId);
        _db.Set<ActivityCategoryRuleEntity>().Add(rule);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"活动分类规则「{request.RuleName.Trim()}」已存在。", ex);
        }
        return ToDto(rule);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner.GetType().Name == "PostgresException")
            {
                var prop = inner.GetType().GetProperty("SqlState");
                if (prop?.GetValue(inner) as string == "23505") return true;
            }
            inner = inner.InnerException;
        }
        return ex.InnerException?.Message.Contains("23505") == true
            || ex.Message.Contains("23505");
    }

    public async Task ValidateAsync(
        SaveActivityClassificationRuleRequest request,
        bool ensureUniqueRuleName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RuleName))
            throw new ArgumentException("规则名称不能为空。", nameof(request));

        var ruleName = request.RuleName.Trim();
        _ = NormalizeScope(request.Scope);
        ValidateConditionsJson(request.ConditionsJson);

        if (ensureUniqueRuleName
            && await _db.Set<ActivityCategoryRuleEntity>().AnyAsync(rule => rule.RuleName == ruleName, ct))
            throw new InvalidOperationException($"活动分类规则「{ruleName}」已存在。");

        if (!string.IsNullOrWhiteSpace(request.CategoryName))
        {
            var categoryName = request.CategoryName.Trim();
            var exists = await _db.Set<PcCategoryEntity>()
                .AnyAsync(category => category.Name == categoryName, ct);
            if (!exists)
                throw new ArgumentException($"分类「{categoryName}」不存在。", nameof(request));
        }
    }

    public static string NormalizeScope(string? scope)
    {
        var normalized = (scope ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => "activity",
            "app" => "activity",
            "activity" => "activity",
            "both" => "both",
            "project" => "project",
            _ => throw new ArgumentException($"不支持的分类规则范围：{scope}。")
        };
    }

    /// <summary>分类名非空时按名查 pc_categories 解析 category_id；查不到抛与 ValidateAsync 相同的异常。</summary>
    private async Task<Guid?> ResolveCategoryIdAsync(SaveActivityClassificationRuleRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CategoryName))
            return null;

        var categoryName = request.CategoryName.Trim();
        var categoryId = await _db.Set<PcCategoryEntity>()
            .Where(category => category.Name == categoryName)
            .Select(category => (Guid?)category.Id)
            .FirstOrDefaultAsync(ct);
        if (categoryId is null)
            throw new ArgumentException($"分类「{categoryName}」不存在。", nameof(request));
        return categoryId;
    }

    public static ActivityCategoryRuleEntity ToEntity(SaveActivityClassificationRuleRequest request, Guid? categoryId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = request.RuleName.Trim(),
            Scope = NormalizeScope(request.Scope),
            CategoryName = string.IsNullOrWhiteSpace(request.CategoryName) ? null : request.CategoryName.Trim(),
            CategoryId = categoryId,
            ProjectTag = string.IsNullOrWhiteSpace(request.ProjectTag) ? null : request.ProjectTag.Trim(),
            Color = string.IsNullOrWhiteSpace(request.Color) ? "#64748b" : request.Color.Trim(),
            Priority = request.Priority,
            Source = "user",
            Status = "active",
            ConditionsJson = request.ConditionsJson,
            Confidence = request.Confidence,
            Explanation = request.Explanation,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static ActivityClassificationRuleDto ToDto(ActivityCategoryRuleEntity rule) =>
        new(
            rule.Id,
            rule.RuleName,
            rule.Scope,
            rule.CategoryName,
            rule.CategoryId,
            rule.ProjectTag,
            rule.Color,
            rule.Priority,
            rule.Source,
            rule.Status,
            rule.ConditionsJson,
            rule.Confidence,
            rule.Explanation);

    private static void ValidateConditionsJson(string? conditionsJson)
    {
        if (string.IsNullOrWhiteSpace(conditionsJson))
            throw new ArgumentException("条件 JSON 不能为空。");

        try
        {
            using var document = JsonDocument.Parse(conditionsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("all", out var allConditions)
                || allConditions.ValueKind != JsonValueKind.Array
                || allConditions.GetArrayLength() == 0)
                throw new ArgumentException("条件 JSON 必须包含非空的 all 数组。");

            foreach (var condition in allConditions.EnumerateArray())
                ValidateCondition(condition);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("条件 JSON 必须是有效 JSON。", ex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException("正则条件值必须是有效的正则表达式。", ex);
        }
    }

    private static void ValidateCondition(JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(condition, "field", out var field)
            || !TryGetStringProperty(condition, "op", out var op)
            || !condition.TryGetProperty("value", out var value))
            throw new ArgumentException("每个条件都必须包含 field、op 和 value。");

        if (!AllowedConditionFields.Contains(field) || !AllowedConditionOps.Contains(op))
            throw new ArgumentException("条件 JSON 包含不支持的条件。");

        ValidateConditionValue(op, value);
    }

    private static void ValidateConditionValue(string op, JsonElement value)
    {
        if (op == "containsAny")
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                throw new ArgumentException("containsAny 需要非空字符串数组。");

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new ArgumentException("containsAny 的字符串值不能为空。");
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"{op} 需要非空字符串值。");

        if (op == "regex")
            _ = new Regex(value.GetString()!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            return false;

        value = property.GetString()!;
        return true;
    }

    private static readonly HashSet<string> AllowedConditionFields = new(StringComparer.Ordinal)
    {
        "recordType",
        "appName",
        "appNameNormalized",
        "domain",
        "urlPath",
        "title",
        "windowTitle",
        "filePath",
        "bucketType"
    };

    private static readonly HashSet<string> AllowedConditionOps = new(StringComparer.Ordinal)
    {
        "equals",
        "contains",
        "containsAny",
        "startsWith",
        "endsWith",
        "domainSuffix",
        "pathPrefix",
        "regex"
    };
}
