using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivitySuggestionService
{
    private const string PendingStatus = "pending";
    /// <summary>P-5（REQ-5）：既有空闲哨兵建议的处置状态 —— 置失效但保留行，便于追溯。</summary>
    private const string InvalidatedStatus = "invalidated";
    /// <summary>采集端空闲哨兵的应用名（Windows 客户端在空闲时段写死）。</summary>
    private const string IdleSentinelAppName = "__IDLE__";
    /// <summary>空闲哨兵对应的建议簇键（历史遗留待处理建议就长这样）。</summary>
    private const string IdleSentinelClusterKey = "app:__idle__";
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
        // P-5（REQ-5）：先把历史遗留的空闲哨兵建议置失效再扫描。
        // 不删除行 —— 谁在什么时候生成过、样本是什么，都还能查到。
        await RetireIdleSentinelSuggestionsAsync(ct);

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
            // REQ-5：这一簇样本属于哪个业务日 —— 列表接口的 date 参数只决定「扫描哪个业务日」，
            // 归日信息由每条建议自己带出来，前端才能把建议放回正确的那一天。
            var generatedForDate = ResolveGeneratedForDate(groupRecords);
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
            if (generatedForDate is not null)
                entity.GeneratedForDate = generatedForDate;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return await GetSuggestionsAsync(ct);
    }

    /// <summary>把历史遗留的空闲哨兵建议置为失效（保留行，可追溯）。幂等：没有匹配行时不写库。</summary>
    private async Task RetireIdleSentinelSuggestionsAsync(CancellationToken ct)
    {
        var stale = await _db.Set<ActivityClassificationSuggestionEntity>()
            .Where(s => s.Status == PendingStatus && s.ClusterKey == IdleSentinelClusterKey)
            .ToListAsync(ct);
        if (stale.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var entity in stale)
        {
            entity.Status = InvalidatedStatus;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>簇内样本所属业务日（取最新一条样本的业务日；全部无法解析时返回 null）。</summary>
    private static DateTime? ResolveGeneratedForDate(IEnumerable<PcDetailRecord> records)
    {
        DateTime? latest = null;
        foreach (var record in records)
        {
            if (!DateTimeOffset.TryParse(
                    record.Start,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var start))
            {
                continue;
            }

            var businessDay = PcTrackerService.GetBusinessDayForTimestamp(start);
            if (latest is null || businessDay > latest)
                latest = businessDay;
        }

        return latest;
    }

    public async Task<List<ActivityClassificationSuggestionDto>> GetSuggestionsAsync(CancellationToken ct)
    {
        var entities = await _db.Set<ActivityClassificationSuggestionEntity>()
            .Where(s => s.Status == PendingStatus)
            // REQ-5：空闲哨兵簇永不出现在列表里（置失效是持久化动作，这里是读取侧的兜底）。
            .Where(s => s.ClusterKey != IdleSentinelClusterKey)
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

    public async Task<List<ActivityClassificationSuggestionV2Dto>> GetSuggestionsV2Async(CancellationToken ct)
    {
        var entities = await _db.Set<ActivityClassificationSuggestionEntity>()
            .Where(s => s.Status == PendingStatus)
            // REQ-5：空闲哨兵簇不出现在建议列表（v2 同样口径）。
            .Where(s => s.ClusterKey != IdleSentinelClusterKey)
            .OrderByDescending(s => s.TotalDurationSeconds)
            .ToListAsync(ct);

        // Feedback silence check: reject within 3 days or rejected >= 3 times
        var threeDaysAgo = DateTimeOffset.UtcNow.AddDays(-3);
        var recentRejections = await _db.Set<PcSuggestionFeedbackEntity>()
            .Where(f => f.Action == "rejected")
            .ToListAsync(ct);

        var silencedProcesses = recentRejections
            .Where(f => f.ProcessName != null)
            .GroupBy(f => f.ProcessName!.ToLowerInvariant())
            .Where(g => g.Count() >= 3 || g.Any(x => x.CreatedAt >= threeDaysAgo))
            .Select(g => g.Key)
            .ToHashSet();

        var silencedDomains = recentRejections
            .Where(f => f.Domain != null)
            .GroupBy(f => f.Domain!.ToLowerInvariant())
            .Where(g => g.Count() >= 3 || g.Any(x => x.CreatedAt >= threeDaysAgo))
            .Select(g => g.Key)
            .ToHashSet();

        var categories = await _db.Set<PcCategoryEntity>().ToListAsync(ct);
        var categoriesByName = categories
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ActivityClassificationSuggestionV2Dto>();
        foreach (var entity in entities)
        {
            var appName = ExtractAppName(entity.ClusterKey);
            var domain = ExtractDomain(entity.ClusterKey);

            // Check silence
            if (appName is not null && silencedProcesses.Contains(appName.ToLowerInvariant()))
                continue;
            if (domain is not null && silencedDomains.Contains(domain.ToLowerInvariant()))
                continue;

            string? appDisplayName = null;
            string? appIcon = null;
            string? recommendedCategoryName = null;
            Guid? recommendedCategoryId = null;
            string? recommendedProductivity = null;
            double confidence = 0.85;
            string recognitionSource = "heuristic";
            bool isOnlineLookup = false;

            if (appName is not null)
            {
                var sig = await _appSignatures.LookupByProcessNameAsync(appName, ct);
                if (sig is not null)
                {
                    appDisplayName = sig.DisplayName;
                    appIcon = sig.Icon;
                    recognitionSource = sig.Source;
                    isOnlineLookup = string.Equals(sig.Source, "online", StringComparison.OrdinalIgnoreCase);
                    confidence = sig.Confidence;
                    if (!string.IsNullOrWhiteSpace(sig.CategoryPath))
                    {
                        var parts = sig.CategoryPath.Split("/");
                        recommendedCategoryName = parts[0];
                    }
                    recommendedProductivity = sig.Productivity;
                }
            }
            else if (domain is not null)
            {
                var inferred = InferDomainCategory(domain);
                if (inferred is not null)
                {
                    appDisplayName = domain;
                    recommendedCategoryName = inferred.Value.Category;
                    recommendedProductivity = inferred.Value.Productivity;
                    confidence = 0.90;
                    recognitionSource = "domain-knowledge";
                }
            }

            if (recommendedCategoryName is not null && categoriesByName.TryGetValue(recommendedCategoryName, out var catEntity))
            {
                recommendedCategoryId = catEntity.Id;
                recommendedCategoryName = catEntity.Name;
                recommendedProductivity ??= catEntity.Productivity;
            }

            result.Add(new ActivityClassificationSuggestionV2Dto(
                Id: entity.Id,
                ClusterKey: entity.ClusterKey,
                ProcessName: appName,
                Domain: domain,
                AppDisplayName: appDisplayName ?? appName ?? domain,
                AppIcon: appIcon,
                CurrentCategory: entity.CurrentCategory ?? "其他",
                RecommendedCategoryName: recommendedCategoryName ?? "其他",
                RecommendedCategoryId: recommendedCategoryId,
                RecommendedProductivity: recommendedProductivity ?? "neutral",
                Confidence: confidence,
                RecognitionSource: recognitionSource,
                IsOnlineLookup: isOnlineLookup,
                TotalDurationSeconds: entity.TotalDurationSeconds,
                SampleCount: entity.SampleCount,
                Status: entity.Status,
                CreatedAt: entity.CreatedAt));
        }

        return result;
    }

    public async Task<BatchAcceptResultDto> BatchAcceptAsync(BatchAcceptSuggestionsRequest req, CancellationToken ct)
    {
        int accepted = 0;
        int rulesCreated = 0;
        int failed = 0;

        foreach (var item in req.Items)
        {
            var entity = await _db.Set<ActivityClassificationSuggestionEntity>()
                .FirstOrDefaultAsync(s => s.Id == item.SuggestionId, ct);

            if (entity is null || entity.Status != PendingStatus)
            {
                failed++;
                continue;
            }

            var appName = ExtractAppName(entity.ClusterKey);
            var domain = ExtractDomain(entity.ClusterKey);
            var categoryName = item.CategoryName;

            if (string.IsNullOrWhiteSpace(categoryName) && item.CategoryId.HasValue)
            {
                var cat = await _db.Set<PcCategoryEntity>().FindAsync(new object[] { item.CategoryId.Value }, ct);
                categoryName = cat?.Name;
            }

            if (string.IsNullOrWhiteSpace(categoryName))
            {
                categoryName = "其他";
            }

            // Mark suggestion accepted
            entity.Status = "accepted";
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            accepted++;

            // Optionally create classification rule
            if (item.CreateRule)
            {
                var conditionsJson = "{}";
                var ruleName = "Rule: " + (appName ?? domain ?? entity.ClusterKey);

                if (appName is not null)
                {
                    conditionsJson = JsonSerializer.Serialize(new
                    {
                        all = new[]
                        {
                            new { field = "appNameNormalized", op = "equals", value = AppNameNormalizer.Normalize(appName) }
                        }
                    });
                }
                else if (domain is not null)
                {
                    conditionsJson = JsonSerializer.Serialize(new
                    {
                        all = new[]
                        {
                            new { field = "domain", op = "equals", value = domain.ToLowerInvariant() }
                        }
                    });
                }

                _db.Set<ActivityCategoryRuleEntity>().Add(new ActivityCategoryRuleEntity
                {
                    Id = Guid.NewGuid(),
                    RuleName = ruleName,
                    Scope = "activity",
                    CategoryName = categoryName,
                    CategoryId = item.CategoryId,
                    Color = CategoryLegacyMapper.UnifiedColors.TryGetValue(categoryName, out var col) ? col : "#64748b",
                    Priority = 500,
                    Source = "suggestion-batch-accept",
                    Status = "active",
                    ConditionsJson = conditionsJson,
                    Confidence = 0.95,
                    Explanation = "Batch accepted suggestion for " + entity.ClusterKey,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                rulesCreated++;
            }

            // Record positive feedback
            _db.Set<PcSuggestionFeedbackEntity>().Add(new PcSuggestionFeedbackEntity
            {
                Id = Guid.NewGuid(),
                ProcessName = appName,
                Domain = domain,
                AcceptedCategoryId = item.CategoryId,
                Action = "accepted",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
        return new BatchAcceptResultDto(accepted, rulesCreated, failed);
    }

    private static (string Category, string Productivity)? InferDomainCategory(string domain)
    {
        var d = domain.ToLowerInvariant();
        if (d.Contains("github.com") || d.Contains("gitlab.com") || d.Contains("gitee.com") || d.Contains("stackoverflow.com"))
            return (CategoryLegacyMapper.ProgrammingTinkering, "productive");
        if (d.Contains("bilibili.com") || d.Contains("youtube.com") || d.Contains("iqiyi.com") || d.Contains("youku.com"))
            return (CategoryLegacyMapper.Video, "distracting");
        if (d.Contains("leetcode.cn") || d.Contains("leetcode.com") || d.Contains("zhihu.com") || d.Contains("wikipedia.org"))
            return (CategoryLegacyMapper.Learning, "productive");
        if (d.Contains("notion.so") || d.Contains("docs.qq.com") || d.Contains("yuque.com"))
            return (CategoryLegacyMapper.Documents, "productive");
        return null;
    }

    private static string? ExtractDomain(string? clusterKey)
    {
        if (string.IsNullOrWhiteSpace(clusterKey))
            return null;
        if (clusterKey.StartsWith("web:", StringComparison.OrdinalIgnoreCase))
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
            ?? throw new KeyNotFoundException($"未找到活动分类建议：{id}。");
        EnsurePending(suggestion);

        throw new InvalidOperationException(
            "已禁用直接接受建议，请使用预览/应用分类流程。");
    }

    public async Task RejectSuggestionAsync(Guid id, CancellationToken ct)
    {
        var suggestion = await _db.Set<ActivityClassificationSuggestionEntity>()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"未找到活动分类建议：{id}。");
        EnsurePending(suggestion);

        suggestion.Status = "rejected";
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;

        // Record suggestion feedback
        var appName = ExtractAppName(suggestion.ClusterKey);
        var domain = ExtractDomain(suggestion.ClusterKey);
        _db.Set<PcSuggestionFeedbackEntity>().Add(new PcSuggestionFeedbackEntity
        {
            Id = Guid.NewGuid(),
            ProcessName = appName,
            Domain = domain,
            Action = "rejected",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }

    private static void EnsurePending(ActivityClassificationSuggestionEntity suggestion)
    {
        if (string.Equals(suggestion.Status, PendingStatus, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"活动分类建议 {suggestion.Id} 必须处于待处理状态后才能修改。当前状态：{suggestion.Status}。");
    }

    private static bool NeedsSuggestion(PcDetailRecord record, int recommendedMinimumMinutes)
    {
        var minimumDurationSeconds = recommendedMinimumMinutes * 60;
        if ((record.DurationSeconds ?? 0) < minimumDurationSeconds)
            return false;

        // REQ-5：未活动记录（gap / idle / afk）与采集端空闲哨兵（__IDLE__）不参与建议候选 ——
        // 空闲时段不是「某个应用的使用行为」，把它们聚成建议簇会让用户去给「空闲」建分类规则。
        if (!IsSuggestible(record))
            return false;

        return string.Equals(record.ClassificationSource, "fallback", StringComparison.OrdinalIgnoreCase)
            || (record.ClassificationConfidence is not null && record.ClassificationConfidence < 0.5);
    }

    private static bool IsSuggestible(PcDetailRecord record)
    {
        if (PcActivityOverlapResolver.IsInactive(record.RecordType))
            return false;

        return !IsIdleSentinelName(record.AppName)
            && !IsIdleSentinelName(record.BrowserAppName)
            && !IsIdleSentinelName(record.DisplayName);
    }

    private static bool IsIdleSentinelName(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && string.Equals(value.Trim(), IdleSentinelAppName, StringComparison.OrdinalIgnoreCase);

    private static string? GetClusterKey(PcDetailRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Domain))
            return $"web:{record.Domain.Trim().ToLowerInvariant()}";

        var app = record.AppName ?? record.BrowserAppName;
        if (string.IsNullOrWhiteSpace(app))
            return null;

        if (IsIdleSentinelName(app))
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
            entity.Status,
            GeneratedForDate: ResolveStoredGeneratedForDate(entity));
    }

    /// <summary>
    /// 建议的业务日：优先用落库的 <c>generated_for_date</c>；历史遗留行没有该列时，
    /// 依次回退到「样本里最新的时刻」与「最后一次刷新时刻」，保证每条建议都能归日（AC-5.1）。
    /// </summary>
    private static string ResolveStoredGeneratedForDate(ActivityClassificationSuggestionEntity entity)
    {
        var date = entity.GeneratedForDate
            ?? ParseLatestSampleBusinessDay(entity.SampleRecordsJson)
            ?? PcTrackerService.GetBusinessDayForTimestamp(entity.UpdatedAt);

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseLatestSampleBusinessDay(string? sampleRecordsJson)
    {
        if (string.IsNullOrWhiteSpace(sampleRecordsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(sampleRecordsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            DateTime? latest = null;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var property in element.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "start", StringComparison.OrdinalIgnoreCase)
                        || property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!DateTimeOffset.TryParse(
                            property.Value.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var start))
                    {
                        continue;
                    }

                    var businessDay = PcTrackerService.GetBusinessDayForTimestamp(start);
                    if (latest is null || businessDay > latest)
                        latest = businessDay;
                }
            }

            return latest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ActivityClassificationRuleDto ToRuleDto(ActivityCategoryRuleEntity rule)
    {
        return new ActivityClassificationRuleDto(
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
    }
}
