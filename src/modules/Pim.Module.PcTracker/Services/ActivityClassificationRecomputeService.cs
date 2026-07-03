using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Auth;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using System.Text.RegularExpressions;

namespace Pim.Module.PcTracker.Services;

public class ActivityClassificationRecomputeService
{
    private const string DefaultCategoryName = "\u5176\u4ed6";
    private const string DefaultCategoryColor = "#64748b";

    private readonly PimDbContext _db;
    private readonly ActivityClassificationSnapshotService _snapshots;
    private readonly IAuditLogService _auditLog;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ActivityClassificationRecomputeService> _logger;

    public ActivityClassificationRecomputeService(
        PimDbContext db,
        ActivityClassificationSnapshotService snapshots,
        IAuditLogService auditLog,
        ICurrentUserService currentUser,
        ILogger<ActivityClassificationRecomputeService> logger)
    {
        _db = db;
        _snapshots = snapshots;
        _auditLog = auditLog;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<ActivityClassificationPreviewDto> PreviewRuleAsync(
        SaveActivityClassificationRuleRequest ruleRequest,
        ActivityClassificationApplyRangeRequest range,
        CancellationToken ct)
    {
        ValidateRuleRequest(ruleRequest);
        var existingRules = await LoadActiveRulesAsync(ct);
        var records = await LoadActivityRecordsAsync(range, existingRules, ct);
        var candidateRule = ToRule(ruleRequest);
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
        ValidateRuleRequest(ruleRequest);
        await EnsureUniqueRuleNameAsync(ruleRequest.RuleName, ct);

        var preview = await PreviewRuleAsync(ruleRequest, range, ct);
        var rule = ToRule(ruleRequest);

        await using var transaction = await BeginTransactionIfSupportedAsync(ct);
        try
        {
            _db.Set<ActivityCategoryRuleEntity>().Add(rule);
            await _db.SaveChangesAsync(ct);

            var audit = await _auditLog.RecordAsync(new CreateAuditLogRequest(
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
                    ["rangeMode"] = range.Mode,
                    ["dateFrom"] = range.DateFrom ?? string.Empty,
                    ["dateTo"] = range.DateTo ?? string.Empty,
                    ["affectedRecordCount"] = preview.AffectedRecordCount.ToString(),
                    ["affectedDurationSeconds"] = preview.AffectedDurationSeconds.ToString("R"),
                    ["initiatedBy"] = "web"
                },
                null,
                null), ct);

            var rules = await LoadActiveRulesAsync(ct);
            var records = await LoadActivityRecordsAsync(range, rules, ct);
            await _snapshots.EnsureClassificationsAsync(records, rules, audit.Id, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);

            throw;
        }

        return preview;
    }

    public async Task<List<ActivityCategoryRuleEntity>> LoadActiveRulesAsync(CancellationToken ct)
    {
        return await _db.Set<ActivityCategoryRuleEntity>()
            .Where(rule => rule.Status == "active")
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.RuleName)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);
    }

    private async Task<List<PcDetailRecord>> LoadActivityRecordsAsync(
        ActivityClassificationApplyRangeRequest range,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        CancellationToken ct)
    {
        var (start, end) = ParseRange(range);
        var events = await _db.Set<AwEventEntity>()
            .Where(e => e.Duration > 0)
            .Where(e => e.Timestamp >= start && e.Timestamp < end)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return BrowserPageTimelineBuilder.BuildInterpretedAwRecords(events, rules);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ParseRange(ActivityClassificationApplyRangeRequest range)
    {
        var mode = range.Mode?.Trim().ToLowerInvariant();
        if (mode == "today")
        {
            if (string.IsNullOrWhiteSpace(range.DateFrom) || !string.Equals(range.DateFrom, range.DateTo, StringComparison.Ordinal))
                throw new ArgumentException("Today mode requires explicit matching DateFrom and DateTo.");

            var date = TryParseDate(range.DateFrom, nameof(range.DateFrom));
            var start = PcTrackerService.GetBusinessDayStartForQuery(date);
            return (start, start.AddDays(1));
        }

        if (mode == "range")
        {
            if (string.IsNullOrWhiteSpace(range.DateFrom) || string.IsNullOrWhiteSpace(range.DateTo))
                throw new ArgumentException("Range mode requires DateFrom and DateTo.");

            var rangeStartDate = TryParseDate(range.DateFrom, nameof(range.DateFrom));
            var rangeEndDate = TryParseDate(range.DateTo, nameof(range.DateTo));
            var rangeStart = PcTrackerService.GetBusinessDayStartForQuery(rangeStartDate);
            var rangeEnd = PcTrackerService.GetBusinessDayStartForQuery(rangeEndDate).AddDays(1);
            if (rangeEnd <= rangeStart)
                throw new ArgumentException("DateTo must be on or after DateFrom.");

            return (rangeStart, rangeEnd);
        }

        throw new ArgumentException($"Unknown range mode '{range.Mode}'.");
    }

    private static DateTime TryParseDate(string? value, string fieldName)
    {
        if (!DateTime.TryParse(value, out var parsed))
            throw new ArgumentException($"{fieldName} must be a valid date.");

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

    private async Task EnsureUniqueRuleNameAsync(string ruleName, CancellationToken ct)
    {
        var exists = await _db.Set<ActivityCategoryRuleEntity>()
            .AnyAsync(rule => rule.RuleName == ruleName, ct);

        if (exists)
            throw new InvalidOperationException($"Activity classification rule '{ruleName}' already exists.");
    }

    private static void ValidateRuleRequest(SaveActivityClassificationRuleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuleName))
            throw new ArgumentException("RuleName is required.");

        ValidateConditionsJson(request.ConditionsJson);
    }

    private static void ValidateConditionsJson(string? conditionsJson)
    {
        if (string.IsNullOrWhiteSpace(conditionsJson))
            throw new ArgumentException("ConditionsJson is required.");

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(conditionsJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("all", out var allConditions)
                || allConditions.ValueKind != System.Text.Json.JsonValueKind.Array
                || allConditions.GetArrayLength() == 0)
                throw new ArgumentException("ConditionsJson must contain a non-empty all array.");

            foreach (var condition in allConditions.EnumerateArray())
            {
                if (condition.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !TryGetStringProperty(condition, "field", out var field)
                    || !TryGetStringProperty(condition, "op", out var op)
                    || !condition.TryGetProperty("value", out _))
                    throw new ArgumentException("Each condition must include field, op, and value.");

                if (!AllowedConditionFields.Contains(field) || !AllowedConditionOps.Contains(op))
                    throw new ArgumentException("ConditionsJson contains an unsupported condition.");

                ValidateConditionValue(op, condition.GetProperty("value"));
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException("ConditionsJson must be valid JSON.", ex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException("Regex condition value must be a valid regular expression.", ex);
        }
        catch (ArgumentException)
        {
            throw;
        }
    }

    private static void ValidateConditionValue(string op, System.Text.Json.JsonElement value)
    {
        if (op == "containsAny")
        {
            if (value.ValueKind != System.Text.Json.JsonValueKind.Array || value.GetArrayLength() == 0)
                throw new ArgumentException("containsAny requires a non-empty string array value.");

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new ArgumentException("containsAny requires non-empty string values.");
            }

            return;
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"{op} requires a non-empty string value.");

        if (op == "regex")
            _ = new Regex(value.GetString()!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static bool TryGetStringProperty(
        System.Text.Json.JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != System.Text.Json.JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            return false;

        value = property.GetString()!;
        return true;
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync(CancellationToken ct)
    {
        return _db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory"
            ? null
            : await _db.Database.BeginTransactionAsync(ct);
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

    private static ActivityCategoryRuleEntity ToRule(SaveActivityClassificationRuleRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = request.RuleName,
            Scope = request.Scope,
            CategoryName = request.CategoryName,
            ProjectTag = request.ProjectTag,
            Color = request.Color,
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
        $"Affected {affectedRecordCount} records ({affectedDurationSeconds:R}s) for {range.Mode}.";

    private sealed record PreviewRecord(
        PcDetailRecord Record,
        ActivityClassificationResult CurrentClassification,
        ActivityClassificationResult AfterClassification);

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

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
}
