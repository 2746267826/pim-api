using System.Text.Json;
using Pim.Core.Audit;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;

namespace Pim.Module.Calendar.Services;

public sealed class DataCenterGovernanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ICurrentUserService _currentUser;
    private readonly IOperationConfirmationService _confirmations;
    private readonly AuditVersionService _auditVersions;

    public DataCenterGovernanceService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService confirmations,
        AuditVersionService auditVersions)
    {
        _ = db;
        _currentUser = currentUser;
        _confirmations = confirmations;
        _auditVersions = auditVersions;
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
                DateTimeOffset.UtcNow.AddHours(8),
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
        var affectedCount = CountObjectsFromPayload(confirmation.PayloadJson);
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
        => _auditVersions.ExportAsync(start, end, ct);

    public Task<RestorePreviewResponse> PreviewRestoreAsync(
        DataCenterRestoreRequest request,
        CancellationToken ct = default)
        => _auditVersions.PreviewRestoreAsync(request.AuditVersionId, ct);

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
                DateTimeOffset.UtcNow.AddHours(8),
                request.AuditVersionId.ToString("N"),
                preview.ChangedFields,
                ["confirm-strict", "reject"],
                preview.ObjectType,
                preview.ObjectId,
                RequiresSecondLevelConfirmation: true,
                BeforeJson: null,
                AfterJson: null,
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

    private static int CountObjectsFromPayload(string payloadJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<DataCenterBatchOperationRequest>(payloadJson, JsonOptions);
            return request?.Objects?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
