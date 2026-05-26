using Pim.Core.Operations;

namespace Pim.Module.Calendar.Services;

public sealed class CalendarAuditWriter
{
    private const string Source = "calendar";
    private readonly IAuditLogService _auditLog;

    public CalendarAuditWriter(IAuditLogService auditLog)
    {
        _auditLog = auditLog;
    }

    public Task RecordSuccessAsync(
        Guid userId,
        string action,
        string resourceType,
        Guid resourceId,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => _auditLog.RecordAsync(new CreateAuditLogRequest(
            userId,
            AuditActorType.User,
            action,
            resourceType,
            resourceId.ToString(),
            Source,
            AuditResult.Success,
            null,
            null,
            null,
            metadata,
            null,
            null), ct);
}
