using System.Text.Json;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class AuditLogService : IAuditLogService
{
    private readonly PimDbContext _db;

    public AuditLogService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
    {
        var entity = new AuditLogEntity
        {
            UserId = request.UserId,
            ActorType = request.ActorType.ToString(),
            Action = request.Action,
            ResourceType = request.ResourceType,
            ResourceId = request.ResourceId,
            Source = request.Source,
            Result = request.Result.ToString(),
            IpAddress = request.IpAddress,
            UserAgent = request.UserAgent,
            CorrelationId = request.CorrelationId,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>()),
            ErrorCode = request.ErrorCode,
            ErrorMessage = request.ErrorMessage,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.AuditLogs.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new AuditLogDto(
            entity.Id,
            entity.UserId,
            Enum.Parse<AuditActorType>(entity.ActorType),
            entity.Action,
            entity.ResourceType,
            entity.ResourceId,
            entity.Source,
            Enum.Parse<AuditResult>(entity.Result),
            entity.CorrelationId,
            entity.CreatedAt);
    }
}
