using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Pim.Core.Notes;

namespace Pim.Module.NoteTaskSuggestions.DTOs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteTaskSuggestionStatusDto
{
    Draft,
    Confirmed,
    Dismissed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteTaskSuggestionExtractionStatusDto
{
    Queued,
    Running,
    Succeeded,
    Failed
}

public sealed record StartNoteTaskSuggestionExtractionRequest(
    Guid RequestId,
    Guid ExpectedUserId,
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    [property: Required, MaxLength(64)] string ExpectedContentSha256,
    NoteDerivedGenerationConsent Consent,
    bool AcknowledgePossibleCharge);

public sealed record NoteTaskSuggestionExtractionDto(
    Guid Id,
    Guid RequestId,
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    string SourceContentSha256,
    string SourceInputSha256,
    string SourceManifestSha256,
    Guid? SourceGenerationTaskId,
    Guid ModelProfileId,
    Guid ModelRevision,
    string ModelEndpoint,
    string PromptVersion,
    NoteTaskSuggestionExtractionStatusDto Status,
    Guid Revision,
    int AttemptCount,
    int SuggestionCount,
    Guid? AiCallLogId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record NoteTaskSuggestionDto(
    Guid Id,
    Guid ExtractionId,
    int Ordinal,
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    string SourceContentSha256,
    string SourceInputSha256,
    string SourceManifestSha256,
    Guid? SourceGenerationTaskId,
    string Title,
    string? Description,
    int Priority,
    DateTimeOffset? Due,
    Guid? TaskBookId,
    Guid? CalendarId,
    NoteTaskSuggestionStatusDto Status,
    Guid Revision,
    Guid? TaskId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DismissedAt);

public sealed record NoteTaskSuggestionListDto(
    int Total,
    IReadOnlyList<NoteTaskSuggestionDto> Items);

public sealed record UpdateNoteTaskSuggestionRequest(
    Guid ExpectedRevision,
    [property: Required, MaxLength(255)] string Title,
    [property: MaxLength(20000)] string? Description,
    int Priority,
    DateTimeOffset? Due,
    Guid? TaskBookId,
    Guid? CalendarId);

public sealed record ConfirmNoteTaskSuggestionRequest(
    Guid RequestId,
    Guid ExpectedRevision);

public sealed record DismissNoteTaskSuggestionRequest(
    Guid ExpectedRevision);
