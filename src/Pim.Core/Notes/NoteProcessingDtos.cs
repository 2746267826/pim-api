using System.Text.Json.Serialization;

namespace Pim.Core.Notes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteProcessingState
{
    WaitingForMaterial,
    PendingProcessing,
    Processing,
    PartiallyCompleted,
    Completed,
    NeedsAttention
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteProcessingChainState
{
    NotApplicable,
    WaitingForMaterial,
    WaitingForAsr,
    PendingStart,
    Queued,
    Running,
    Succeeded,
    NeedsAttention,
    Stopped
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteProcessingTaskKind
{
    Asr,
    Derived
}

public static class NoteProcessingAuthorizationPurpose
{
    public const string NativeNoteProcessing = "native-note-processing";
    public const string PurposeText = "生成当前固定版本的转写与整理结果";
    public const string MaterialScope = "FullVersion";
    public const string MaterialScopeText = "当前固定版本的全部材料（正文、图片、手写与录音）";
}

public static class NoteProcessingAuthorizationSource
{
    public const string Manual = "manual";
    public const string Standing = "standing";
}

public static class NoteProcessingAuthorizationRules
{
    public const string CostNotice =
        "当前固定版本的全部材料会按账号默认目标处理；云端转写与多模态整理可能按服务商用量产生费用。";

    public static bool IsStandingAuthorizationValid(Guid? revision, string? purpose,
        string? materialScope, string? costNotice, bool possibleChargeAcknowledged,
        Guid? authorizedTargetRevision, Guid currentTargetRevision) =>
        revision is { } value && value != Guid.Empty &&
        string.Equals(purpose, NoteProcessingAuthorizationPurpose.NativeNoteProcessing,
            StringComparison.Ordinal) &&
        string.Equals(materialScope, NoteProcessingAuthorizationPurpose.MaterialScope,
            StringComparison.Ordinal) &&
        string.Equals(costNotice, CostNotice, StringComparison.Ordinal) &&
        possibleChargeAcknowledged &&
        authorizedTargetRevision is { } target && target != Guid.Empty &&
        target == currentTargetRevision;
}

public sealed record NoteProcessingTargetDto(
    NoteProcessingTaskKind Kind,
    bool Required,
    bool Paid,
    Guid? ModelId,
    Guid? ModelRevision,
    string? ModelName,
    string? Model,
    string? Endpoint,
    string? Route,
    string? ExecutionMode,
    string? PresetId,
    string? PresetVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? RouteConfigRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RouteConsentVersion = null);

public sealed record NoteProcessingAuthorizationDto(
    string Purpose,
    string PurposeText,
    string MaterialScope,
    string MaterialScopeText,
    string CostNotice,
    bool PossibleCharge,
    bool Confirmed,
    bool PossibleChargeAcknowledged,
    Guid AuthorizationRevision,
    Guid? StandingAuthorizationRevision,
    string Source,
    IReadOnlyList<NoteProcessingTargetDto> Targets);

public sealed record NoteProcessingPlanDto(
    Guid NoteId,
    Guid VersionId,
    string Purpose,
    string PurposeText,
    string MaterialScope,
    string MaterialScopeText,
    string CostNotice,
    bool PossibleCharge,
    Guid AuthorizationRevision,
    Guid? StandingAuthorizationRevision,
    bool StandingAuthorizationValid,
    bool AsrRequired,
    IReadOnlyList<NoteProcessingTargetDto> Targets);

public sealed record NoteProcessingConsent(
    string Purpose,
    string MaterialScope,
    string CostNotice,
    bool PossibleChargeAcknowledged,
    bool Confirmed,
    Guid AuthorizationRevision,
    Guid? StandingAuthorizationRevision = null);

public sealed record StartNoteProcessingRequest(
    Guid NoteId,
    Guid VersionId,
    NoteProcessingConsent Consent);

public sealed record NoteProcessingPreferencesDto(
    Guid Revision,
    bool AutoProcessingEnabled,
    Guid? StandingAuthorizationRevision,
    string Purpose,
    string PurposeText,
    string MaterialScope,
    string MaterialScopeText,
    string CostNotice,
    bool PossibleChargeAcknowledged,
    bool StandingAuthorizationValid,
    Guid CurrentDefaultTargetRevision,
    Guid? StandingAuthorizationTargetRevision,
    IReadOnlyList<string> DefaultTargets);

public sealed record SaveNoteProcessingPreferencesRequest(
    Guid ExpectedRevision,
    bool AutoProcessingEnabled,
    bool ConfirmStandingAuthorization = false,
    bool AcknowledgePossibleCharge = false,
    Guid? DefaultTargetRevision = null);

public sealed record NoteProcessingIssueDto(
    string Code,
    string Message,
    NoteProcessingTaskKind? Task = null);

public sealed record NoteProcessingChainDto(
    NoteProcessingTaskKind Kind,
    NoteProcessingChainState State,
    Guid? TaskId,
    bool TaskCreated,
    Guid? Revision,
    string? Phase,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    bool HasResult,
    IReadOnlyList<string> Errors,
    string? WaitingReason);

public sealed record NoteProcessingArtifactDto(
    Guid ArtifactId,
    NoteDerivedArtifactKind Kind,
    Guid? HeadVersionId);

public sealed record NoteProcessingStatusDto(
    Guid ProcessingId,
    Guid NoteId,
    Guid VersionId,
    NoteProcessingState State,
    bool AutoProcessingEnabled,
    bool AutoStarted,
    bool ManualStartAvailable,
    string Scope,
    bool IncrementalScopeSupported,
    string? PendingReason,
    NoteProcessingChainDto Asr,
    NoteProcessingChainDto Derived,
    IReadOnlyList<NoteProcessingArtifactDto> Artifacts,
    IReadOnlyList<NoteProcessingIssueDto> Issues,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    NoteProcessingAuthorizationDto? Authorization = null);
