using System.ComponentModel.DataAnnotations;

namespace Pim.Module.QuickNotes.DTOs;

public record CreateQuickNoteRequest(
    [Required] string ContentMarkdown,
    string? Source = null,
    string? MetadataJson = null,
    List<Guid>? AttachmentIds = null
);

public record UpdateQuickNoteRequest(
    [Required] string ContentMarkdown,
    string? Status = null,
    string? MetadataJson = null
);

public record QuickNoteResponse(
    Guid Id,
    string ContentMarkdown,
    string Status,
    string Source,
    string MetadataJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    List<QuickNoteAttachmentResponse> Attachments
);

public record QuickNoteAttachmentResponse(
    Guid Id,
    Guid? QuickNoteId,
    string StorageProvider,
    string ObjectKey,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? ContentHash,
    string MetadataJson,
    DateTimeOffset CreatedAt
);

public record QuickNoteUploadRequest(
    [Required] string FileName,
    [Required] string ContentType,
    long SizeBytes,
    string? ContentHash = null,
    string? MetadataJson = null
);
