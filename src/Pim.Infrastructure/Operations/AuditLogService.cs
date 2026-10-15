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
        var dto = Enqueue(request);
        await _db.SaveChangesAsync(ct);
        return dto;
    }

    /// <summary>
    /// 只把审计行加入当前 <see cref="PimDbContext"/> 的变更跟踪，**不**落库。
    ///
    /// 供「业务改动必须与审计原子提交」的调用方使用：调用方做完业务改动后调用本方法，
    /// 再用**自己的一次** <c>SaveChangesAsync</c> 把业务行与审计行一起提交
    /// （EF 对多条改动自动包一个事务），失败时整体回滚。
    ///
    /// 之所以需要它：<see cref="RecordAsync"/> 会自行保存，若业务方先保存再调用它，
    /// 两次保存之间失败就会留下「业务已生效、审计缺失」的中间状态；
    /// 而显式事务在本仓不可用——生产装配开了 <c>EnableRetryOnFailure</c>，
    /// <c>NpgsqlRetryingExecutionStrategy</c> 拒绝用户发起的事务。
    /// </summary>
    public AuditLogDto Enqueue(CreateAuditLogRequest request)
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
