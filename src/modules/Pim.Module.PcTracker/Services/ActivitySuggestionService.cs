using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivitySuggestionService
{
    private const string PendingStatus = "pending";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PimDbContext _db;
    private readonly AppSignatureService _appSignatures;

    public ActivitySuggestionService(PimDbContext db, AppSignatureService appSignatures)
    {
        _db = db;
        _appSignatures = appSignatures;
    }

    public async Task<List<ActivityClassificationSuggestionDto>> BuildSuggestionsAsync(
        IReadOnlyCollection<PcDetailRecord> records,
        int recommendedMinimumMinutes,
        CancellationToken ct)
    {
        var candidates = records
            .Where(record => NeedsSuggestion(record, recommendedMinimumMinutes))
            .Select(record => new { Record = record, ClusterKey = GetClusterKey(record) })
            .Where(x => x.ClusterKey is not null)
            .GroupBy(x => x.ClusterKey!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var group in candidates)
        {
            var groupRecords = group.Select(x => x.Record).ToList();
            var clusterKey = group.Key;
            var existingSuggestions = await _db.Set<ActivityClassificationSuggestionEntity>()
                .Where(s => s.ClusterKey == clusterKey)
                .ToListAsync(ct);
            var entity = existingSuggestions
                .FirstOrDefault(s => string.Equals(s.Status, PendingStatus, StringComparison.Ordinal));

            if (entity is null)
            {
                if (existingSuggestions.Count > 0)
                    continue;

                entity = new ActivityClassificationSuggestionEntity
                {
                    Id = Guid.NewGuid(),
                    ClusterKey = clusterKey,
                    Status = PendingStatus,
                    CreatedAt = now
                };
                _db.Set<ActivityClassificationSuggestionEntity>().Add(entity);
            }

            entity.SampleCount = groupRecords.Count;
            entity.TotalDurationSeconds = groupRecords.Sum(r => r.DurationSeconds ?? 0);
            entity.SampleRecordsJson = JsonSerializer.Serialize(BuildSampleRecords(groupRecords), JsonOptions);
            entity.SanitizedContextJson = JsonSerializer.Serialize(BuildSanitizedContext(clusterKey, groupRecords), JsonOptions);
            entity.CurrentCategory = groupRecords
                .Select(r => r.CategoryName)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return await GetSuggestionsAsync(ct);
    }

    public async Task<List<ActivityClassificationSuggestionDto>> GetSuggestionsAsync(CancellationToken ct)
    {
        var entities = await _db.Set<ActivityClassificationSuggestionEntity>()
            .Where(s => s.Status == PendingStatus)
            .OrderByDescending(s => s.TotalDurationSeconds)
            .ToListAsync(ct);

        var result = new List<ActivityClassificationSuggestionDto>(entities.Count);
        foreach (var entity in entities)
        {
            var dto = ToSuggestionDto(entity);

            // Enrich with app signature info
            var appName = ExtractAppName(entity.ClusterKey);
            if (appName is not null)
            {
                var sig = await _appSignatures.LookupByProcessNameAsync(appName, ct);
                if (sig is not null)
                {
                    dto = dto with
                    {
                        AppDisplayName = sig.DisplayName,
                        AppIcon = sig.Icon,
                        RecognitionSource = sig.Source
                    };
                }
            }

            result.Add(dto);
        }

        return result;
    }

    private static string? ExtractAppName(string? clusterKey)
    {
        if (string.IsNullOrWhiteSpace(clusterKey))
            return null;
        if (clusterKey.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
            return clusterKey[4..];
        return null;
    }

    public async Task<List<string>> GetRecentProjectTagsAsync(CancellationToken ct)
    {
        var ruleTags = await _db.Set<ActivityCategoryRuleEntity>()
            .Where(r => r.ProjectTag != null && r.ProjectTag != "")
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => r.ProjectTag!)
            .Take(20)
            .ToListAsync(ct);

        var snapshotTags = await _db.Set<ActivityClassificationEntity>()
            .Where(s => s.ProjectTag != null && s.ProjectTag != "")
            .OrderByDescending(s => s.ClassifiedAt)
            .Select(s => s.ProjectTag!)
            .Take(20)
            .ToListAsync(ct);

        return ruleTags
            .Concat(snapshotTags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    public async Task<ActivityClassificationRuleDto> AcceptSuggestionAsync(
        Guid id,
        AcceptActivityClassificationSuggestionRequest req,
        CancellationToken ct)
    {
        var suggestion = await _db.Set<ActivityClassificationSuggestionEntity>()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"Activity classification suggestion '{id}' was not found.");
        EnsurePending(suggestion);

        var now = DateTimeOffset.UtcNow;
        var rule = new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = req.RuleName,
            Scope = req.Scope,
            CategoryName = req.CategoryName,
            ProjectTag = req.ProjectTag,
            Color = req.Color,
            Priority = req.Priority,
            Source = "user",
            Status = "active",
            ConditionsJson = req.ConditionsJson,
            Confidence = req.Confidence,
            Explanation = req.Explanation,
            CreatedAt = now,
            UpdatedAt = now
        };

        suggestion.Status = "accepted";
        suggestion.UpdatedAt = now;
        _db.Set<ActivityCategoryRuleEntity>().Add(rule);
        await _db.SaveChangesAsync(ct);
        return ToRuleDto(rule);
    }

    public async Task RejectSuggestionAsync(Guid id, CancellationToken ct)
    {
        var suggestion = await _db.Set<ActivityClassificationSuggestionEntity>()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"Activity classification suggestion '{id}' was not found.");
        EnsurePending(suggestion);

        suggestion.Status = "rejected";
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static void EnsurePending(ActivityClassificationSuggestionEntity suggestion)
    {
        if (string.Equals(suggestion.Status, PendingStatus, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"Activity classification suggestion '{suggestion.Id}' must be pending before it can be changed. Current status is '{suggestion.Status}'.");
    }

    private static bool NeedsSuggestion(PcDetailRecord record, int recommendedMinimumMinutes)
    {
        var minimumDurationSeconds = recommendedMinimumMinutes * 60;
        if ((record.DurationSeconds ?? 0) < minimumDurationSeconds)
            return false;

        return string.Equals(record.ClassificationSource, "fallback", StringComparison.OrdinalIgnoreCase)
            || (record.ClassificationConfidence is not null && record.ClassificationConfidence < 0.5);
    }

    private static string? GetClusterKey(PcDetailRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Domain))
            return $"web:{record.Domain.Trim().ToLowerInvariant()}";

        var app = record.AppName ?? record.BrowserAppName;
        if (string.IsNullOrWhiteSpace(app))
            return null;

        return $"app:{AppNameNormalizer.Normalize(app)}";
    }

    private static List<object> BuildSampleRecords(List<PcDetailRecord> records)
    {
        return records
            .OrderByDescending(r => r.DurationSeconds ?? 0)
            .Take(5)
            .Select(r => new
            {
                r.RecordType,
                r.Start,
                r.End,
                r.DurationSeconds,
                r.AppName,
                r.BrowserAppName,
                r.Domain,
                sanitizedUrl = ActivityUrlSanitizer.Sanitize(r.Url),
                r.Title,
                r.CategoryName,
                r.ClassificationConfidence,
                r.ClassificationSource
            })
            .Cast<object>()
            .ToList();
    }

    private static object BuildSanitizedContext(string clusterKey, List<PcDetailRecord> records)
    {
        return new
        {
            clusterKey,
            sampleCount = records.Count,
            totalDurationSeconds = records.Sum(r => r.DurationSeconds ?? 0),
            domains = records
                .Select(r => r.Domain)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            apps = records
                .Select(r => r.AppName ?? r.BrowserAppName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            urls = records
                .Select(r => ActivityUrlSanitizer.Sanitize(r.Url))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            titles = records
                .Select(r => r.Title)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList()
        };
    }

    private static ActivityClassificationSuggestionDto ToSuggestionDto(ActivityClassificationSuggestionEntity entity)
    {
        return new ActivityClassificationSuggestionDto(
            entity.Id,
            entity.ClusterKey,
            entity.SampleCount,
            entity.TotalDurationSeconds,
            entity.SampleRecordsJson,
            entity.SanitizedContextJson,
            entity.CurrentCategory,
            entity.SuggestedCategory,
            entity.SuggestedProjectTag,
            entity.SuggestedRulesJson,
            entity.UserFeedback,
            entity.LlmResponseJson,
            entity.Status);
    }

    private static ActivityClassificationRuleDto ToRuleDto(ActivityCategoryRuleEntity rule)
    {
        return new ActivityClassificationRuleDto(
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
    }
}
