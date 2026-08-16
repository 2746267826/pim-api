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
        new("其他", "#64748b", null, 0.2, "fallback", "没有匹配到规则或启发式分类。");
}

public record ActivityClassificationRuleDto(
    Guid Id,
    string RuleName,
    string Scope,
    string? CategoryName,
    Guid? CategoryId,
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

public record SuggestionClassificationPreviewRequest(
    string? CategoryName,
    string? ProjectTag,
    ActivityClassificationApplyRangeRequest Range);

public record SuggestionClassificationApplyRequest(
    string? CategoryName,
    string? ProjectTag,
    ActivityClassificationApplyRangeRequest Range);

public record ActivityClassificationSuggestionPreviewDto(
    SaveActivityClassificationRuleRequest Rule,
    ActivityClassificationPreviewDto Preview);

public record ActivityClassificationSuggestionApplyDto(
    ActivityClassificationRuleDto Rule,
    ActivityClassificationPreviewDto Preview,
    Guid AuditId,
    string SuggestionStatus);

public record ActivityClassificationRecomputeRequest(
    ActivityClassificationApplyRangeRequest Range);

public record ActivityClassificationRecomputeDto(
    int RecomputedRecordCount,
    double RecomputedDurationSeconds,
    Guid AuditId,
    string Summary);

public record PcActivityAnalysisResponse(
    string Date,
    int BlockMinutes,
    IReadOnlyList<PcActivityAnalysisBlockDto> Blocks);

public record PcActivityAnalysisBlockDto(
    string Start,
    string End,
    int IntensityScore,
    double ActiveDurationSeconds,
    int PendingClassificationCount,
    int ContextSwitchCount,
    int CategoryChangeCount,
    IReadOnlyList<PcActivityAnalysisCategoryDto> Categories,
    IReadOnlyList<PcActivityAnalysisAppDto> Apps);

public record PcActivityAnalysisCategoryDto(
    string CategoryName,
    string Color,
    double DurationSeconds);

public record PcActivityAnalysisAppDto(
    string AppName,
    double DurationSeconds);

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

public record AppKnowledgeAppDto(
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
    DateTimeOffset CreatedAt,
    int ContextCount,
    int PendingContextCount,
    double RecentAffectedDurationSeconds);

public record AppKnowledgeContextDto(
    Guid Id,
    Guid? AppId,
    string ProcessName,
    string PatternType,
    string PatternValue,
    string? TargetCategoryName,
    string? ProjectTag,
    string ScopeSummary,
    string Source,
    double Confidence,
    bool Enabled,
    int AffectedRecordCount,
    double AffectedDurationSeconds,
    DateTimeOffset? LastMatchedAt);

public record SaveAppKnowledgeContextRequest(
    Guid? AppId,
    string ProcessName,
    string PatternType,
    string PatternValue,
    string? TargetCategoryName,
    string? ProjectTag,
    double? Confidence,
    bool? Enabled);

public record AppKnowledgeSuggestionPreviewDto(
    Guid SuggestionId,
    AppKnowledgeContextDto RecommendedContext,
    IReadOnlyList<AppKnowledgeContextDto> Alternatives,
    ActivityClassificationPreviewDto Preview);

public record AppKnowledgeSuggestionApplyDto(
    Guid SuggestionId,
    AppKnowledgeContextDto SavedContext,
    ActivityClassificationPreviewDto Preview,
    Guid AuditId,
    string SuggestionStatus,
    string Message);
