namespace Pim.Core.Audit;

public sealed record AuditVersionDto(
    Guid Id,
    string ObjectType,
    Guid ObjectId,
    Guid? ConfirmationId,
    string Source,
    string Actor,
    string BeforeJson,
    string AfterJson,
    string ChangedFieldsJson,
    DateTimeOffset CreatedAt);

public sealed record AuditTimelineResponse(IReadOnlyList<AuditVersionDto> Items);

public sealed record RestorePreviewResponse(
    string ObjectType,
    Guid ObjectId,
    string Summary,
    bool RequiresConfirmation,
    IReadOnlyList<string> ChangedFields,
    string? BeforeJson = null,
    string? AfterJson = null);

public sealed record AuditExportResponse(
    string FileName,
    string ContentType,
    string Content);
