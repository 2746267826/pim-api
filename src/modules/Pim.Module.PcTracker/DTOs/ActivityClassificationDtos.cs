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
    /// <summary>
    /// 「未活动」结论的类别名（#331）。gap / idle / afk 表示「这里没有人」，
    /// 不是某个应用的使用行为，因此不能落到「游戏」之类「应用类别」上。
    /// 与「其他」区分开，便于下游识别空档而不是把空档混进中性活动统计。
    /// </summary>
    public const string InactiveCategoryName = "未活动";

    /// <summary>「未活动」结论的配色（中性灰，与「其他」的 #64748b 区分）。</summary>
    public const string InactiveCategoryColor = "#94a3b8";

    /// <summary>「未活动」结论的来源标记。</summary>
    public const string InactiveSource = "inactive";

    public static ActivityClassificationResult Fallback() =>
        new("其他", "#64748b", null, 0.2, "fallback", "没有匹配到规则或启发式分类。");

    /// <summary>
    /// 空档 / 空闲 / 离开（#331）：显式声明「无活动」，不参与任何应用类别判定。
    /// 置信度取 1.0 —— 这不是猜测，而是对「该时段没有应用活动」的确定结论。
    /// </summary>
    public static ActivityClassificationResult Inactive() =>
        new(
            InactiveCategoryName,
            InactiveCategoryColor,
            null,
            1.0,
            InactiveSource,
            "空档 / 未活动时段：该记录不代表任何应用使用行为（gap/idle/afk）。");
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

public record ActivityClassificationSuggestionV2Dto(
    Guid Id,
    string ClusterKey,
    string? ProcessName,
    string? Domain,
    string? AppDisplayName,
    string? AppIcon,
    string? CurrentCategory,
    string? RecommendedCategoryName,
    Guid? RecommendedCategoryId,
    string? RecommendedProductivity,
    double Confidence,
    string RecognitionSource,
    bool IsOnlineLookup,
    double TotalDurationSeconds,
    int SampleCount,
    string Status,
    DateTimeOffset CreatedAt);

public record BatchAcceptItem(
    Guid SuggestionId,
    Guid? CategoryId,
    string? CategoryName,
    bool CreateRule = true);

public record BatchAcceptSuggestionsRequest(
    IReadOnlyList<BatchAcceptItem> Items);

public record BatchAcceptResultDto(
    int AcceptedCount,
    int RulesCreatedCount,
    int FailuresCount);
