using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Audit;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Audit;

public sealed class AuditVersionService
{
    private readonly PimDbContext _db;

    public AuditVersionService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<AuditVersionDto> RecordAsync(
        string objectType,
        Guid objectId,
        object before,
        object after,
        IReadOnlyList<string> changedFields,
        Guid? confirmationId,
        string source,
        CancellationToken ct = default)
    {
        var entity = new AuditVersionEntity
        {
            ObjectType = objectType,
            ObjectId = objectId,
            ConfirmationId = confirmationId,
            Source = source,
            Actor = "system",
            BeforeJson = JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after),
            ChangedFieldsJson = JsonSerializer.Serialize(changedFields),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.AuditVersions.Add(entity);
        await _db.SaveChangesAsync(ct);

        return Map(entity);
    }

    public async Task<AuditTimelineResponse> GetTimelineAsync(
        string objectType,
        Guid objectId,
        CancellationToken ct = default)
    {
        var items = await _db.AuditVersions
            .AsNoTracking()
            .Where(v => v.ObjectType == objectType && v.ObjectId == objectId)
            .OrderBy(v => v.CreatedAt)
            .ThenBy(v => v.Id)
            .Select(v => Map(v))
            .ToListAsync(ct);

        return new AuditTimelineResponse(items);
    }

    public async Task<RestorePreviewResponse> PreviewRestoreAsync(
        Guid auditVersionId,
        CancellationToken ct = default)
    {
        var entity = await _db.AuditVersions
            .AsNoTracking()
            .SingleAsync(v => v.Id == auditVersionId, ct);
        var changedFields = JsonSerializer.Deserialize<IReadOnlyList<string>>(entity.ChangedFieldsJson)
            ?? Array.Empty<string>();

        return new RestorePreviewResponse(
            entity.ObjectType,
            entity.ObjectId,
            $"Restore {entity.ObjectType} {entity.ObjectId} to audit version {entity.Id}.",
            RequiresConfirmation: true,
            changedFields);
    }

    public async Task<AuditExportResponse> ExportAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        var items = await _db.AuditVersions
            .AsNoTracking()
            .Where(v => v.CreatedAt >= start && v.CreatedAt <= end)
            .OrderBy(v => v.CreatedAt)
            .ThenBy(v => v.Id)
            .Select(v => Map(v))
            .ToListAsync(ct);

        return new AuditExportResponse(
            "audit-export.json",
            "application/json",
            JsonSerializer.Serialize(items));
    }

    private static AuditVersionDto Map(AuditVersionEntity entity)
        => new(
            entity.Id,
            entity.ObjectType,
            entity.ObjectId,
            entity.ConfirmationId,
            entity.Source,
            entity.Actor,
            entity.BeforeJson,
            entity.AfterJson,
            entity.ChangedFieldsJson,
            entity.CreatedAt);
}
