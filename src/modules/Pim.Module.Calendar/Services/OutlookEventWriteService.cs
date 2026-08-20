using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookEventWriteService
{
    private readonly PimDbContext _db;
    private readonly GraphCalendarClient _graph;
    private readonly EventAttachmentService _attachments;
    private readonly CalendarAuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutlookEventWriteService> _logger;

    public OutlookEventWriteService(
        PimDbContext db,
        GraphCalendarClient graph,
        CalendarAuditWriter audit,
        TimeProvider timeProvider,
        ILogger<OutlookEventWriteService> logger)
        : this(db, graph, new EventAttachmentService(db, graph), audit, timeProvider, logger)
    {
    }

    public OutlookEventWriteService(
        PimDbContext db,
        GraphCalendarClient graph,
        EventAttachmentService attachments,
        CalendarAuditWriter audit,
        TimeProvider timeProvider,
        ILogger<OutlookEventWriteService> logger)
    {
        _db = db;
        _graph = graph;
        _attachments = attachments;
        _audit = audit;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<OutlookWriteResult> ExecuteAsync(
        Guid userId, OutlookWriteRequest request, CancellationToken ct)
    {
        if (request.Operation is not ("create" or "update" or "delete"))
            throw new DomainException(02009, "不支持的 Microsoft 日程操作。");
        if (request.ClientOperationId == Guid.Empty)
            throw new DomainException(02009, "Client operation ID is required.");

        if (request.Draft is not null)
            request = request with { Draft = EventFieldValidator.ValidateAndNormalize(request.Draft) };

        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook is not connected.");
        if (connection.Status != "connected")
            throw new DomainException(02005, "Outlook is not connected.");

        var binding = await _db.Set<OutlookCalendarBindingEntity>()
            .FirstOrDefaultAsync(b => b.Id == request.CalendarBindingId && b.ConnectionId == connection.Id, ct)
            ?? throw new DomainException(02009, "日历绑定不存在。");
        if (!binding.CanEdit)
            throw new DomainException(02009, "此日历为只读日历，无法写回。");
        if (binding.RemoteState != "active")
            throw new DomainException(02009, "日历绑定状态异常。");
        if (!binding.IsSelected)
            throw new DomainException(02009, "日历未选中，无法写回。");

        var pimCalendar = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == binding.PimCalendarId && c.UserId == userId, ct)
            ?? throw new DomainException(02005, "日历不存在。");

        if (request.Draft?.RRule is string rrule && rrule.Length > 0 && string.IsNullOrWhiteSpace(rrule))
            throw new DomainException(02009, "RRule 不能为空白。");

        if (request.Operation == "create" && request.Scope != "instance")
            throw new DomainException(02009, "创建操作 Scope 仅支持 instance。");

        return request.Operation switch
        {
            "create" => await CreateAsync(userId, connection, binding, request, ct),
            "update" => await UpdateAsync(userId, connection, binding, request, ct),
            "delete" => await DeleteAsync(userId, connection, binding, request, ct),
            _ => throw new DomainException(02009, "不支持的 Microsoft 日程操作。")
        };
    }

    private async Task<OutlookWriteResult> CreateAsync(
        Guid userId, OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding, OutlookWriteRequest request, CancellationToken ct)
    {
        if (request.EventId is not null)
            throw new DomainException(02009, "新建事件不应包含 EventId。");
        if (request.Draft is null)
            throw new DomainException(02009, "新建事件必须包含 Draft。");
        if (request.Draft.CalendarId != binding.PimCalendarId)
            throw new DomainException(02009, "日历不匹配。");

        var draftPimFileReferences = await ValidateDraftPimFileReferencesAsync(
            userId, request.Draft.AttachmentReferences, ct);

        var batch = NewBatch(userId, connection.Id, binding.Id, binding.Name, "create");
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync(ct);

        var payload = OutlookEventMapper.BuildWritePayload(
            request.Draft, request.ClientOperationId.ToString("D"));
        JsonElement graphResult;
        try
        {
            graphResult = await _graph.CreateEventAsync(
                connection.Id, binding.GraphCalendarId, payload, ct);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            return await HandleReauthAsync(connection, batch, binding.Name, "create");
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException)
        {
            await FailBatchAsync(batch, ex, binding.Name, "create");
            throw;
        }

        var graphId = graphResult.GetProperty("id").GetString()!;
        var existing = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.OutlookConnectionId == connection.Id && e.OutlookEventId == graphId)
            .FirstOrDefaultAsync(CancellationToken.None);

        var localEvent = existing ?? new EventEntity();
        var generation = Guid.NewGuid();
        try
        {
            OutlookEventMapper.ApplyGraphEvent(
                localEvent, graphResult, binding.Id, binding.PimCalendarId,
                connection.Id, generation);
        }
        catch (Exception ex)
        {
            if (existing is not null)
                _db.Entry(existing).Reload();
            await FailBatchAsync(batch, ex, binding.Name, "create");
            throw;
        }

        var now = _timeProvider.GetUtcNow();
        if (existing is not null)
        {
            existing.DeletedAt = null;
            existing.DeletedByOperationId = null;
            existing.DeletedByOperationKind = null;
            existing.UpdatedAt = now;
            existing.DtStamp = now;
        }
        else
        {
            localEvent.CreatedAt = now;
            localEvent.UpdatedAt = now;
            localEvent.DtStamp = now;
            _db.Set<EventEntity>().Add(localEvent);
        }

        // Outlook/Graph attachments stay provider-read-only: persist the
        // validated native pimFile references plus the target's existing
        // outlook references; never store client-supplied outlook refs.
        localEvent.AttachmentReferencesJson =
            MergeAttachmentReferences(draftPimFileReferences, localEvent.AttachmentReferencesJson);

        var steps = new List<BatchStepEntry>
        {
            new("graph-create", "success", now),
            new("persist-local", "success", now)
        };
        SetBatchSummary(batch, binding.Name, "create", "completed", localEvent.Id, localEvent.Title,
            1, 0, 0, 0, binding.Id, steps);
        await _db.SaveChangesAsync(CancellationToken.None);

        await AuditSuccessAsync(userId, "outlook.event.create", localEvent.Id);

        return new OutlookWriteResult("created", MapEvent(localEvent), null, null, null, null);
    }

    private async Task<OutlookWriteResult> UpdateAsync(
        Guid userId, OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding, OutlookWriteRequest request, CancellationToken ct)
    {
        if (request.EventId is null)
            throw new DomainException(02009, "修改事件必须包含 EventId。");
        if (request.Draft is null)
            throw new DomainException(02009, "修改事件必须包含 Draft。");
        if (request.Draft.CalendarId != binding.PimCalendarId)
            throw new DomainException(02009, "日历不匹配。");
        if (string.IsNullOrEmpty(request.ExpectedEtag))
            throw new DomainException(02009, "修改事件需要 ExpectedEtag。");
        if (request.Scope is not ("instance" or "series"))
            throw new DomainException(02009, "Scope 必须是 instance 或 series。");

        var draftPimFileReferences = await ValidateDraftPimFileReferencesAsync(
            userId, request.Draft.AttachmentReferences, ct);

        var localEvent = await LoadEventAsync(request.EventId.Value, userId, ct);
        ValidateEventBinding(localEvent, binding.Id);

        var batch = NewBatch(userId, connection.Id, binding.Id, binding.Name, "update", localEvent.Id, localEvent.Title);
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync(ct);

        var graphTargetId = GetGraphTargetId(localEvent, request.Scope);
        var payload = OutlookEventMapper.BuildWritePayload(request.Draft, null);

        try
        {
            var expectedEtag = await ResolveWriteEtagAsync(
                localEvent, request.Scope, request.ExpectedEtag!, ct);
            var graphResult = await _graph.UpdateEventAsync(
                connection.Id, binding.GraphCalendarId, graphTargetId,
                expectedEtag, payload, ct);

            var resultGraphId = graphResult.GetProperty("id").GetString()!;
            EventEntity targetEntity;
            bool isNewEntity = false;

            if (resultGraphId == localEvent.OutlookEventId)
            {
                targetEntity = localEvent;
            }
            else
            {
                var existing = await _db.Set<EventEntity>()
                    .IgnoreQueryFilters()
                    .Where(e => e.OutlookConnectionId == connection.Id
                        && e.OutlookEventId == resultGraphId
                        && e.OutlookCalendarBindingId == binding.Id)
                    .FirstOrDefaultAsync(CancellationToken.None);

                if (existing is not null)
                {
                    targetEntity = existing;
                }
                else
                {
                    targetEntity = new EventEntity();
                    isNewEntity = true;
                }
            }

            var generation = Guid.NewGuid();
            var now = _timeProvider.GetUtcNow();

            if (targetEntity != localEvent)
            {
                try
                {
                    OutlookEventMapper.ApplyGraphEvent(
                        targetEntity, graphResult, binding.Id, binding.PimCalendarId,
                        connection.Id, generation);
                }
                catch
                {
                    if (!isNewEntity)
                        _db.Entry(targetEntity).Reload();
                    throw;
                }
                if (isNewEntity)
                {
                    targetEntity.CreatedAt = now;
                    targetEntity.UpdatedAt = now;
                    targetEntity.DtStamp = now;
                    _db.Set<EventEntity>().Add(targetEntity);
                }
                else
                {
                    targetEntity.DeletedAt = null;
                    targetEntity.DeletedByOperationId = null;
                    targetEntity.DeletedByOperationKind = null;
                    targetEntity.UpdatedAt = now;
                    targetEntity.DtStamp = now;
                }
            }
            else
            {
                try
                {
                    OutlookEventMapper.ApplyGraphEvent(
                        localEvent, graphResult, binding.Id, binding.PimCalendarId,
                        connection.Id, generation);
                }
                catch
                {
                    _db.Entry(localEvent).Reload();
                    throw;
                }
                localEvent.UpdatedAt = now;
                localEvent.DtStamp = now;
            }

            // Outlook/Graph attachments stay provider-read-only: persist the
            // validated native pimFile references from the draft plus the
            // target entity's existing outlook references; never store
            // client-supplied outlook refs as authoritative data.
            targetEntity.AttachmentReferencesJson =
                MergeAttachmentReferences(draftPimFileReferences, targetEntity.AttachmentReferencesJson);

            var steps = new List<BatchStepEntry>
            {
                new("graph-update", "success", _timeProvider.GetUtcNow()),
                new("persist-local", "success", _timeProvider.GetUtcNow())
            };
            SetBatchSummary(batch, binding.Name, "update", "completed",
                targetEntity.Id, targetEntity.Title, 0, 1, 0, 0, binding.Id, steps);
            await _db.SaveChangesAsync(CancellationToken.None);

            await AuditSuccessAsync(userId, "outlook.event.update", targetEntity.Id);

            return new OutlookWriteResult("updated", MapEvent(targetEntity), null, null, null, null);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            return await HandleReauthAsync(connection, batch, binding.Name, "update", localEvent.Id, localEvent.Title);
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            try
            {
                return await HandleConflictAsync(
                    binding, localEvent, binding.GraphCalendarId, graphTargetId, batch, binding.Name, "update", ct);
            }
            catch (OperationCanceledException oce)
            {
                await FailBatchAsync(batch, oce, binding.Name, "update", localEvent.Id, localEvent.Title);
                throw;
            }
            catch (OutlookReauthenticationRequiredException)
            {
                return await HandleReauthAsync(connection, batch, binding.Name, "update", localEvent.Id, localEvent.Title);
            }
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException)
        {
            await FailBatchAsync(batch, ex, binding.Name, "update", localEvent.Id, localEvent.Title);
            throw;
        }
    }

    private async Task<OutlookWriteResult> DeleteAsync(
        Guid userId, OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding, OutlookWriteRequest request, CancellationToken ct)
    {
        if (request.EventId is null)
            throw new DomainException(02009, "删除事件必须包含 EventId。");
        if (string.IsNullOrEmpty(request.ExpectedEtag))
            throw new DomainException(02009, "删除事件需要 ExpectedEtag。");
        if (request.Scope is not ("instance" or "series"))
            throw new DomainException(02009, "Scope 必须是 instance 或 series。");

        var localEvent = await LoadEventAsync(request.EventId.Value, userId, ct);
        ValidateEventBinding(localEvent, binding.Id);

        var batch = NewBatch(userId, connection.Id, binding.Id, binding.Name, "delete", localEvent.Id, localEvent.Title);
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync(ct);

        var graphTargetId = GetGraphTargetId(localEvent, request.Scope);
        var now = _timeProvider.GetUtcNow();

        try
        {
            var expectedEtag = await ResolveWriteEtagAsync(
                localEvent, request.Scope, request.ExpectedEtag!, ct);
            await _graph.DeleteEventAsync(
                connection.Id, binding.GraphCalendarId, graphTargetId,
                expectedEtag, ct);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            return await HandleReauthAsync(connection, batch, binding.Name, "delete", localEvent.Id, localEvent.Title);
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            try
            {
                return await HandleConflictAsync(
                    binding, localEvent, binding.GraphCalendarId, graphTargetId, batch, binding.Name, "delete", ct);
            }
            catch (OperationCanceledException oce)
            {
                await FailBatchAsync(batch, oce, binding.Name, "delete", localEvent.Id, localEvent.Title);
                throw;
            }
            catch (OutlookReauthenticationRequiredException)
            {
                return await HandleReauthAsync(connection, batch, binding.Name, "delete", localEvent.Id, localEvent.Title);
            }
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException)
        {
            await FailBatchAsync(batch, ex, binding.Name, "delete", localEvent.Id, localEvent.Title);
            throw;
        }

        int affectedCount;
        if (request.Scope == "series")
        {
            var seriesEvents = await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .Where(e => e.OutlookCalendarBindingId == binding.Id
                    && e.DeletedAt == null
                    && (e.OutlookEventId == graphTargetId || e.OutlookSeriesMasterId == graphTargetId))
                .ToListAsync(CancellationToken.None);

            var ids = new HashSet<Guid>(seriesEvents.Select(e => e.Id));
            if (!ids.Contains(localEvent.Id))
            {
                seriesEvents.Add(localEvent);
            }

            foreach (var evt in seriesEvents)
            {
                ApplySoftDelete(evt, request.ClientOperationId, now);
            }
            affectedCount = seriesEvents.Count;
        }
        else
        {
            ApplySoftDelete(localEvent, request.ClientOperationId, now);
            affectedCount = 1;
        }

        batch.Status = "completed";
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.UpdatedCount = affectedCount;
        batch.ConfirmationCount = 0;
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = "graph-delete", status = "success", timestamp = now },
            new { step = "soft-delete", status = "success", timestamp = now }
        });
        PopulateBatchHistory(batch, binding.Name, "delete", "completed",
            localEvent.Id, localEvent.Title, 0, 0, affectedCount, 0, 0, binding.Id);
        await _db.SaveChangesAsync(CancellationToken.None);

        await AuditSuccessAsync(userId, "outlook.event.delete", localEvent.Id);

        return new OutlookWriteResult("deleted", null, null, null, null, null);
    }

    private async Task<EventEntity> LoadEventAsync(Guid eventId, Guid userId, CancellationToken ct)
    {
        var evt = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == userId, ct)
            ?? throw new DomainException(02009, "事件不存在。");
        if (evt.Calendar.DeletedAt is not null)
            throw new DomainException(02009, "事件所属日历已被删除。");
        return evt;
    }

    private async Task<IReadOnlyList<EventAttachmentReferenceDto>?> ValidateDraftPimFileReferencesAsync(
        Guid userId,
        IReadOnlyList<EventAttachmentReferenceDto>? references,
        CancellationToken ct)
    {
        if (references is null)
            return null;
        var pimFileReferences = references
            .Where(reference => string.Equals(reference.Kind, "pimFile", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var reference in pimFileReferences)
            await _attachments.ValidatePimFileReferenceAsync(userId, reference, ct);
        return pimFileReferences;
    }

    private static string MergeAttachmentReferences(
        IReadOnlyList<EventAttachmentReferenceDto>? pimFileReferences,
        string existingReferencesJson)
    {
        if (pimFileReferences is null)
            return existingReferencesJson;
        var outlookReferences = EventFieldCodec.DeserializeAttachments(existingReferencesJson)
            .Where(reference => string.Equals(reference.Kind, "outlook", StringComparison.OrdinalIgnoreCase));
        return EventFieldCodec.SerializeAttachments(pimFileReferences.Concat(outlookReferences).ToList());
    }

    private static void ValidateEventBinding(EventEntity evt, Guid bindingId)
    {
        if (evt.OutlookCalendarBindingId != bindingId)
            throw new DomainException(02009, "事件不属于请求的日历绑定。");
        if (string.IsNullOrEmpty(evt.OutlookEventId))
            throw new DomainException(02009, "事件没有 Outlook 关联，无法操作。");
        if (evt.OutlookSyncState == "legacy-unbound")
            throw new DomainException(02009, "事件处于 legacy-unbound 状态，无法写回。");
    }

    private static string GetGraphTargetId(EventEntity evt, string scope)
        => scope == "series"
            ? evt.OutlookSeriesMasterId ?? evt.OutlookEventId!
            : evt.OutlookEventId!;

    private async Task<string> ResolveWriteEtagAsync(
        EventEntity evt, string scope, string requestEtag, CancellationToken ct)
    {
        if (scope == "series" && !string.IsNullOrEmpty(evt.OutlookSeriesMasterId))
        {
            var masterEtag = await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .Where(e => e.OutlookCalendarBindingId == evt.OutlookCalendarBindingId
                    && e.OutlookEventId == evt.OutlookSeriesMasterId)
                .Select(e => e.OutlookEtag)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(masterEtag))
                return masterEtag;
        }
        return requestEtag;
    }

    private async Task<OutlookWriteResult> HandleConflictAsync(
        OutlookCalendarBindingEntity binding, EventEntity localEvent,
        string graphCalendarId, string graphEventId,
        OutlookSyncBatchEntity batch, string calendarName, string operation, CancellationToken ct)
    {
        async Task<OutlookWriteResult> FailConflictResolveAsync(
            string errorCode, string errorMessage, string resultCode, string resultMessage,
            string step, string stepStatus)
        {
            var now = _timeProvider.GetUtcNow();
            batch.Status = "failed";
            batch.FailureCount = 1;
            batch.FinishedAt = now;
            batch.UpdatedAt = now;
            batch.ConfirmationCount = 0;
            SetBatchError(batch, errorCode, errorMessage);
            PopulateBatchHistory(batch, calendarName, operation, "failed", null, null,
                0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
            batch.StepsJson = JsonSerializer.Serialize(new[]
            {
                new { step, status = stepStatus, timestamp = now }
            });
            await _db.SaveChangesAsync(CancellationToken.None);
            return new OutlookWriteResult(
                "error", null, null, null, resultCode, resultMessage);
        }

        JsonElement? latest;
        try
        {
            latest = await _graph.GetEventAsync(
                binding.ConnectionId, graphCalendarId, graphEventId, ct);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await FailConflictResolveAsync(
                "conflict-resolve-failed", "Failed to retrieve latest event from Graph.",
                "CONFLICT_RESOLVE_FAILED", "冲突后获取最新数据失败。",
                "conflict-fetch", "failed");
        }

        if (latest is null)
        {
            return await FailConflictResolveAsync(
                "conflict-resolve-not-found", "Latest event not found on Graph.",
                "CONFLICT_EVENT_MISSING", "冲突后最新事件在远端已不存在。",
                "conflict-fetch", "not-found");
        }

        EventResponse? latestEvent;
        string? latestEtag;
        try
        {
            var transient = new EventEntity { Id = localEvent.Id };
            OutlookEventMapper.ApplyGraphEvent(
                transient, latest.Value, binding.Id, binding.PimCalendarId,
                binding.ConnectionId, Guid.NewGuid());

            // PR2: the typed latest event must carry remote attachment
            // references so the client re-compare does not report spurious
            // attachment diffs. Hydration is best-effort: a failure keeps the
            // typed latest event usable without attachments.
            try
            {
                var references = await _attachments.GetOutlookAttachmentReferencesAsync(
                    binding.ConnectionId, graphCalendarId, graphEventId, ct);
                transient.AttachmentReferencesJson =
                    EventFieldCodec.SerializeAttachments(references);
            }
            catch (OutlookReauthenticationRequiredException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Outlook conflict attachment hydration failed for binding {BindingId}, event {EventId}",
                    binding.Id,
                    graphEventId);
            }

            latestEtag = transient.OutlookEtag;
            latestEvent = EventResponseMapper.Map(transient);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await FailConflictResolveAsync(
                "conflict-resolve-failed", "Failed to resolve latest event from Graph.",
                "CONFLICT_RESOLVE_FAILED", "冲突后获取最新数据失败。",
                "conflict-fetch", "failed");
        }

        batch.Status = "failed";
        batch.ConflictCount = 1;
        batch.FinishedAt = _timeProvider.GetUtcNow();
        batch.UpdatedAt = _timeProvider.GetUtcNow();
        batch.ConfirmationCount = 0;
        SetBatchError(batch, "412-conflict", "ETag conflict with current Graph state.");
        PopulateBatchHistory(batch, calendarName, operation, "failed", null, null,
            0, 0, 0, 1, 0, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "conflict", timestamp = _timeProvider.GetUtcNow() },
            new { step = "conflict-fetch", status = "success", timestamp = _timeProvider.GetUtcNow() }
        });
        await _db.SaveChangesAsync(CancellationToken.None);

        return new OutlookWriteResult(
            "conflict", null, latestEvent, latestEtag,
            "CONFLICT",
            "事件在 Outlook 中已被修改。请刷新后重新编辑。");
    }

    private async Task<OutlookWriteResult> HandleReauthAsync(
        OutlookConnectionEntity connection, OutlookSyncBatchEntity batch,
        string calendarName, string operation, Guid? eventId = null, string? eventTitle = null)
    {
        var now = _timeProvider.GetUtcNow();
        connection.Status = "reauth-required";
        connection.TokenHealth = "interaction-required";
        connection.LastError = "Microsoft 需要重新授权以继续写回操作。";
        connection.UpdatedAt = now;

        batch.Status = "failed";
        batch.FailureCount = 1;
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.ConfirmationCount = 0;
        SetBatchError(batch, "reauth-required", "Microsoft 需要重新授权。");

        PopulateBatchHistory(batch, calendarName, operation, "failed", eventId, eventTitle,
            0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "reauth-required", timestamp = now }
        });

        await _db.SaveChangesAsync(CancellationToken.None);
        return new OutlookWriteResult(
            "reauth-required", null, null, null, "REAUTH_REQUIRED",
            "Microsoft 需要重新授权。");
    }

    private static void ApplySoftDelete(EventEntity evt, Guid operationId, DateTimeOffset now)
    {
        evt.DeletedAt = now;
        evt.DeletedByOperationId = operationId;
        evt.DeletedByOperationKind = "outlook-writeback";
        evt.UpdatedAt = now;
    }

    private async Task AuditSuccessAsync(Guid userId, string action, Guid resourceId)
    {
        try
        {
            await _audit.RecordSuccessAsync(
                userId, action, "calendar_event", resourceId, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write success audit for {Action} on event {EventId}", action, resourceId);
        }
    }

    private OutlookSyncBatchEntity NewBatch(Guid userId, Guid connectionId, Guid bindingId,
        string calendarName, string operation, Guid? eventId = null, string? eventTitle = null)
    {
        var now = _timeProvider.GetUtcNow();
        var batch = new OutlookSyncBatchEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = connectionId,
            Provider = "outlook",
            Mode = "writeback",
            Status = "running",
            RequestedCalendarIdsJson = JsonSerializer.Serialize(new[] { bindingId.ToString() }),
            StartedAt = now,
            UpdatedAt = now
        };

        batch.PerCalendarJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                bindingId = bindingId.ToString(),
                calendarName,
                operation,
                status = "running",
                eventId = eventId?.ToString(),
                eventTitle,
                createdCount = 0,
                updatedCount = 0,
                deletedCount = 0,
                conflictCount = 0,
                failureCount = 0,
                timestamp = now
            }
        });

        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "running", timestamp = now }
        });

        batch.ConfirmationCount = 0;

        return batch;
    }

    private async Task FailBatchAsync(OutlookSyncBatchEntity batch, Exception ex,
        string calendarName, string operation, Guid? eventId = null, string? eventTitle = null)
    {
        var now = _timeProvider.GetUtcNow();
        batch.Status = "failed";
        batch.FailureCount = 1;
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.ConfirmationCount = 0;
        if (ex is GraphRequestException grex)
        {
            SetBatchError(batch, $"graph-{(int)grex.StatusCode!.Value}", "Graph write request failed.");
        }
        else
        {
            SetBatchError(batch, "unknown", "An unexpected error occurred.");
        }

        PopulateBatchHistory(batch, calendarName, operation, "failed", eventId, eventTitle,
            0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "failed", timestamp = now }
        });
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception saveEx)
        {
            _logger.LogWarning(saveEx, "Failed to persist failure history for batch {BatchId}", batch.Id);
        }
    }

    private static void SetBatchError(OutlookSyncBatchEntity batch, string code, string message)
    {
        batch.ErrorsJson = JsonSerializer.Serialize(new[]
        {
            new { code, message }
        });
        batch.ErrorSummary = $"[{code}] {message}";
    }

    private static EventResponse MapEvent(EventEntity e) =>
        EventResponseMapper.Map(e);

    private sealed record BatchStepEntry(string Step, string Status, DateTimeOffset Timestamp);

    private void SetBatchSummary(
        OutlookSyncBatchEntity batch, string calendarName,
        string operation, string status, Guid? eventId, string? eventTitle,
        int created, int updated, int conflict, int failure,
        Guid bindingId, List<BatchStepEntry> steps)
    {
        var now = _timeProvider.GetUtcNow();
        batch.Status = status;
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.CreatedCount = created;
        batch.UpdatedCount = updated;
        batch.ConflictCount = conflict;
        batch.FailureCount = failure;
        batch.ConfirmationCount = 0;

        PopulateBatchHistory(batch, calendarName, operation, status, eventId, eventTitle,
            created, updated, 0, conflict, failure, bindingId);
        batch.StepsJson = JsonSerializer.Serialize(steps.Select(s => new
        {
            step = s.Step,
            status = s.Status,
            timestamp = s.Timestamp
        }));
    }

    private static void PopulateBatchHistory(
        OutlookSyncBatchEntity batch, string calendarName,
        string operation, string status, Guid? eventId, string? eventTitle,
        int created, int updated, int deleted, int conflict, int failure,
        Guid bindingId)
    {
        batch.PerCalendarJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                bindingId = bindingId.ToString(),
                calendarName,
                operation,
                status,
                eventId = eventId?.ToString(),
                eventTitle,
                createdCount = created,
                updatedCount = updated,
                deletedCount = deleted,
                conflictCount = conflict,
                failureCount = failure,
                timestamp = batch.FinishedAt ?? batch.UpdatedAt
            }
        });
    }

    private static Guid GetBindingIdFromBatch(OutlookSyncBatchEntity batch)
    {
        try
        {
            using var doc = JsonDocument.Parse(batch.RequestedCalendarIdsJson);
            if (doc.RootElement.GetArrayLength() > 0)
                return Guid.Parse(doc.RootElement[0].GetString()!);
        }
        catch { }
        return Guid.Empty;
    }
}
