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

    /// <summary>
    /// 只把审计行加入当前 DbContext 的变更跟踪，**不落库**；由调用方用自己的
    /// <c>SaveChangesAsync</c> 与业务改动一并提交，从而保证「业务 + 审计」原子。
    /// 语义与使用约束见 <c>AuditLogService.Enqueue</c> 的注释。
    /// </summary>
    AuditLogDto Enqueue(CreateAuditLogRequest request);
}
