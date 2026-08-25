using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class OperationConfirmationService : IOperationConfirmationService
{
    private readonly PimDbContext _db;
    private readonly TimeProvider _timeProvider;

    public OperationConfirmationService(PimDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OperationConfirmationDto> CreateAsync(
        CreateOperationConfirmationRequest request,
        CancellationToken ct = default)
    {
        ValidateJson(request.PayloadJson, 3006, "PayloadJson must be valid JSON");
        ValidateJson(request.PreviewJson, 3007, "PreviewJson must be valid JSON");
        var previewJson = BuildPreviewJson(request);
        ValidateJson(previewJson, 3007, "PreviewJson must be valid JSON");

        var entity = new OperationConfirmationEntity
        {
            RequestedByUserId = request.RequestedByUserId,
            OperationType = request.OperationType,
            Summary = request.Summary,
            RiskLevel = request.RiskLevel.ToString(),
            Source = request.Source,
            PayloadJson = request.PayloadJson,
            PreviewJson = previewJson,
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = request.ExpiresAt,
            CreatedAt = _timeProvider.GetUtcNow(),
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
        await ExpireOldAsync(_timeProvider.GetUtcNow(), ct);

        var pending = await _db.OperationConfirmations
            .AsNoTracking()
            .Where(c => c.Status == OperationConfirmationStatus.Pending.ToString())
            .OrderBy(c => c.ExpiresAt)
            .ToListAsync(ct);

        return pending.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<OperationConfirmationDto>> ListPendingForUserAsync(
        Guid? userId,
        CancellationToken ct = default)
    {
        await ExpireOldAsync(_timeProvider.GetUtcNow(), ct);

        var pending = await _db.OperationConfirmations
            .AsNoTracking()
            .Where(c =>
                c.Status == OperationConfirmationStatus.Pending.ToString()
                && (c.RequestedByUserId == null || c.RequestedByUserId == userId))
            .OrderBy(c => c.ExpiresAt)
            .ToListAsync(ct);

        return pending.Select(Map).ToList();
    }

    public async Task<OperationConfirmationDto> ConfirmAsync(Guid id, Guid? userId, CancellationToken ct = default)
        => await ConfirmWithModeAsync(id, userId, ConfirmationMode.Basic, ct);

    public async Task<OperationConfirmationDto> ConfirmSecondLevelAsync(
        Guid id,
        Guid? userId,
        CancellationToken ct = default)
        => await ConfirmWithModeAsync(id, userId, ConfirmationMode.SecondLevel, ct);

    public async Task<OperationConfirmationDto> ConfirmStrictAsync(
        Guid id,
        Guid? userId,
        CancellationToken ct = default)
        => await ConfirmWithModeAsync(id, userId, ConfirmationMode.Strict, ct);

    private async Task<OperationConfirmationDto> ConfirmWithModeAsync(
        Guid id,
        Guid? userId,
        ConfirmationMode mode,
        CancellationToken ct)
    {
        var entity = await LoadPendingAsync(id, ct);
        EnsureUserCanAct(entity, userId);
        EnsureConfirmationMode(entity, mode);

        entity.Status = OperationConfirmationStatus.Confirmed.ToString();
        entity.ConfirmedAt = _timeProvider.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    private static void EnsureConfirmationMode(OperationConfirmationEntity entity, ConfirmationMode mode)
    {
        var metadata = ExtractMetadata(entity.PreviewJson);

        if (metadata.RequiresStrictConfirmation && mode != ConfirmationMode.Strict)
        {
            throw new DomainException(3009, "Strict confirmation is required for this operation.");
        }

        if (metadata.RequiresSecondLevelConfirmation && mode == ConfirmationMode.Basic)
        {
            throw new DomainException(3010, "Second-level confirmation is required for this operation.");
        }
    }

    public async Task<OperationConfirmationDto> RejectAsync(Guid id, Guid? userId, CancellationToken ct = default)
    {
        var entity = await LoadPendingAsync(id, ct);
        EnsureUserCanAct(entity, userId);

        entity.Status = OperationConfirmationStatus.Rejected.ToString();
        entity.RejectedAt = _timeProvider.GetUtcNow();

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
            throw new DomainException(3001, "Confirmation record does not exist.");
        }

        if (entity.Status != OperationConfirmationStatus.Confirmed.ToString())
        {
            throw new DomainException(3002, "Only confirmed operations can be executed.");
        }

        ValidateJson(resultJson, 3008, "ResultJson must be valid JSON");

        entity.Status = OperationConfirmationStatus.Executed.ToString();
        entity.ExecutedAt = _timeProvider.GetUtcNow();
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
            throw new DomainException(3001, "Confirmation record does not exist.");
        }

        if (entity.Status != OperationConfirmationStatus.Pending.ToString())
        {
            throw new DomainException(3003, "Confirmation record is not pending.");
        }

        if (entity.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            entity.Status = OperationConfirmationStatus.Expired.ToString();
            await _db.SaveChangesAsync(ct);
            throw new DomainException(3004, "Confirmation record has expired.");
        }

        return entity;
    }

    private static void EnsureUserCanAct(OperationConfirmationEntity entity, Guid? userId)
    {
        if (entity.RequestedByUserId is { } requestedByUserId && requestedByUserId != userId)
        {
            throw new DomainException(3005, "Confirmation record is not assigned to the current user.");
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

    private static string BuildPreviewJson(CreateOperationConfirmationRequest request)
    {
        using var document = JsonDocument.Parse(request.PreviewJson);
        var preview = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText())
            ?? new Dictionary<string, object?>();

        preview["_meta"] = new
        {
            changedFields = request.ChangedFields ?? Array.Empty<string>(),
            allowedActions = request.AllowedActions ?? Array.Empty<string>(),
            objectType = request.ObjectType,
            objectId = request.ObjectId,
            requiresSecondLevelConfirmation = request.RequiresSecondLevelConfirmation,
            beforeJson = request.BeforeJson,
            afterJson = request.AfterJson,
            requiresStrictConfirmation = request.RequiresStrictConfirmation,
            auditBatchId = request.AuditBatchId,
            aiRecommendation = request.AiRecommendation,
            externalEffect = request.ExternalEffect,
            recoveryPath = request.RecoveryPath
        };

        return JsonSerializer.Serialize(preview);
    }

    private static ConfirmationMetadata ExtractMetadata(string previewJson)
    {
        try
        {
            using var document = JsonDocument.Parse(previewJson);
            if (!document.RootElement.TryGetProperty("_meta", out var meta)
                || meta.ValueKind != JsonValueKind.Object)
            {
                return ConfirmationMetadata.Empty;
            }

            return new ConfirmationMetadata(
                ReadStringArray(meta, "changedFields"),
                ReadStringArray(meta, "allowedActions"),
                ReadString(meta, "objectType"),
                ReadGuid(meta, "objectId"),
                ReadBool(meta, "requiresSecondLevelConfirmation"),
                ReadString(meta, "beforeJson"),
                ReadString(meta, "afterJson"),
                ReadBool(meta, "requiresStrictConfirmation"),
                ReadGuid(meta, "auditBatchId"),
                ReadString(meta, "aiRecommendation"),
                ReadString(meta, "externalEffect"),
                ReadString(meta, "recoveryPath"));
        }
        catch (JsonException)
        {
            return ConfirmationMetadata.Empty;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static Guid? ReadGuid(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out var guid))
        {
            return guid;
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();
    }

    private static readonly Regex ExternalEffectToken = new(
        "(?:GraphEventId|ChangeKey|ETag|DeltaLink|@odata\\.etag)=[^\\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Redacts provider token assignments inside free-text effect summaries.</summary>
    private static string RedactExternalEffect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        return ExternalEffectToken.Replace(value, "***");
    }

    private static OperationConfirmationDto Map(OperationConfirmationEntity entity)
    {
        var payloadJson = AuditSnapshotSanitizer.SanitizeJson(entity.PayloadJson);
        var previewJson = AuditSnapshotSanitizer.SanitizeJson(entity.PreviewJson);
        var resultJson = entity.ResultJson is null
            ? null
            : AuditSnapshotSanitizer.SanitizeJson(entity.ResultJson);
        var metadata = ExtractMetadata(previewJson);
        var redactedEffect = RedactExternalEffect(metadata.ExternalEffect);
        return new OperationConfirmationDto(
            entity.Id,
            entity.RequestedByUserId,
            entity.OperationType,
            entity.Summary,
            ParseRiskLevel(entity.RiskLevel),
            entity.Source,
            payloadJson,
            previewJson,
            Enum.Parse<OperationConfirmationStatus>(entity.Status),
            entity.ExpiresAt,
            entity.CreatedAt,
            entity.ConfirmedAt,
            entity.ExecutedAt,
            resultJson,
            entity.CorrelationId,
            metadata.ChangedFields,
            metadata.AllowedActions,
            metadata.ObjectType,
            metadata.ObjectId,
            metadata.RequiresSecondLevelConfirmation,
            AuditSnapshotSanitizer.SanitizeJson(metadata.BeforeJson),
            AuditSnapshotSanitizer.SanitizeJson(metadata.AfterJson),
            metadata.RequiresStrictConfirmation,
            metadata.AuditBatchId,
            metadata.AiRecommendation,
            redactedEffect,
            metadata.RecoveryPath);
    }

    private static OperationRiskLevel ParseRiskLevel(string value)
    {
        return Enum.TryParse<OperationRiskLevel>(value, out var parsed)
            ? parsed
            : OperationRiskLevel.Medium;
    }

    private sealed record ConfirmationMetadata(
        IReadOnlyList<string> ChangedFields,
        IReadOnlyList<string> AllowedActions,
        string? ObjectType,
        Guid? ObjectId,
        bool RequiresSecondLevelConfirmation,
        string? BeforeJson,
        string? AfterJson,
        bool RequiresStrictConfirmation,
        Guid? AuditBatchId,
        string? AiRecommendation,
        string? ExternalEffect,
        string? RecoveryPath)
    {
        public static ConfirmationMetadata Empty { get; } = new(
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            null,
            false,
            null,
            null,
            false,
            null,
            null,
            null,
            null);
    }

    private enum ConfirmationMode
    {
        Basic,
        SecondLevel,
        Strict
    }
}
