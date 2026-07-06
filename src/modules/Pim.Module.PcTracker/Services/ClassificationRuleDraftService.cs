using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class ClassificationRuleDraftService
{
    private const int MaxRuleNameLength = 128;
    private const string DefaultCategoryColor = "#64748b";

    private readonly PimDbContext _db;

    public ClassificationRuleDraftService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<SaveActivityClassificationRuleRequest> BuildSuggestionDraftAsync(
        Guid suggestionId,
        SuggestionClassificationPreviewRequest request,
        CancellationToken ct)
    {
        var suggestion = await _db.Set<ActivityClassificationSuggestionEntity>()
            .FirstOrDefaultAsync(item => item.Id == suggestionId, ct)
            ?? throw new KeyNotFoundException($"未找到活动分类建议：{suggestionId}。");

        if (!string.Equals(suggestion.Status, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException($"分类建议 {suggestionId} 必须处于待处理状态后才能预览或应用。");

        var condition = BuildCondition(suggestion.ClusterKey);
        var category = request.CategoryName ?? suggestion.SuggestedCategory ?? suggestion.CurrentCategory;
        var categoryName = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var projectTag = request.ProjectTag ?? suggestion.SuggestedProjectTag;
        var ruleName = BuildRuleName(suggestion);
        var color = await ResolveCategoryColorAsync(categoryName, ct);

        return new SaveActivityClassificationRuleRequest(
            ruleName,
            "activity",
            categoryName,
            string.IsNullOrWhiteSpace(projectTag) ? null : projectTag.Trim(),
            color,
            900,
            JsonSerializer.Serialize(new { all = new[] { condition } }),
            0.95,
            $"由分类建议 {suggestion.Id} 创建。");
    }

    private static object BuildCondition(string clusterKey)
    {
        var separator = clusterKey.IndexOf(':');
        if (separator <= 0 || separator == clusterKey.Length - 1)
            throw new ArgumentException($"不支持的建议聚类键：{clusterKey}。");

        var kind = clusterKey[..separator].Trim().ToLowerInvariant();
        var value = clusterKey[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"不支持的建议聚类键：{clusterKey}。");

        return kind switch
        {
            "web" => new { field = "domain", op = "domainSuffix", value },
            "app" => new { field = "appNameNormalized", op = "equals", value },
            _ => throw new ArgumentException($"不支持的建议聚类键：{clusterKey}。")
        };
    }

    private async Task<string> ResolveCategoryColorAsync(string? categoryName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return DefaultCategoryColor;

        return await _db.Set<PcCategoryEntity>()
            .Where(category => category.Name == categoryName)
            .Select(category => category.Color)
            .FirstOrDefaultAsync(ct)
            ?? DefaultCategoryColor;
    }

    private static string BuildRuleName(ActivityClassificationSuggestionEntity suggestion)
    {
        const string prefix = "Suggestion: ";
        var suffix = $" {suggestion.Id:N}";
        var maxClusterLength = MaxRuleNameLength - prefix.Length - suffix.Length;
        var clusterKey = suggestion.ClusterKey.Trim();
        if (clusterKey.Length > maxClusterLength)
        {
            var truncatedLength = Math.Max(0, maxClusterLength - 3);
            clusterKey = clusterKey[..truncatedLength] + "...";
        }

        return $"{prefix}{clusterKey}{suffix}";
    }
}
