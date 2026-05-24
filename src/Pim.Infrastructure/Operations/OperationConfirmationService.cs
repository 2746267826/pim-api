using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class OperationConfirmationService : IOperationConfirmationService
{
    private readonly PimDbContext _db;

    public OperationConfirmationService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<OperationConfirmationDto> CreateAsync(
        CreateOperationConfirmationRequest request,
        CancellationToken ct = default)
    {
        ValidateJson(request.PayloadJson, 3006, "PayloadJson must be valid JSON");
        ValidateJson(request.PreviewJson, 3007, "PreviewJson must be valid JSON");

        var entity = new OperationConfirmationEntity
        {
            RequestedByUserId = request.RequestedByUserId,
            OperationType = request.OperationType,
            Summary = request.Summary,
            RiskLevel = request.RiskLevel.ToString(),
            Source = request.Source,
            PayloadJson = request.PayloadJson,
            PreviewJson = request.PreviewJson,
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = request.CorrelationId
        };

        _db.OperationConfirmations.Add(entity);
        await _db.SaveChangesAsync(ct);

        return Map(entity);
    }

    public async Task<OperationConfirmationDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.OperationConfirmations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<OperationConfirmationDto>> ListPendingAsync(CancellationToken ct = default)
    {
        await ExpireOldAsync(DateTimeOffset.UtcNow, ct);

        var pending = await _db.OperationConfirmations
            .AsNoTracking()
            .Where(c => c.Status == OperationConfirmationStatus.Pending.ToString())
            .OrderBy(c => c.ExpiresAt)
            .ToListAsync(ct);

        return pending.Select(Map).ToList();
    }

    public async Task<OperationConfirmationDto> ConfirmAsync(Guid id, Guid? userId, CancellationToken ct = default)
    {
        var entity = await LoadPendingAsync(id, ct);
        EnsureUserCanAct(entity, userId);

        entity.Status = OperationConfirmationStatus.Confirmed.ToString();
        entity.ConfirmedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<OperationConfirmationDto> RejectAsync(Guid id, Guid? userId, CancellationToken ct = default)
    {
        var entity = await LoadPendingAsync(id, ct);
        EnsureUserCanAct(entity, userId);

        entity.Status = OperationConfirmationStatus.Rejected.ToString();
        entity.RejectedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<OperationConfirmationDto> MarkExecutedAsync(
        Guid id,
        string resultJson,
        CancellationToken ct = default)
    {
        var entity = await _db.OperationConfirmations.FindAsync([id], ct);

        if (entity is null)
        {
            throw new DomainException(3001, "Confirmation not found");
        }

        if (entity.Status != OperationConfirmationStatus.Confirmed.ToString())
        {
            throw new DomainException(3002, "Only confirmed operations can be executed");
        }

        ValidateJson(resultJson, 3008, "ResultJson must be valid JSON");

        entity.Status = OperationConfirmationStatus.Executed.ToString();
        entity.ExecutedAt = DateTimeOffset.UtcNow;
        entity.ResultJson = resultJson;

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<int> ExpireOldAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var pending = await _db.OperationConfirmations
            .Where(c =>
                c.Status == OperationConfirmationStatus.Pending.ToString()
                && c.ExpiresAt <= now)
            .ToListAsync(ct);

        foreach (var entity in pending)
        {
            entity.Status = OperationConfirmationStatus.Expired.ToString();
        }

        await _db.SaveChangesAsync(ct);
        return pending.Count;
    }

    private async Task<OperationConfirmationEntity> LoadPendingAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.OperationConfirmations.FindAsync([id], ct);

        if (entity is null)
        {
            throw new DomainException(3001, "Confirmation not found");
        }

        if (entity.Status != OperationConfirmationStatus.Pending.ToString())
        {
            throw new DomainException(3003, "Confirmation is not pending");
        }

        if (entity.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            entity.Status = OperationConfirmationStatus.Expired.ToString();
            await _db.SaveChangesAsync(ct);
            throw new DomainException(3004, "Confirmation has expired");
        }

        return entity;
    }

    private static void EnsureUserCanAct(OperationConfirmationEntity entity, Guid? userId)
    {
        if (entity.RequestedByUserId is { } requestedByUserId && requestedByUserId != userId)
        {
            throw new DomainException(3005, "Confirmation is not assigned to this user");
        }
    }

    private static void ValidateJson(string json, int errorCode, string message)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DomainException(errorCode, message);
        }
    }

    private static OperationConfirmationDto Map(OperationConfirmationEntity entity)
    {
        return new OperationConfirmationDto(
            entity.Id,
            entity.RequestedByUserId,
            entity.OperationType,
            entity.Summary,
            Enum.Parse<OperationRiskLevel>(entity.RiskLevel),
            entity.Source,
            entity.PayloadJson,
            entity.PreviewJson,
            Enum.Parse<OperationConfirmationStatus>(entity.Status),
            entity.ExpiresAt,
            entity.CreatedAt,
            entity.ConfirmedAt,
            entity.ExecutedAt,
            entity.ResultJson,
            entity.CorrelationId);
    }
}
