using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Pim.Infrastructure.Auth;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivityClassificationRecomputeService
{
    private const string DefaultCategoryName = "\u5176\u4ed6";
    private const string DefaultCategoryColor = "#64748b";

    private readonly PimDbContext _db;
    private readonly ActivityClassificationSnapshotService _snapshots;
    private readonly ActivityClassificationRuleService _rules;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ActivityClassificationRecomputeService> _logger;

    public ActivityClassificationRecomputeService(
        PimDbContext db,
        ActivityClassificationSnapshotService snapshots,
        ActivityClassificationRuleService rules,
        ICurrentUserService currentUser,
        ILogger<ActivityClassificationRecomputeService> logger)
    {
        _db = db;
        _snapshots = snapshots;
        _rules = rules;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<ActivityClassificationPreviewDto> PreviewRuleAsync(
        SaveActivityClassificationRuleRequest ruleRequest,
        ActivityClassificationApplyRangeRequest range,
        CancellationToken ct)
    {
        await _rules.ValidateAsync(ruleRequest, ensureUniqueRuleName: false, ct);
        var existingRules = await _rules.LoadActiveAsync(ct);
        var records = await LoadActivityRecordsAsync(range, existingRules, ct);
        var candidateRule = ActivityClassificationRuleService.ToEntity(ruleRequest);
        var afterRules = OrderRules(existingRules.Append(candidateRule)).ToList();
        var protectedSnapshots = await LoadProtectedSnapshotsAsync(records, ct);
        var affected = records
            .Select(record => new PreviewRecord(
                record,
                ClassifyForPreview(record, existingRules, protectedSnapshots),
                ClassifyForPreview(record, afterRules, protectedSnapshots)))
            .Where(item => HasMeaningfulChange(item.CurrentClassification, item.AfterClassification))
            .ToList();

        var currentCategoryCounts = affected
            .GroupBy(item => item.CurrentClassification.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var newCategoryCounts = affected
            .GroupBy(item => item.AfterClassification.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var affectedDurationSeconds = affected.Sum(item => item.Record.DurationSeconds ?? 0);
        var samples = affected
            .Take(5)
            .Select(item => ApplyClassification(item.Record, item.AfterClassification))
            .ToList();

        return new ActivityClassificationPreviewDto(
            affected.Count,
            affectedDurationSeconds,
            currentCategoryCounts,
            newCategoryCounts,
            samples,
            affected.Count > 0,
            BuildSummary(affected.Count, affectedDurationSeconds, range));
    }

    public async Task<ActivityClassificationPreviewDto> ApplyRuleAsync(
        SaveActivityClassificationRuleRequest ruleRequest,
        ActivityClassificationApplyRangeRequest range,
        CancellationToken ct)
    {
        var preview = await PreviewRuleAsync(ruleRequest, range, ct);
        var result = await ApplyRuleCoreAsync(ruleRequest, range, preview, suggestionId: null, afterApply: null, ct);
        return result.Preview;
    }

    public async Task<ActivityClassificationSuggestionPreviewDto> PreviewSuggestionAsync(
        Guid suggestionId,
        SuggestionClassificationPreviewRequest request,
        ClassificationRuleDraftService drafts,
        CancellationToken ct)
    {
        var rule = await drafts.BuildSuggestionDraftAsync(suggestionId, request, ct);
        var preview = await PreviewRuleAsync(rule, request.Range, ct);
        return new ActivityClassificationSuggestionPreviewDto(rule, preview);
    }

    public async Task<ActivityClassificationSuggestionApplyDto> ApplySuggestionAsync(
        Guid suggestionId,
        SuggestionClassificationApplyRequest request,
        ClassificationRuleDraftService drafts,
        CancellationToken ct)
    {
        var previewRequest = new SuggestionClassificationPreviewRequest(
            request.CategoryName,
            request.ProjectTag,
            request.Range);
        var rule = await drafts.BuildSuggestionDraftAsync(suggestionId, previewRequest, ct);
        var preview = await PreviewRuleAsync(rule, request.Range, ct);
        var result = await ApplyRuleCoreAsync(rule, request.Range, preview, suggestionId, afterApply: null, ct);

        return ToSuggestionApplyDto(result);
    }

    public async Task<(ActivityClassificationSuggestionApplyDto Applied, TSideEffect SideEffect)> ApplySuggestionWithSideEffectAsync<TSideEffect>(
        Guid suggestionId,
        SuggestionClassificationApplyRequest request,
        ClassificationRuleDraftService drafts,
        Func<ActivityClassificationSuggestionApplyDto, CancellationToken, Task<TSideEffect>> afterApply,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(afterApply);

        var previewRequest = new SuggestionClassificationPreviewRequest(
            request.CategoryName,
            request.ProjectTag,
            request.Range);
        var rule = await drafts.BuildSuggestionDraftAsync(suggestionId, previewRequest, ct);
        var preview = await PreviewRuleAsync(rule, request.Range, ct);
        TSideEffect? sideEffect = default;
        var result = await ApplyRuleCoreAsync(
            rule,
            request.Range,
            preview,
            suggestionId,
            async (coreResult, token) =>
            {
                var applied = ToSuggestionApplyDto(coreResult);
                sideEffect = await afterApply(applied, token);
            },
            ct);

        return (ToSuggestionApplyDto(result), sideEffect!);
    }

    public async Task<ActivityClassificationRecomputeDto> RecomputeAsync(
        ActivityClassificationApplyRangeRequest range,
        CancellationToken ct)
    {
        var (start, end) = ParseRange(range);
        var rules = await _rules.LoadActiveAsync(ct);
        var records = await LoadActivityRecordsAsync(start, end, rules, ct);
        var duration = records.Sum(record => record.DurationSeconds ?? 0);

        return await ExecuteInTransactionAsync(async token =>
        {
            var audit = CreatePcAudit(
                "range.recompute",
                range,
                records,
                records.Count,
                duration,
                ruleId: null,
                suggestionId: null);
            _db.Set<ActivityClassificationAuditEntity>().Add(audit);
            await _db.SaveChangesAsync(token);

            await EnsureSnapshotsForRangeAsync(start, end, audit.Id, token);

            return new ActivityClassificationRecomputeDto(
                records.Count,
                duration,
                audit.Id,
                $"\u5df2\u91cd\u7b97 {records.Count} \u6761\u8bb0\u5f55\uff0c\u8303\u56f4\uff1a{FormatRangeMode(range.Mode)}\u3002");
        }, ct);
    }

    /// <summary>共享核心：加载窗口内事件（Duration &gt; 0）→ BuildInterpretedAwRecords → EnsureClassificationsAsync。
    /// 手动 recompute 先写审计再把 auditId 传入（快照可溯源）；后台定时补齐传 null 不写审计。
    /// 返回加载的记录数，便于调用方记录统计。</summary>
    public async Task<int> EnsureSnapshotsForRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? auditId,
        CancellationToken ct)
    {
        var rules = await _rules.LoadActiveAsync(ct);
        var records = await LoadActivityRecordsAsync(startUtc, endUtc, rules, ct);
        await _snapshots.EnsureClassificationsAsync(records, rules, auditId, ct);
        return records.Count;
    }

    private async Task<ApplyRuleCoreResult> ApplyRuleCoreAsync(
        SaveActivityClassificationRuleRequest ruleRequest,
        ActivityClassificationApplyRangeRequest range,
        ActivityClassificationPreviewDto preview,
        Guid? suggestionId,
        Func<ApplyRuleCoreResult, CancellationToken, Task>? afterApply,
        CancellationToken ct)
    {
        await _rules.ValidateAsync(ruleRequest, ensureUniqueRuleName: true, ct);

        return await ExecuteInTransactionAsync(async token =>
        {
            var rule = ActivityClassificationRuleService.ToEntity(ruleRequest);
            var pcAuditId = Guid.NewGuid();
            var plannedResult = new ApplyRuleCoreResult(rule, preview, pcAuditId, "accepted");
            if (afterApply is not null)
                await afterApply(plannedResult, token);

            string? suggestionStatus = null;
            if (suggestionId is Guid id)
                suggestionStatus = await MarkSuggestionAcceptedAsync(id, token);

            _db.Set<ActivityCategoryRuleEntity>().Add(rule);

            AddAuditLog(new CreateAuditLogRequest(
                _currentUser.UserId,
                _currentUser.UserId is null ? AuditActorType.System : AuditActorType.User,
                "pc.classification.rule.apply",
                "pc_activity_category_rule",
                rule.Id.ToString(),
                "pc-tracker",
                AuditResult.Success,
                null,
                null,
                null,
                new Dictionary<string, string>
                {
                    ["ruleId"] = rule.Id.ToString(),
                    ["suggestionId"] = suggestionId?.ToString() ?? string.Empty,
                    ["rangeMode"] = range.Mode,
                    ["dateFrom"] = range.DateFrom ?? string.Empty,
                    ["dateTo"] = range.DateTo ?? string.Empty,
                    ["affectedRecordCount"] = preview.AffectedRecordCount.ToString(),
                    ["affectedDurationSeconds"] = preview.AffectedDurationSeconds.ToString("R"),
                    ["initiatedBy"] = "web"
                },
                null,
                null));

            var rules = OrderRules((await _rules.LoadActiveAsync(token)).Append(rule)).ToList();
            var records = await LoadActivityRecordsAsync(range, rules, token);
            var pcAudit = CreatePcAudit(
                "rule.apply",
                range,
                records,
                preview.AffectedRecordCount,
                preview.AffectedDurationSeconds,
                rule.Id,
                suggestionId,
                pcAuditId);
            _db.Set<ActivityClassificationAuditEntity>().Add(pcAudit);
            await _snapshots.EnsureClassificationsAsync(records, rules, pcAudit.Id, token, saveChanges: false);

            var result = new ApplyRuleCoreResult(rule, preview, pcAudit.Id, suggestionStatus);

            await _db.SaveChangesAsync(token);

            return result;
        }, ct);
    }

    public async Task<List<ActivityCategoryRuleEntity>> LoadActiveRulesAsync(CancellationToken ct)
    {
        return await _rules.LoadActiveAsync(ct);
    }

    private async Task<string> MarkSuggestionAcceptedAsync(Guid suggestionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var suggestion = await _db.Set<ActivityClassificationSuggestionEntity>()
                .FirstOrDefaultAsync(item => item.Id == suggestionId, ct)
                ?? throw new KeyNotFoundException($"未找到活动分类建议：{suggestionId}。");

            if (!string.Equals(suggestion.Status, "pending", StringComparison.Ordinal))
                throw new InvalidOperationException($"分类建议 {suggestionId} 必须处于待处理状态后才能应用。");

            suggestion.Status = "accepted";
            suggestion.UpdatedAt = now;
            return suggestion.Status;
        }

        var updated = await _db.Set<ActivityClassificationSuggestionEntity>()
            .Where(item => item.Id == suggestionId && item.Status == "pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "accepted")
                .SetProperty(item => item.UpdatedAt, now), ct);

        if (updated == 1)
            return "accepted";

        var exists = await _db.Set<ActivityClassificationSuggestionEntity>()
            .AnyAsync(item => item.Id == suggestionId, ct);
        if (!exists)
            throw new KeyNotFoundException($"未找到活动分类建议：{suggestionId}。");

        throw new InvalidOperationException($"分类建议 {suggestionId} 必须处于待处理状态后才能应用。");
    }

    private async Task<List<PcDetailRecord>> LoadActivityRecordsAsync(
        ActivityClassificationApplyRangeRequest range,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        CancellationToken ct)
    {
        var (start, end) = ParseRange(range);
        return await LoadActivityRecordsAsync(start, end, rules, ct);
    }

    private async Task<List<PcDetailRecord>> LoadActivityRecordsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        CancellationToken ct)
    {
        var events = await _db.Set<AwEventEntity>()
            .Where(e => e.Duration > 0)
            .Where(e => e.Timestamp >= startUtc && e.Timestamp < endUtc)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return BrowserPageTimelineBuilder.BuildInterpretedAwRecords(events, rules);
    }

    private ActivityClassificationAuditEntity CreatePcAudit(
        string operation,
        ActivityClassificationApplyRangeRequest range,
        IReadOnlyCollection<PcDetailRecord> records,
        int affectedRecordCount,
        double affectedDurationSeconds,
        Guid? ruleId,
        Guid? suggestionId,
        Guid? auditId = null) =>
        new()
        {
            Id = auditId ?? Guid.NewGuid(),
            Operation = operation,
            RuleId = ruleId,
            SuggestionId = suggestionId,
            RangeMode = range.Mode,
            DateFrom = range.DateFrom,
            DateTo = range.DateTo,
            AffectedRecordCount = affectedRecordCount,
            AffectedDurationSeconds = affectedDurationSeconds,
            AffectedRecordKeysJson = JsonSerializer.Serialize(records
                .Select(ActivityClassificationRecordKey.FromRecord)
                .Distinct(StringComparer.Ordinal)
                .ToList()),
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private void AddAuditLog(CreateAuditLogRequest request)
    {
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = request.UserId,
            ActorType = request.ActorType.ToString(),
            Action = request.Action,
            ResourceType = request.ResourceType,
            ResourceId = request.ResourceId,
            Source = request.Source,
            Result = request.Result.ToString(),
            IpAddress = request.IpAddress,
            UserAgent = request.UserAgent,
            CorrelationId = request.CorrelationId,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>()),
            ErrorCode = request.ErrorCode,
            ErrorMessage = request.ErrorMessage,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ParseRange(ActivityClassificationApplyRangeRequest range)
    {
        var mode = range.Mode?.Trim().ToLowerInvariant();
        if (mode == "today")
        {
            if (string.IsNullOrWhiteSpace(range.DateFrom) || !string.Equals(range.DateFrom, range.DateTo, StringComparison.Ordinal))
                throw new ArgumentException("今天模式需要 DateFrom 和 DateTo 明确且相同。");

            var date = TryParseDate(range.DateFrom, nameof(range.DateFrom));
            var start = PcTrackerService.GetBusinessDayStartForQuery(date);
            return (start, start.AddDays(1));
        }

        if (mode == "range")
        {
            if (string.IsNullOrWhiteSpace(range.DateFrom) || string.IsNullOrWhiteSpace(range.DateTo))
                throw new ArgumentException("日期范围模式需要 DateFrom 和 DateTo。");

            var rangeStartDate = TryParseDate(range.DateFrom, nameof(range.DateFrom));
            var rangeEndDate = TryParseDate(range.DateTo, nameof(range.DateTo));
            var rangeStart = PcTrackerService.GetBusinessDayStartForQuery(rangeStartDate);
            var rangeEnd = PcTrackerService.GetBusinessDayStartForQuery(rangeEndDate).AddDays(1);
            if (rangeEnd <= rangeStart)
                throw new ArgumentException("DateTo 必须晚于或等于 DateFrom。");

            return (rangeStart, rangeEnd);
        }

        throw new ArgumentException($"未知的范围模式：{range.Mode}。");
    }

    private static DateTime TryParseDate(string? value, string fieldName)
    {
        if (!DateTime.TryParse(value, out var parsed))
            throw new ArgumentException($"{fieldName} 必须是有效日期。");

        return parsed.Date;
    }

    private async Task<Dictionary<string, ActivityClassificationEntity>> LoadProtectedSnapshotsAsync(
        IReadOnlyCollection<PcDetailRecord> records,
        CancellationToken ct)
    {
        var keys = records
            .Select(ActivityClassificationRecordKey.FromRecord)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0)
            return new Dictionary<string, ActivityClassificationEntity>(StringComparer.Ordinal);

        var snapshots = await _db.Set<ActivityClassificationEntity>()
            .Where(snapshot => keys.Contains(snapshot.RecordKey))
            .ToListAsync(ct);

        return snapshots
            .Where(IsProtectedSnapshot)
            .ToDictionary(snapshot => snapshot.RecordKey, StringComparer.Ordinal);
    }

    private ActivityClassificationResult ClassifyForPreview(
        PcDetailRecord record,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        IReadOnlyDictionary<string, ActivityClassificationEntity> protectedSnapshots)
    {
        var recordKey = ActivityClassificationRecordKey.FromRecord(record);
        return protectedSnapshots.TryGetValue(recordKey, out var snapshot)
            ? ToClassificationResult(snapshot)
            : ActivityClassifier.Classify(ToContext(record), rules, _logger);
    }

    private async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            return await operation(ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await operation(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    private static IEnumerable<ActivityCategoryRuleEntity> OrderRules(IEnumerable<ActivityCategoryRuleEntity> rules) =>
        rules
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.RuleName)
            .ThenBy(rule => rule.Id);

    private static bool IsProtectedSnapshot(ActivityClassificationEntity snapshot) =>
        snapshot.Source is "manual" or "corrected" or "user_corrected" or "llm_corrected"
        || string.Equals(snapshot.Source, "manual", StringComparison.OrdinalIgnoreCase)
        || string.Equals(snapshot.Source, "corrected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(snapshot.Source, "user_corrected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(snapshot.Source, "llm_corrected", StringComparison.OrdinalIgnoreCase);

    private static ActivityClassificationResult ToClassificationResult(ActivityClassificationEntity snapshot) =>
        new(
            snapshot.CategoryName,
            snapshot.CategoryColor,
            snapshot.ProjectTag,
            snapshot.Confidence,
            snapshot.Source,
            snapshot.Explanation,
            snapshot.SourceRuleId);

    private static ActivityClassificationContext ToContext(PcDetailRecord record)
    {
        var normalizedApp = AppNameNormalizer.Normalize(record.AppName ?? record.BrowserAppName ?? record.DisplayName);
        return new ActivityClassificationContext(
            record.RecordType,
            record.AppName ?? record.BrowserAppName,
            normalizedApp,
            record.Domain,
            record.Path,
            record.Title,
            record.BrowserWindowTitle ?? record.Title,
            record.IsLocalFile ? record.Path : null,
            record.BucketType);
    }

    private static PcDetailRecord ApplyClassification(
        PcDetailRecord record,
        ActivityClassificationResult classification) =>
        record with
        {
            CategoryName = classification.CategoryName,
            CategoryColor = classification.CategoryColor,
            ProjectTag = classification.ProjectTag,
            ClassificationConfidence = classification.Confidence,
            ClassificationSource = classification.Source,
            ClassificationExplanation = classification.Explanation
        };

    private static bool HasMeaningfulChange(
        ActivityClassificationResult current,
        ActivityClassificationResult after) =>
        !string.Equals(current.CategoryName, after.CategoryName, StringComparison.Ordinal)
        || !string.Equals(current.ProjectTag, after.ProjectTag, StringComparison.Ordinal)
        || current.SourceRuleId != after.SourceRuleId
        || !string.Equals(current.Source, after.Source, StringComparison.Ordinal)
        || !string.Equals(current.CategoryColor, after.CategoryColor, StringComparison.Ordinal)
        || Math.Abs(current.Confidence - after.Confidence) > 0.000001;

    private static string BuildSummary(
        int affectedRecordCount,
        double affectedDurationSeconds,
        ActivityClassificationApplyRangeRequest range) =>
        $"\u672c\u6b21\u4f1a\u5f71\u54cd {affectedRecordCount} \u6761\u8bb0\u5f55\uff08{affectedDurationSeconds:R} \u79d2\uff09\uff0c\u8303\u56f4\uff1a{FormatRangeMode(range.Mode)}\u3002";

    private static string FormatRangeMode(string? mode) =>
        string.Equals(mode, "today", StringComparison.OrdinalIgnoreCase)
            ? "\u4eca\u5929"
            : string.Equals(mode, "range", StringComparison.OrdinalIgnoreCase)
                ? "\u81ea\u5b9a\u4e49\u8303\u56f4"
                : mode ?? "\u672a\u77e5";

    private static ActivityClassificationSuggestionApplyDto ToSuggestionApplyDto(ApplyRuleCoreResult result) =>
        new(
            ActivityClassificationRuleService.ToDto(result.Rule),
            result.Preview,
            result.AuditId,
            result.SuggestionStatus ?? "accepted");

    private sealed record PreviewRecord(
        PcDetailRecord Record,
        ActivityClassificationResult CurrentClassification,
        ActivityClassificationResult AfterClassification);

    private sealed record ApplyRuleCoreResult(
        ActivityCategoryRuleEntity Rule,
        ActivityClassificationPreviewDto Preview,
        Guid AuditId,
        string? SuggestionStatus);
}
