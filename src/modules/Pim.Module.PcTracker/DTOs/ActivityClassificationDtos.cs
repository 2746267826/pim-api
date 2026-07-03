namespace Pim.Module.PcTracker.DTOs;

public record ActivityClassificationResult(
    string CategoryName,
    string CategoryColor,
    string? ProjectTag,
    double Confidence,
    string Source,
    string Explanation,
    Guid? SourceRuleId = null)
{
    public static ActivityClassificationResult Fallback() =>
        new("其他", "#64748b", null, 0.2, "fallback", "No rule or heuristic matched.");
}

public record ActivityClassificationRuleDto(
    Guid Id,
    string RuleName,
    string Scope,
    string? CategoryName,
    string? ProjectTag,
    string Color,
    int Priority,
    string Source,
    string Status,
    string ConditionsJson,
    double Confidence,
    string? Explanation);

public record SaveActivityClassificationRuleRequest(
    string RuleName,
    string Scope,
    string? CategoryName,
    string? ProjectTag,
    string Color,
    int Priority,
    string ConditionsJson,
    double Confidence,
    string? Explanation);

public record ActivityClassificationSuggestionDto(
    Guid Id,
    string ClusterKey,
    int SampleCount,
    double TotalDurationSeconds,
    string SampleRecordsJson,
    string SanitizedContextJson,
    string? CurrentCategory,
    string? SuggestedCategory,
    string? SuggestedProjectTag,
    string? SuggestedRulesJson,
    string? UserFeedback,
    string? LlmResponseJson,
    string Status,
    string? AppDisplayName = null,
    string? AppIcon = null,
    string? RecognitionSource = null);

public record AcceptActivityClassificationSuggestionRequest(
    string RuleName,
    string Scope,
    string? CategoryName,
    string? ProjectTag,
    string Color,
    int Priority,
    string ConditionsJson,
    double Confidence,
    string? Explanation);

public record ActivityClassificationSettingsDto(
    int RecommendedMinimumClassificationDurationMinutes,
    IReadOnlyList<int> SupportedRecommendedMinimumDurations);

public record SaveActivityClassificationSettingsRequest(
    int RecommendedMinimumClassificationDurationMinutes);

public record ActivityClassificationApplyRangeRequest(
    string Mode,
    string? DateFrom,
    string? DateTo);

public record ActivityClassificationPreviewRequest(
    SaveActivityClassificationRuleRequest Rule,
    ActivityClassificationApplyRangeRequest Range);

public record ActivityClassificationPreviewDto(
    int AffectedRecordCount,
    double AffectedDurationSeconds,
    IReadOnlyDictionary<string, int> CurrentCategoryCounts,
    IReadOnlyDictionary<string, int> NewCategoryCounts,
    IReadOnlyList<PcDetailRecord> Samples,
    bool RequiresConfirmation,
    string Summary);

public record ApplyActivityClassificationRuleRequest(
    SaveActivityClassificationRuleRequest Rule,
    ActivityClassificationApplyRangeRequest Range);

// App Knowledge Base

public record AppSignatureDto(
    Guid Id,
    string ProcessName,
    string DisplayName,
    string? CategoryPath,
    string? Productivity,
    string? Description,
    string Source,
    double Confidence,
    string? Icon,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);

public record SaveAppSignatureRequest(
    string ProcessName,
    string DisplayName,
    string? CategoryPath,
    string? Productivity,
    string? Description);
