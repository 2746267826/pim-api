using System.Text.Json.Serialization;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

internal static class NoteDerivedLimits
{
    internal const string PlanVersion = "note-derived-v1";
    internal const int MaxImages = 32;
    internal const int MaxImagesPerVisionCall = 4;
    internal const int MaxImageBytes = 4 * 1024 * 1024;
    internal const int MaxVisionCallBytes = 8 * 1024 * 1024;
    internal const int MaxVisionCalls = 8;
    internal const int MaxEvidenceCharsPerStep = 50_000;
    internal const int MaxEvidenceSteps = 12;
    // Keep this aligned with AiTextCallService's actual SDK input limit.
    internal const int MaxSynthesisInputChars = 200_000;
    internal const int MaxDerivedContentCharacters = 1_000_000;

    internal static NoteDerivedLimitsDto Describe() => new(
        MaxImages, MaxImagesPerVisionCall, MaxImageBytes, MaxVisionCallBytes, MaxVisionCalls,
        MaxEvidenceCharsPerStep, MaxEvidenceSteps, MaxSynthesisInputChars, MaxDerivedContentCharacters);
}

internal sealed record NoteDerivedTextInput(int Ordinal, Guid BlockId, string Text);

internal sealed record NoteDerivedTranscriptInput(
    int Ordinal,
    int AsrSegmentIndex,
    Guid RecordingId,
    int RecordingSegment,
    int ChunkIndex,
    int SourceFileIndex,
    string SourcePath,
    string SourceSha256,
    long SourceOffsetSamples,
    long Samples,
    string Text,
    Guid? LogId,
    Guid? EvidenceId = null,
    string? ReceiptKind = null,
    string? ProviderHandle = null);

internal sealed record NoteDerivedImageInput(
    int Ordinal,
    int FileIndex,
    string Path,
    string Kind,
    string Sha256,
    long SizeBytes,
    string ContentType,
    string? BlockId);

internal sealed record NoteDerivedInputSnapshot(
    Guid Owner,
    Guid NoteId,
    Guid SourceVersionId,
    Guid? AsrTaskId,
    Guid? AsrRevision,
    string InputSha256,
    string ManifestSha256,
    string NoteTitle,
    string NoteMarkdown,
    IReadOnlyList<NoteDerivedTextInput> TextBlocks,
    IReadOnlyList<NoteDerivedTranscriptInput> Transcripts,
    IReadOnlyList<NoteDerivedImageInput> Images,
    IReadOnlyList<NoteDerivedIssueDto> Issues);

internal sealed record NoteDerivedPlanStep(
    string Key,
    NoteDerivedStepKind Kind,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<NoteDerivedImageInput> Images,
    int InputCharacters);

internal sealed record NoteDerivedPlan(
    string PlanVersion,
    NoteDerivedInputSnapshot Input,
    IReadOnlyList<NoteDerivedPlanStep> Steps,
    NoteDerivedCoverageDto InitialCoverage);

internal interface INoteDerivedPlanner
{
    NoteDerivedPlan Build(NoteDerivedInputSnapshot input);
}

internal sealed class NoteDerivedTaskState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Owner { get; set; }
    public required StartNoteDerivedTaskRequest Request { get; set; }
    public required string Fingerprint { get; set; }
    public Guid NoteId { get; set; }
    public required string InputSha256 { get; set; }
    public required string ManifestSha256 { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool StopRequested { get; set; }
    public bool Blocked { get; set; }
    public NoteDerivedPlan Plan { get; set; } = null!;
    public NoteDerivedCoverageDto Coverage { get; set; } = EmptyCoverage();
    public List<NoteDerivedIssueDto> Issues { get; set; } = [];
    public Dictionary<Guid, string> Retries { get; set; } = [];
    public List<NoteDerivedStepState> Steps { get; set; } = [];
    public Guid CompleteRecordArtifactId { get; set; }
    public Guid IndependentAnalysisArtifactId { get; set; }

    internal static NoteDerivedCoverageDto EmptyCoverage(IReadOnlyList<NoteDerivedIssueDto>? issues = null) =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, false, issues ?? []);
}

internal sealed class NoteDerivedStepState
{
    public required string Key { get; set; }
    public NoteDerivedStepKind Kind { get; set; }
    public IReadOnlyList<string> DependsOn { get; set; } = [];
    public IReadOnlyList<NoteDerivedImageInput> Images { get; set; } = [];
    public int InputCharacters { get; set; }
    public List<NoteDerivedAttemptState> Attempts { get; set; } = [new()];
    [JsonIgnore]
    public NoteDerivedAttemptState Current => Attempts[^1];
}

internal sealed class NoteDerivedAttemptState
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid? LogId { get; set; }
    public string Status { get; set; } = "Queued";
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResultSha256 { get; set; }
}

internal sealed class NoteDerivedArtifactState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Owner { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid NoteId { get; set; }
    public NoteDerivedArtifactKind Kind { get; set; }
    public Guid HeadRevision { get; set; } = Guid.NewGuid();
    public Guid? HeadVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class NoteDerivedVersionState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Owner { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid NoteId { get; set; }
    public NoteDerivedArtifactKind Kind { get; set; }
    public Guid VersionId { get; set; }
    public NoteDerivedVersionAuthor Author { get; set; }
    public required string IdempotencyHash { get; set; }
    public Guid? ParentVersionId { get; set; }
    public Guid? ObservedHeadVersionId { get; set; }
    public Guid? SourceCompleteRecordVersionId { get; set; }
    public Guid SourceVersionId { get; set; }
    public Guid? GenerationTaskId { get; set; }
    public required string ContentMarkdown { get; set; }
    public required string ContentSha256 { get; set; }
    public required string InputSha256 { get; set; }
    public required string ManifestSha256 { get; set; }
    public required string PlanVersion { get; set; }
    public required string PromptVersion { get; set; }
    public Guid? AiCallLogId { get; set; }
    public Guid? ModelProfileId { get; set; }
    public Guid? ModelRevision { get; set; }
    public string? ModelEndpoint { get; set; }
    public NoteDerivedCoverageDto? Coverage { get; set; }
    public bool HasConflict { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed record NoteDerivedVersionDraft(
    Guid VersionId,
    NoteDerivedArtifactKind Kind,
    Guid? ParentVersionId,
    Guid? SourceCompleteRecordVersionId,
    Guid SourceVersionId,
    Guid? GenerationTaskId,
    string ContentMarkdown,
    string InputSha256,
    string ManifestSha256,
    string PlanVersion,
    string PromptVersion,
    Guid? AiCallLogId,
    Guid? ModelProfileId,
    Guid? ModelRevision,
    string? ModelEndpoint,
    NoteDerivedCoverageDto? Coverage);

internal sealed record NoteDerivedManualRevisionDraft(
    Guid VersionId,
    Guid? ParentVersionId,
    Guid? ObservedHeadVersionId,
    Guid SourceVersionId,
    string ContentMarkdown,
    string InputSha256,
    string ManifestSha256,
    string PlanVersion,
    bool HasConflict);
