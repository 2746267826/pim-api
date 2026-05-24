namespace Pim.Core.Operations;

public sealed record CreateAuditLogRequest(
    Guid? UserId,
    AuditActorType ActorType,
    string Action,
    string ResourceType,
    string? ResourceId,
    string Source,
    AuditResult Result,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId,
    IReadOnlyDictionary<string, string>? Metadata,
    int? ErrorCode,
    string? ErrorMessage);

public sealed record AuditLogDto(
    Guid Id,
    Guid? UserId,
    AuditActorType ActorType,
    string Action,
    string ResourceType,
    string? ResourceId,
    string Source,
    AuditResult Result,
    string? CorrelationId,
    DateTimeOffset CreatedAt);

public interface IAuditLogService
{
    Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default);
}
