using Pim.Core.Common;

namespace Pim.Module.QuickNotes.DTOs;

public sealed record QuickNoteListItemDto(
    Guid Id,
    string ContentPreview,
    string Status,
    string Source,
    int AttachmentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record QuickNoteAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string DownloadUrl,
    string? PreviewUrl,
    DateTimeOffset CreatedAt);

public sealed record QuickNoteDetailDto(
    Guid Id,
    string ContentMarkdown,
    string Status,
    string Source,
    IReadOnlyList<QuickNoteAttachmentDto> Attachments,
    string MetadataJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record CreateQuickNoteRequest(
    string ContentMarkdown,
    string? Source,
    IReadOnlyList<Guid>? AttachmentIds);

public sealed record UpdateQuickNoteRequest(
    string ContentMarkdown,
    string? Status,
    IReadOnlyList<Guid>? AttachmentIds);

public sealed record RestoreQuickNoteRequest(string Status);

public sealed record QuickNoteAttachmentUploadDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string DownloadUrl,
    string? PreviewUrl);

public sealed record QuickNoteListQuery(
    string? Status,
    string? Search,
    int Page,
    int PageSize);

public sealed record QuickNoteListResponse(PagedResult<QuickNoteListItemDto> Result);
