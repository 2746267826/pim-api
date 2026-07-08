namespace Pim.Core.Operations;

public sealed record CreateOperationConfirmationRequest(
    Guid? RequestedByUserId,
    string OperationType,
    string Summary,
    OperationRiskLevel RiskLevel,
    string Source,
    string PayloadJson,
    string PreviewJson,
    DateTimeOffset ExpiresAt,
    string? CorrelationId,
    IReadOnlyList<string>? ChangedFields = null,
    IReadOnlyList<string>? AllowedActions = null,
    string? ObjectType = null,
    Guid? ObjectId = null,
    bool RequiresSecondLevelConfirmation = false,
    string? BeforeJson = null,
    string? AfterJson = null,
    bool RequiresStrictConfirmation = false,
    Guid? AuditBatchId = null,
    string? AiRecommendation = null,
    string? ExternalEffect = null,
    string? RecoveryPath = null);

public sealed record OperationConfirmationDto(
    Guid Id,
    Guid? RequestedByUserId,
    string OperationType,
    string Summary,
    OperationRiskLevel RiskLevel,
    string Source,
    string PayloadJson,
    string PreviewJson,
    OperationConfirmationStatus Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ExecutedAt,
    string? ResultJson,
    string? CorrelationId,
    IReadOnlyList<string>? ChangedFields = null,
    IReadOnlyList<string>? AllowedActions = null,
    string? ObjectType = null,
    Guid? ObjectId = null,
    bool RequiresSecondLevelConfirmation = false,
    string? BeforeJson = null,
    string? AfterJson = null,
    bool RequiresStrictConfirmation = false,
    Guid? AuditBatchId = null,
    string? AiRecommendation = null,
    string? ExternalEffect = null,
    string? RecoveryPath = null);

public interface IOperationConfirmationService
{
    Task<OperationConfirmationDto> CreateAsync(CreateOperationConfirmationRequest request, CancellationToken ct = default);
    Task<OperationConfirmationDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<OperationConfirmationDto>> ListPendingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OperationConfirmationDto>> ListPendingForUserAsync(Guid? userId, CancellationToken ct = default);
    Task<OperationConfirmationDto> ConfirmAsync(Guid id, Guid? userId, CancellationToken ct = default);
    Task<OperationConfirmationDto> ConfirmSecondLevelAsync(Guid id, Guid? userId, CancellationToken ct = default);
    Task<OperationConfirmationDto> ConfirmStrictAsync(Guid id, Guid? userId, CancellationToken ct = default);
    Task<OperationConfirmationDto> RejectAsync(Guid id, Guid? userId, CancellationToken ct = default);
    Task<OperationConfirmationDto> MarkExecutedAsync(Guid id, string resultJson, CancellationToken ct = default);
    Task<int> ExpireOldAsync(DateTimeOffset now, CancellationToken ct = default);
}
