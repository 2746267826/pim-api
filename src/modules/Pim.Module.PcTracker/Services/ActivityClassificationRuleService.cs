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
        var rule = ToEntity(request);
        _db.Set<ActivityCategoryRuleEntity>().Add(rule);
        await _db.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    public async Task ValidateAsync(
        SaveActivityClassificationRuleRequest request,
        bool ensureUniqueRuleName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RuleName))
            throw new ArgumentException("RuleName is required.", nameof(request));

        var ruleName = request.RuleName.Trim();
        _ = NormalizeScope(request.Scope);
        ValidateConditionsJson(request.ConditionsJson);

        if (ensureUniqueRuleName
            && await _db.Set<ActivityCategoryRuleEntity>().AnyAsync(rule => rule.RuleName == ruleName, ct))
            throw new InvalidOperationException($"Activity classification rule '{ruleName}' already exists.");

        if (!string.IsNullOrWhiteSpace(request.CategoryName))
        {
            var categoryName = request.CategoryName.Trim();
            var exists = await _db.Set<PcCategoryEntity>()
                .AnyAsync(category => category.Name == categoryName, ct);
            if (!exists)
                throw new ArgumentException($"CategoryName '{categoryName}' does not exist.", nameof(request));
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
            _ => throw new ArgumentException($"Unsupported classification rule scope '{scope}'.")
        };
    }

    public static ActivityCategoryRuleEntity ToEntity(SaveActivityClassificationRuleRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = request.RuleName.Trim(),
            Scope = NormalizeScope(request.Scope),
            CategoryName = string.IsNullOrWhiteSpace(request.CategoryName) ? null : request.CategoryName.Trim(),
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
            throw new ArgumentException("ConditionsJson is required.");

        try
        {
            using var document = JsonDocument.Parse(conditionsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("all", out var allConditions)
                || allConditions.ValueKind != JsonValueKind.Array
                || allConditions.GetArrayLength() == 0)
                throw new ArgumentException("ConditionsJson must contain a non-empty all array.");

            foreach (var condition in allConditions.EnumerateArray())
                ValidateCondition(condition);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("ConditionsJson must be valid JSON.", ex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException("Regex condition value must be a valid regular expression.", ex);
        }
    }

    private static void ValidateCondition(JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(condition, "field", out var field)
            || !TryGetStringProperty(condition, "op", out var op)
            || !condition.TryGetProperty("value", out var value))
            throw new ArgumentException("Each condition must include field, op, and value.");

        if (!AllowedConditionFields.Contains(field) || !AllowedConditionOps.Contains(op))
            throw new ArgumentException("ConditionsJson contains an unsupported condition.");

        ValidateConditionValue(op, value);
    }

    private static void ValidateConditionValue(string op, JsonElement value)
    {
        if (op == "containsAny")
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                throw new ArgumentException("containsAny requires a non-empty string array value.");

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new ArgumentException("containsAny requires non-empty string values.");
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"{op} requires a non-empty string value.");

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
