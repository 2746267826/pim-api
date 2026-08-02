using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookConflictService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ConflictActions =
    [
        "keep_pim",
        "keep_outlook",
        "merge_by_field",
        "create_merge_copy",
        "skip_batch",
        "stop_sync"
    ];

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOperationConfirmationService _confirmations;

    public OutlookConflictService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService confirmations)
    {
        _db = db;
        _currentUser = currentUser;
        _confirmations = confirmations;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<SyncConflictDetailDto> GetAsync(Guid conflictId, CancellationToken ct = default)
    {
        var conflict = await LoadConflictAsync(conflictId, ct);
        return Map(conflict);
    }

    public async Task<OperationConfirmationDto> RequestActionAsync(
        Guid conflictId,
        ConflictResolutionRequest request,
        CancellationToken ct = default)
    {
        var conflict = await LoadConflictAsync(conflictId, ct);
        var action = NormalizeAction(request.Action);
        var confirmation = await CreateConfirmationAsync(
            action,
            conflict.Id,
            conflict.ObjectType,
            conflict.ObjectId,
            conflict.GraphEventId,
            conflict.PimSnapshotJson,
            conflict.ExternalSnapshotJson,
            request.MergedFieldsJson,
            request.Reason,
            ct);

        conflict.Status = "pending-confirmation";
        conflict.ResolvedConfirmationId = confirmation.Id;
        conflict.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return confirmation;
    }

    public async Task<OperationConfirmationDto> RequestStopSyncPreviewAsync(
        Guid eventId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var evt = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == userId, ct)
            ?? throw new DomainException(02001, "Event does not exist.");

        return await CreateConfirmationAsync(
            "stop_sync",
            null,
            "event",
            evt.Id,
            evt.OutlookEventId,
            JsonSerializer.Serialize(new { evt.Title, evt.Location, evt.DtStart, evt.DtEnd }),
            JsonSerializer.Serialize(new { evt.OutlookEventId, evt.OutlookChangeKey }),
            null,
            "Stop Outlook sync for this event.",
            ct);
    }

    public async Task ExecuteConfirmedResolutionAsync(Guid confirmationId, CancellationToken ct = default)
    {
        var confirmation = await _confirmations.GetAsync(confirmationId, ct)
            ?? throw new DomainException(02006, "Confirmation does not exist.");
        if (confirmation.Status != OperationConfirmationStatus.Confirmed)
            throw new DomainException(02007, "Operation has not been confirmed.");

        using var payload = JsonDocument.Parse(confirmation.PayloadJson);
        var root = payload.RootElement;
        if (root.TryGetProperty("conflictId", out var conflictIdElement)
            && conflictIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(conflictIdElement.GetString(), out var conflictId))
        {
            var conflict = await LoadConflictAsync(conflictId, ct);
            conflict.Status = "resolved";
            conflict.ResolvedConfirmationId = confirmationId;
            conflict.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _confirmations.MarkExecutedAsync(
            confirmationId,
            JsonSerializer.Serialize(new { status = "resolved", provider = "outlook" }),
            ct);
    }

    public async Task<object> ExecuteStopSyncAsync(
        Guid eventId,
        Guid confirmationId,
        CancellationToken ct = default)
    {
        var confirmation = await _confirmations.GetAsync(confirmationId, ct)
            ?? throw new DomainException(02006, "Confirmation does not exist.");
        if (confirmation.Status != OperationConfirmationStatus.Confirmed)
            throw new DomainException(02007, "Operation has not been confirmed.");
        if (!confirmation.RequiresStrictConfirmation)
            throw new DomainException(02043, "Strict confirmation metadata is required for stop-sync execution.");
        if (confirmation.RequestedByUserId.HasValue && confirmation.RequestedByUserId.Value != UserId)
            throw new DomainException(02041, "Confirmation belongs to a different user.");

        using var payload = JsonDocument.Parse(confirmation.PayloadJson);
        var root = payload.RootElement;
        var action = root.GetProperty("action").GetString();
        var payloadEventId = root.GetProperty("objectId").GetGuid();
        if (action != "stop_sync" || payloadEventId != eventId)
            throw new DomainException(02042, "Confirmation does not match this stop-sync operation.");

        var evt = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "Event does not exist.");
        var before = new
        {
            evt.Source,
            evt.OutlookEventId,
            evt.OutlookChangeKey,
            evt.OutlookEtag,
            evt.UpdatedAt
        };

        evt.Source = "manual";
        evt.OutlookEventId = null;
        evt.OutlookChangeKey = null;
        evt.OutlookEtag = null;
        evt.UpdatedAt = DateTimeOffset.UtcNow;

        await new AuditVersionService(_db).RecordAsync(
            "event",
            evt.Id,
            before,
            new
            {
                evt.Source,
                evt.OutlookEventId,
                evt.OutlookChangeKey,
                evt.OutlookEtag,
                evt.UpdatedAt
            },
            ["source", "outlookEventId", "outlookChangeKey", "outlookEtag", "updatedAt"],
            confirmationId,
            "outlook",
            UserId,
            ct);

        await _confirmations.MarkExecutedAsync(
            confirmationId,
            JsonSerializer.Serialize(new { status = "stopped-sync", provider = "outlook", eventId }, JsonOptions),
            ct);

        return new { evt.Id, evt.Source };
    }

    private async Task<OperationConfirmationDto> CreateConfirmationAsync(
        string action,
        Guid? conflictId,
        string objectType,
        Guid objectId,
        string? graphEventId,
        string pimSnapshotJson,
        string externalSnapshotJson,
        string? mergedFieldsJson,
        string? reason,
        CancellationToken ct)
    {
        var risk = action == "stop_sync"
            ? OperationRiskLevel.L4BatchOrDestructiveGovernance
            : OperationRiskLevel.L3ExternalSourceOrWriteback;
        var payloadJson = JsonSerializer.Serialize(new
        {
            provider = "outlook",
            conflictId,
            action,
            objectType,
            objectId,
            graphEventId,
            mergedFieldsJson,
            reason
        });
        var previewJson = JsonSerializer.Serialize(new
        {
            action,
            objectType,
            objectId,
            GraphEventId = graphEventId,
            pim = JsonDocument.Parse(pimSnapshotJson).RootElement,
            outlook = JsonDocument.Parse(externalSnapshotJson).RootElement
        });

        // The raw Graph event id must never leave the server: it is redacted from
        // payload/preview JSON by AuditSnapshotSanitizer and the effect text is static.
        _ = graphEventId;

        return await _confirmations.CreateAsync(
            new CreateOperationConfirmationRequest(
                UserId,
                action == "stop_sync" ? "outlook.stop_sync" : "outlook.conflict." + action,
                $"Resolve Outlook conflict with action {action}.",
                risk,
                "outlook",
                payloadJson,
                previewJson,
                DateTimeOffset.UtcNow.AddHours(2),
                conflictId?.ToString("N") ?? objectId.ToString("N"),
                ["title", "location", "dtStart", "dtEnd"],
                ConflictActions,
                objectType,
                objectId,
                RequiresSecondLevelConfirmation: risk == OperationRiskLevel.L3ExternalSourceOrWriteback,
                BeforeJson: pimSnapshotJson,
                AfterJson: externalSnapshotJson,
                RequiresStrictConfirmation: risk == OperationRiskLevel.L4BatchOrDestructiveGovernance,
                ExternalEffect: "Graph event conflict (details hidden)",
                RecoveryPath: "Use audit timeline or conflict queue to revisit this decision."),
            ct);
    }

    private async Task<SyncConflictEntity> LoadConflictAsync(Guid conflictId, CancellationToken ct)
        => await _db.Set<SyncConflictEntity>()
            .FirstOrDefaultAsync(c => c.Id == conflictId && c.UserId == UserId, ct)
            ?? throw new DomainException(02039, "Sync conflict does not exist.");

    private static string NormalizeAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action)
            || !ConflictActions.Contains(action, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainException(02040, "Unsupported conflict action.");
        }

        return ConflictActions.Single(candidate => string.Equals(candidate, action, StringComparison.OrdinalIgnoreCase));
    }

    private static SyncConflictDetailDto Map(SyncConflictEntity conflict)
        => new(
            conflict.Id,
            conflict.Provider,
            conflict.ObjectType,
            conflict.ObjectId,
            conflict.GraphEventId,
            conflict.ConflictKind,
            conflict.Status,
            conflict.PimSnapshotJson,
            conflict.ExternalSnapshotJson,
            conflict.ResolvedConfirmationId);
}
