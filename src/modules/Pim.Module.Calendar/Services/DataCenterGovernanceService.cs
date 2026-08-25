using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Audit;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class DataCenterGovernanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOperationConfirmationService _confirmations;
    private readonly AuditVersionService _auditVersions;
    private readonly TimeProvider _timeProvider;

    public DataCenterGovernanceService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService confirmations,
        AuditVersionService auditVersions,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _currentUser = currentUser;
        _confirmations = confirmations;
        _auditVersions = auditVersions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public Task<DataCenterBatchPreviewResponse> PreviewBatchOperationAsync(
        DataCenterBatchOperationRequest request,
        CancellationToken ct = default)
    {
        _ = ct;
        var objects = NormalizeObjects(request.Objects);
        var objectTypes = objects
            .Select(o => o.ObjectType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var summary = string.Join(
            Environment.NewLine,
            $"Batch action '{request.Action}' affects {objects.Count} object(s).",
            $"Affected types: {string.Join(", ", objectTypes)}.",
            "Recoverability: audit timeline, export, and recycle-bin restore preview are available before execution.");

        return Task.FromResult(new DataCenterBatchPreviewResponse(
            OperationRiskLevel.L4BatchOrDestructiveGovernance.ToString(),
            RequiresStrictConfirmation: true,
            summary,
            objectTypes,
            objects.Count));
    }

    public async Task<OperationConfirmationDto> RequestBatchConfirmationAsync(
        DataCenterBatchOperationRequest request,
        CancellationToken ct = default)
    {
        var preview = await PreviewBatchOperationAsync(request, ct);
        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var previewJson = JsonSerializer.Serialize(preview, JsonOptions);

        return await _confirmations.CreateAsync(
            new CreateOperationConfirmationRequest(
                UserId,
                $"data-center.batch.{request.Action}",
                preview.Summary,
                OperationRiskLevel.L4BatchOrDestructiveGovernance,
                "data-center",
                payloadJson,
                previewJson,
                _timeProvider.GetUtcNow().AddHours(8),
                Guid.NewGuid().ToString("N"),
                preview.AffectedObjectTypes,
                ["confirm-strict", "reject"],
                "data-center-batch",
                null,
                RequiresSecondLevelConfirmation: true,
                BeforeJson: null,
                AfterJson: null,
                RequiresStrictConfirmation: true,
                AuditBatchId: Guid.NewGuid(),
                AiRecommendation: "Review affected objects and export audit evidence before execution.",
                ExternalEffect: "May affect synced or governed schedule objects.",
                RecoveryPath: "Use audit timeline, audit export, and recycle-bin restore preview."),
            ct);
    }

    public async Task<DataCenterBatchExecutionResponse> ExecuteConfirmedBatchAsync(
        Guid confirmationId,
        CancellationToken ct = default)
    {
        var confirmation = await _confirmations.GetAsync(confirmationId, ct)
            ?? throw new DomainException(02046, "Batch confirmation does not exist.");
        EnsureCanExecute(confirmation);
        var request = ReadBatchRequest(confirmation.PayloadJson);
        var affectedCount = await ExecuteBatchActionAsync(confirmation, request, ct);
        var executed = await _confirmations.MarkExecutedAsync(
            confirmationId,
            JsonSerializer.Serialize(new { status = "executed", affectedCount }, JsonOptions),
            ct);

        return new DataCenterBatchExecutionResponse(executed.Id, executed.Status.ToString(), affectedCount);
    }

    public Task<AuditExportResponse> ExportAuditAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
        => _auditVersions.ExportAsync(start, end, UserId, ct);

    public Task<RestorePreviewResponse> PreviewRestoreAsync(
        DataCenterRestoreRequest request,
        CancellationToken ct = default)
        => _auditVersions.PreviewRestoreAsync(request.AuditVersionId, UserId, ct);

    public async Task<OperationConfirmationDto> RequestRestoreConfirmationAsync(
        DataCenterRestoreRequest request,
        CancellationToken ct = default)
    {
        var preview = await PreviewRestoreAsync(request, ct);
        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var previewJson = JsonSerializer.Serialize(preview, JsonOptions);

        return await _confirmations.CreateAsync(
            new CreateOperationConfirmationRequest(
                UserId,
                "data-center.restore",
                $"Restore preview for {preview.ObjectType} {preview.ObjectId}.",
                OperationRiskLevel.L4BatchOrDestructiveGovernance,
                "data-center",
                payloadJson,
                previewJson,
                _timeProvider.GetUtcNow().AddHours(8),
                request.AuditVersionId.ToString("N"),
                preview.ChangedFields,
                ["confirm-strict", "reject"],
                preview.ObjectType,
                preview.ObjectId,
                RequiresSecondLevelConfirmation: true,
                BeforeJson: preview.BeforeJson,
                AfterJson: preview.AfterJson,
                RequiresStrictConfirmation: true,
                AuditBatchId: Guid.NewGuid(),
                AiRecommendation: "Restore only after reviewing audit before/after values.",
                ExternalEffect: "May require downstream sync reconciliation.",
                RecoveryPath: "Create a fresh audit version before applying restore."),
            ct);
    }

    private static IReadOnlyList<DataCenterObjectRef> NormalizeObjects(
        IReadOnlyList<DataCenterObjectRef>? objects)
    {
        if (objects is null || objects.Count == 0)
            throw new DomainException(02047, "Batch operation requires at least one object.");

        var normalized = objects
            .Where(o => !string.IsNullOrWhiteSpace(o.ObjectType) && o.ObjectId != Guid.Empty)
            .Select(o => new DataCenterObjectRef(o.ObjectType.Trim(), o.ObjectId))
            .ToList();

        if (normalized.Count == 0)
            throw new DomainException(02048, "Batch operation requires valid object references.");

        return normalized;
    }

    private async Task<int> ExecuteBatchActionAsync(
        OperationConfirmationDto confirmation,
        DataCenterBatchOperationRequest request,
        CancellationToken ct)
    {
        var action = request.Action.Trim();
        if (!string.Equals(action, "archive", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(02049, $"Unsupported data center batch action '{request.Action}'.");
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async token =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(token);
                var affectedCount = 0;
                var deletedAt = _timeProvider.GetUtcNow();
                var operationKind = $"data-center.batch.{action.ToLowerInvariant()}";
                foreach (var obj in NormalizeObjects(request.Objects))
                {
                    affectedCount += await ArchiveObjectAsync(obj, confirmation.Id, operationKind, deletedAt, token);
                }
                await _db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                return affectedCount;
            }, ct);
        }
        else
        {
            var affectedCount = 0;
            var deletedAt = _timeProvider.GetUtcNow();
            var operationKind = $"data-center.batch.{action.ToLowerInvariant()}";
            foreach (var obj in NormalizeObjects(request.Objects))
            {
                affectedCount += await ArchiveObjectAsync(obj, confirmation.Id, operationKind, deletedAt, ct);
            }
            await _db.SaveChangesAsync(ct);
            return affectedCount;
        }
    }

    private async Task<int> ArchiveObjectAsync(
        DataCenterObjectRef obj,
        Guid confirmationId,
        string operationKind,
        DateTimeOffset archivedAt,
        CancellationToken ct)
    {
        var objectType = obj.ObjectType.Trim();
        if (string.Equals(objectType, "task", StringComparison.OrdinalIgnoreCase))
        {
            var task = await _db.Set<TaskEntity>()
                .FirstOrDefaultAsync(t => t.Id == obj.ObjectId && t.UserId == UserId, ct);
            if (task is null)
                return 0;

            var before = new
            {
                task.DeletedAt,
                task.DeletedByOperationId,
                task.DeletedByOperationKind,
                task.UpdatedAt
            };
            task.DeletedAt = archivedAt;
            task.DeletedByOperationId = confirmationId;
            task.DeletedByOperationKind = operationKind;
            task.UpdatedAt = archivedAt;
            await _auditVersions.RecordAsync(
                "task",
                task.Id,
                before,
                new
                {
                    task.DeletedAt,
                    task.DeletedByOperationId,
                    task.DeletedByOperationKind,
                    task.UpdatedAt
                },
                ["deletedAt", "deletedByOperationId", "deletedByOperationKind", "updatedAt"],
                confirmationId,
                "data-center",
                UserId,
                ct);
            return 1;
        }

        if (string.Equals(objectType, "event", StringComparison.OrdinalIgnoreCase))
        {
            var evt = await _db.Set<EventEntity>()
                .Include(e => e.Calendar)
                .FirstOrDefaultAsync(e => e.Id == obj.ObjectId && e.Calendar.UserId == UserId, ct);
            if (evt is null)
                return 0;

            var before = new
            {
                evt.DeletedAt,
                evt.DeletedByOperationId,
                evt.DeletedByOperationKind,
                evt.UpdatedAt
            };
            evt.DeletedAt = archivedAt;
            evt.DeletedByOperationId = confirmationId;
            evt.DeletedByOperationKind = operationKind;
            evt.UpdatedAt = archivedAt;
            await _auditVersions.RecordAsync(
                "event",
                evt.Id,
                before,
                new
                {
                    evt.DeletedAt,
                    evt.DeletedByOperationId,
                    evt.DeletedByOperationKind,
                    evt.UpdatedAt
                },
                ["deletedAt", "deletedByOperationId", "deletedByOperationKind", "updatedAt"],
                confirmationId,
                "data-center",
                UserId,
                ct);
            return 1;
        }

        if (string.Equals(objectType, "report", StringComparison.OrdinalIgnoreCase))
        {
            var report = await _db.Set<ReportArtifactEntity>()
                .FirstOrDefaultAsync(r => r.Id == obj.ObjectId && r.UserId == UserId, ct);
            if (report is null)
                return 0;

            var before = new
            {
                report.Status,
                report.UpdatedAt
            };
            report.Status = "Archived";
            report.UpdatedAt = archivedAt;
            await _auditVersions.RecordAsync(
                "report",
                report.Id,
                before,
                new
                {
                    report.Status,
                    report.UpdatedAt
                },
                ["status", "updatedAt"],
                confirmationId,
                "data-center",
                UserId,
                ct);
            return 1;
        }

        throw new DomainException(02050, $"Unsupported data center archive object type '{obj.ObjectType}'.");
    }

    private void EnsureCanExecute(OperationConfirmationDto confirmation)
    {
        if (confirmation.RequestedByUserId.HasValue && confirmation.RequestedByUserId.Value != UserId)
        {
            throw new DomainException(02051, "Batch confirmation belongs to a different user.");
        }

        if (confirmation.Status != OperationConfirmationStatus.Confirmed)
        {
            throw new DomainException(02052, "Batch confirmation must be strictly confirmed before execution.");
        }

        if (!confirmation.RequiresStrictConfirmation)
        {
            throw new DomainException(02055, "Batch confirmation is missing strict confirmation metadata.");
        }
    }

    private static DataCenterBatchOperationRequest ReadBatchRequest(string payloadJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<DataCenterBatchOperationRequest>(payloadJson, JsonOptions);
            if (request is null)
            {
                throw new DomainException(02053, "Batch operation payload is empty.");
            }

            return request;
        }
        catch (JsonException)
        {
            throw new DomainException(02054, "Batch operation payload is invalid.");
        }
    }
}
