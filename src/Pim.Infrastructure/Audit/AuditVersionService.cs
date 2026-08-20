using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Audit;
using Pim.Core.Exceptions;
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
        Guid? userId = null,
        CancellationToken ct = default)
    {
        var entity = new AuditVersionEntity
        {
            ObjectType = objectType,
            ObjectId = objectId,
            ConfirmationId = confirmationId,
            UserId = userId,
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
        Guid userId,
        CancellationToken ct = default)
    {
        var items = await _db.AuditVersions
            .AsNoTracking()
            .Where(v => v.ObjectType == objectType && v.ObjectId == objectId && v.UserId == userId)
            .OrderBy(v => v.CreatedAt)
            .ThenBy(v => v.Id)
            .Select(v => Map(v))
            .ToListAsync(ct);

        return new AuditTimelineResponse(items);
    }

    public async Task<RestorePreviewResponse> PreviewRestoreAsync(
        Guid auditVersionId,
        Guid userId,
        CancellationToken ct = default)
    {
        var entity = await _db.AuditVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == auditVersionId && v.UserId == userId, ct)
            ?? throw new DomainException(02056, "Audit version does not exist.");
        var changedFields = JsonSerializer.Deserialize<IReadOnlyList<string>>(entity.ChangedFieldsJson)
            ?? Array.Empty<string>();

        return new RestorePreviewResponse(
            entity.ObjectType,
            entity.ObjectId,
            $"Restore {entity.ObjectType} {entity.ObjectId} to audit version {entity.Id}.",
            RequiresConfirmation: true,
            changedFields,
            AuditSnapshotSanitizer.SanitizeJson(entity.BeforeJson),
            AuditSnapshotSanitizer.SanitizeJson(entity.AfterJson));
    }

    public async Task<AuditExportResponse> ExportAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        Guid userId,
        CancellationToken ct = default)
    {
        var items = await _db.AuditVersions
            .AsNoTracking()
            .Where(v => v.CreatedAt >= start && v.CreatedAt <= end && v.UserId == userId)
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
            AuditSnapshotSanitizer.SanitizeJson(entity.BeforeJson),
            AuditSnapshotSanitizer.SanitizeJson(entity.AfterJson),
            AuditSnapshotSanitizer.SanitizeJson(entity.ChangedFieldsJson),
            entity.CreatedAt);
}
