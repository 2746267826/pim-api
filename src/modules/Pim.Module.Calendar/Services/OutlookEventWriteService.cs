using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);

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
        => await ExecuteCoreAsync(userId, request, null, ct);

    public async Task<OutlookWriteResult> ExecuteUndoAsync(
        Guid userId, OutlookWriteRequest request, Guid undoOfOperationId, CancellationToken ct)
        => await ExecuteCoreAsync(userId, request, undoOfOperationId, ct);

    private async Task<OutlookWriteResult> ExecuteCoreAsync(
        Guid userId, OutlookWriteRequest request, Guid? undoOfOperationId, CancellationToken ct)
    {
        if (request.Operation is not ("create" or "update" or "delete"))
            throw new DomainException(02009, "不支持的 Microsoft 日程操作。");
        if (request.ClientOperationId == Guid.Empty)
            throw new DomainException(02009, "Client operation ID is required.");

        if (request.Draft is not null)
            request = request with
            {
                Draft = EventFieldValidator.ValidateAndNormalize(
                    request.Draft,
                    requireAllDayDates: request.Operation != "update")
            };
        if (request.Operation == "create"
            && request.Draft is { IsAllDay: true } createDraft
            && (!createDraft.AllDayStartDate.HasValue || !createDraft.AllDayEndDateExclusive.HasValue))
        {
            throw new DomainException(02009, "新建全天事件必须提供 allDayStartDate 与 allDayEndDateExclusive。");
        }

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

        var payloadHash = ComputePayloadHash(request, undoOfOperationId);
        var existing = await _db.Set<OutlookWritebackOperationEntity>()
            .FirstOrDefaultAsync(
                o => o.UserId == userId && o.ClientOperationId == request.ClientOperationId,
                ct);
        if (existing is not null)
        {
            var replay = await TryReplayOrResumeAsync(existing, payloadHash, ct);
            if (replay is not null)
                return replay;
        }

        var operation = existing ?? new OutlookWritebackOperationEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ClientOperationId = request.ClientOperationId,
            UndoOfOperationId = undoOfOperationId,
            Operation = request.Operation,
            CalendarBindingId = binding.Id,
            RequestEventId = request.EventId,
            EventId = request.EventId,
            Scope = request.Scope,
            PayloadHash = payloadHash,
            State = "processing",
            AttemptCount = 1,
            UndoState = undoOfOperationId is null && request.Scope == "instance"
                ? "available"
                : "unavailable",
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        if (existing is null)
            _db.Set<OutlookWritebackOperationEntity>().Add(operation);

        operation.CalendarBindingId = binding.Id;
        operation.RequestEventId ??= request.EventId;
        operation.EventId = request.EventId;
        operation.Scope = request.Scope;

        return request.Operation switch
        {
            "create" => await CreateAsync(userId, connection, binding, request, operation, ct),
            "update" => await UpdateAsync(userId, connection, binding, request, operation, ct),
            "delete" => await DeleteAsync(userId, connection, binding, request, operation, ct),
            _ => throw new DomainException(02009, "不支持的 Microsoft 日程操作。")
        };
    }

    public async Task<OutlookWriteResult> UndoAsync(
        Guid userId, Guid originalClientOperationId, CancellationToken ct)
    {
        var original = await _db.Set<OutlookWritebackOperationEntity>()
            .FirstOrDefaultAsync(
                o => o.UserId == userId && o.ClientOperationId == originalClientOperationId,
                ct)
            ?? throw new DomainException(02009, "要撤销的 Outlook 写回操作不存在。");
        if (original.State != "completed")
            throw new DomainException(02009, "只有已完成的写回操作才能撤销。");
        if (original.UndoOfOperationId is not null)
            throw new DomainException(02009, "撤销操作本身不可再次撤销。");
        if (original.UndoState == "in-progress")
        {
            var recoveredId = DeterministicUndoId(userId, originalClientOperationId);
            var recovered = await _db.Set<OutlookWritebackOperationEntity>()
                .FirstOrDefaultAsync(
                    o => o.UserId == userId
                        && o.ClientOperationId == recoveredId
                        && o.State == "completed",
                    ct);
            if (recovered is null)
                return OperationInProgress(original);
            original.UndoState = "used";
            original.UpdatedAt = _timeProvider.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return ReplayCached(recovered);
        }
        if (original.UndoState == "used")
            throw new DomainException(02009, "该写回操作已经撤销过。");
        if (original.UndoState != "available")
            throw new DomainException(02009, "该日历操作不提供自动撤销，请在 Outlook 中手动恢复。");

        var undoClientOperationId = DeterministicUndoId(userId, originalClientOperationId);
        var priorUndo = await _db.Set<OutlookWritebackOperationEntity>()
            .FirstOrDefaultAsync(
                o => o.UserId == userId && o.ClientOperationId == undoClientOperationId,
                ct);
        if (priorUndo is { State: "completed" })
        {
            original.UndoState = "used";
            original.UpdatedAt = _timeProvider.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return ReplayCached(priorUndo);
        }

        original.UndoState = "in-progress";
        original.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        try
        {
            OutlookWriteRequest undoRequest;
            switch (original.Operation)
            {
                case "create":
                {
                    var eventId = original.ResultEventId
                        ?? throw new DomainException(02009, "原创建操作缺少日程 ID，无法撤销。");
                    undoRequest = new OutlookWriteRequest(
                        "delete",
                        original.CalendarBindingId,
                        eventId,
                        null,
                        original.Scope,
                        undoClientOperationId,
                        ExpectedEtag: null);
                    break;
                }
                case "update":
                {
                    var eventId = original.ResultEventId ?? original.EventId
                        ?? throw new DomainException(02009, "原更新操作缺少日程 ID，无法撤销。");
                    var eventEntity = await _db.Set<EventEntity>()
                        .FirstOrDefaultAsync(
                            e => e.Id == eventId && e.Calendar.UserId == userId,
                            ct)
                        ?? throw new DomainException(02009, "原更新操作的日程已不存在，无法撤销。");
                    if (string.IsNullOrWhiteSpace(original.PreviousDraftJson))
                        throw new DomainException(02009, "原更新操作缺少可恢复内容，无法撤销。");
                    undoRequest = new OutlookWriteRequest(
                        "update",
                        original.CalendarBindingId,
                        eventId,
                        DeserializeDraft(original.PreviousDraftJson),
                        original.Scope,
                        undoClientOperationId,
                        ExpectedEtag: eventEntity.OutlookEtag);
                    break;
                }
                case "delete":
                {
                    if (string.IsNullOrWhiteSpace(original.PreviousDraftJson))
                        throw new DomainException(02009, "原删除操作缺少日程快照，无法撤销。");
                    var draft = DeserializeDraft(original.PreviousDraftJson);
                    undoRequest = new OutlookWriteRequest(
                        "create",
                        original.CalendarBindingId,
                        null,
                        draft,
                        "instance",
                        undoClientOperationId);
                    break;
                }
                default:
                    throw new DomainException(02009, "不支持的撤销操作。");
            }

            var result = await ExecuteUndoAsync(userId, undoRequest, original.Id, ct);
            if (result.Status is "created" or "updated" or "deleted")
            {
                original.UndoState = "used";
                original.UpdatedAt = _timeProvider.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return result with
                {
                    UndoAvailable = false,
                    UserMessage = result.UserMessage + " 已撤销上一次 Outlook 写回。"
                };
            }

            original.UndoState = "available";
            original.UpdatedAt = _timeProvider.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch
        {
            original.UndoState = "available";
            original.UpdatedAt = _timeProvider.GetUtcNow();
            try { await _db.SaveChangesAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Replays a durable receipt when the target event row is no longer visible
    /// (for example a successful delete followed by an idempotent retry). The
    /// request identity is checked before returning the cached result so the same
    /// operation ID cannot silently stand in for a different event or operation.
    /// </summary>
    public async Task<OutlookWriteResult?> TryReplayWithoutEventAsync(
        Guid userId,
        string requestedOperation,
        Guid clientOperationId,
        Guid? requestEventId,
        string? scope,
        CancellationToken ct)
    {
        var operation = await _db.Set<OutlookWritebackOperationEntity>()
            .FirstOrDefaultAsync(
                o => o.UserId == userId && o.ClientOperationId == clientOperationId,
                ct);
        if (operation is null)
            return null;

        if (!string.Equals(operation.Operation, requestedOperation, StringComparison.OrdinalIgnoreCase))
            return IdempotencyConflict(operation);
        if (requestEventId is { } requested
            && operation.RequestEventId is { } stored
            && requested != stored)
            return IdempotencyConflict(operation);
        if (!string.IsNullOrWhiteSpace(scope)
            && !string.Equals(operation.Scope, scope, StringComparison.OrdinalIgnoreCase))
            return IdempotencyConflict(operation);

        return operation.State switch
        {
            "completed" or "conflict" => ReplayCached(operation),
            "processing" => await ReplayProcessingAsync(operation, ct),
            "unknown" => OutcomeUnknown(operation),
            _ => null
        };
    }

    /// <summary>
    /// A process crash between the Graph write and the local commit can leave a durable
    /// "processing" row behind. A fresh in-flight request keeps returning in-progress;
    /// an expired lease is converged to "unknown" so it never blocks the client forever
    /// and never silently re-applies a write whose remote outcome was not recorded.
    /// </summary>
    private async Task<OutlookWriteResult> ReplayProcessingAsync(
        OutlookWritebackOperationEntity operation, CancellationToken ct)
    {
        if (_timeProvider.GetUtcNow() - operation.UpdatedAt < ProcessingLease)
            return OperationInProgress(operation);

        operation.State = "unknown";
        operation.ErrorCode = "OPERATION_OUTCOME_UNKNOWN";
        operation.ErrorMessage = "该操作的处理进程已中断，Outlook 侧结果未知。请先在 Outlook 核对，勿直接重复提交。";
        operation.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return OutcomeUnknown(operation);
    }

    private static Guid DeterministicUndoId(Guid userId, Guid originalClientOperationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"outlook-undo:{userId:D}:{originalClientOperationId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task<OutlookWriteResult> CreateAsync(
        Guid userId, OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding, OutlookWriteRequest request,
        OutlookWritebackOperationEntity operation, CancellationToken ct)
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
        var startReplay = await SaveStartAsync(batch, operation, operation.PayloadHash, ct);
        if (startReplay is not null)
            return startReplay;

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
            return await HandleReauthAsync(connection, batch, operation, binding.Name, "create");
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException)
        {
            await FailBatchAsync(batch, operation, ex, binding.Name, "create");
            throw;
        }

        var graphId = graphResult.GetProperty("id").GetString()!;
        var existingEvent = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.OutlookConnectionId == connection.Id && e.OutlookEventId == graphId)
            .FirstOrDefaultAsync(CancellationToken.None);

        var localEvent = existingEvent ?? new EventEntity();
        var generation = Guid.NewGuid();
        try
        {
            OutlookEventMapper.ApplyGraphEvent(
                localEvent, graphResult, binding.Id, binding.PimCalendarId,
                connection.Id, generation);
        }
        catch (Exception ex)
        {
            if (existingEvent is not null)
                _db.Entry(existingEvent).Reload();
            await MarkOutcomeUnknownAsync(operation, batch, ex, binding.Name, "create");
            throw;
        }

        var now = _timeProvider.GetUtcNow();
        if (existingEvent is not null)
        {
            existingEvent.DeletedAt = null;
            existingEvent.DeletedByOperationId = null;
            existingEvent.DeletedByOperationKind = null;
            existingEvent.UpdatedAt = now;
            existingEvent.DtStamp = now;
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

        // 工单 04：is_muted 是 PIM 原生字段（Graph 事件没有对应属性，BuildWritePayload 也不会下发），
        // 因此只能由草稿显式落到本地实体 —— 否则 Outlook 日历下的日程没法设置「期间静音」。
        localEvent.IsMuted = request.Draft.IsMuted;

        var steps = new List<BatchStepEntry>
        {
            new("graph-create", "success", now),
            new("persist-local", "success", now)
        };
        operation.EventId = localEvent.Id;
        operation.ResultEventId = localEvent.Id;
        operation.ResultStatus = "created";
        operation.UndoState = operation.UndoOfOperationId is null ? "available" : "unavailable";
        var result = BuildSuccessResult(operation, "created", localEvent);
        operation.ResultJson = SerializeResult(result);
        operation.State = "completed";
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.CompletedAt = now;
        operation.UpdatedAt = now;
        SetBatchSummary(batch, binding.Name, "create", "completed", localEvent.Id, localEvent.Title,
            1, 0, 0, 0, binding.Id, steps);
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await MarkOutcomeUnknownAsync(operation, batch, ex, binding.Name, "create");
            throw;
        }

        await AuditSuccessAsync(
            userId, operation.UndoOfOperationId is null ? "outlook.event.create" : "outlook.event.undo",
            localEvent.Id, operation);

        return result;
    }

    private async Task<OutlookWriteResult> UpdateAsync(
        Guid userId, OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding, OutlookWriteRequest request,
        OutlookWritebackOperationEntity operation, CancellationToken ct)
    {
        if (request.EventId is null)
            throw new DomainException(02009, "修改事件必须包含 EventId。");
        if (request.Draft is null)
            throw new DomainException(02009, "修改事件必须包含 Draft。");
        if (request.Draft.CalendarId != binding.PimCalendarId)
            throw new DomainException(02009, "日历不匹配。");
        if (request.Scope is not ("instance" or "series"))
            throw new DomainException(02009, "Scope 必须是 instance 或 series。");

        var draftPimFileReferences = await ValidateDraftPimFileReferencesAsync(
            userId, request.Draft.AttachmentReferences, ct);

        var target = await ResolveWriteTargetAsync(userId, binding, request, ct);
        var localEvent = target.LocalEvent;
        ValidateUpdateAllDayDates(target.ValidationEntity, request.Draft);

        operation.EventId = localEvent.Id;
        operation.Scope = request.Scope;
        operation.PreviousDraftJson = request.Scope == "instance"
            ? SerializeDraft(
                target.PreviousDraft
                ?? OutlookEventDraftFactory.FromEntity(target.ValidationEntity))
            : null;
        operation.UndoState = operation.UndoOfOperationId is null && target.CanUndo
            ? "available"
            : "unavailable";

        var batch = NewBatch(userId, connection.Id, binding.Id, binding.Name, "update", localEvent.Id, localEvent.Title);
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        var startReplay = await SaveStartAsync(batch, operation, operation.PayloadHash, ct);
        if (startReplay is not null)
            return startReplay;

        var graphTargetId = target.GraphTargetId;
        var payload = OutlookEventMapper.BuildWritePayload(request.Draft, null);
        var outcomeUnknown = false;
        var conflictEvent = target.IsSyntheticOccurrence
            ? new EventEntity { Id = request.EventId ?? localEvent.Id }
            : localEvent;

        try
        {
            var expectedEtag = await ResolveWriteEtagAsync(
                binding, target.EtagEntity, request.Scope, request.ExpectedEtag, ct);
            var graphResult = await _graph.UpdateEventAsync(
                connection.Id, binding.GraphCalendarId, graphTargetId,
                expectedEtag, payload, ct);

            var resultGraphId = graphResult.GetProperty("id").GetString()!;
            EventEntity targetEntity;
            bool isNewEntity = false;

            if (target.ExistingOccurrence is not null)
            {
                targetEntity = target.ExistingOccurrence;
            }
            else if (resultGraphId == localEvent.OutlookEventId)
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
                    existing = target.Master is null
                        ? null
                        : await FindOccurrenceExceptionAsync(
                            target.Master.Id, target.RecurrenceId, ct);
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

            if (target.IsSyntheticOccurrence && target.Master is not null)
            {
                targetEntity.SeriesMasterId = target.Master.Id;
                targetEntity.IsException = true;
                targetEntity.IsSeriesMaster = false;
                targetEntity.RecurrenceId = target.RecurrenceId;
                targetEntity.RRule = null;
            }

            // Outlook/Graph attachments stay provider-read-only: persist the
            // validated native pimFile references from the draft plus the
            // target entity's existing outlook references; never store
            // client-supplied outlook refs as authoritative data.
            targetEntity.AttachmentReferencesJson =
                MergeAttachmentReferences(draftPimFileReferences, targetEntity.AttachmentReferencesJson);

            // 工单 04：同上，静音是 PIM 原生字段，随写回草稿整体替换（null = 未指定 → 跟随日历本默认）。
            targetEntity.IsMuted = request.Draft!.IsMuted;

            var steps = new List<BatchStepEntry>
            {
                new("graph-update", "success", _timeProvider.GetUtcNow()),
                new("persist-local", "success", _timeProvider.GetUtcNow())
            };
            operation.EventId = targetEntity.Id;
            operation.ResultEventId = targetEntity.Id;
            operation.ResultStatus = "updated";
            operation.UndoState = operation.UndoOfOperationId is null && target.CanUndo
                ? "available"
                : "unavailable";
            var result = BuildSuccessResult(operation, "updated", targetEntity);
            operation.ResultJson = SerializeResult(result);
            operation.State = "completed";
            operation.ErrorCode = null;
            operation.ErrorMessage = null;
            operation.CompletedAt = _timeProvider.GetUtcNow();
            operation.UpdatedAt = operation.CompletedAt.Value;
            SetBatchSummary(batch, binding.Name, "update", "completed",
                targetEntity.Id, targetEntity.Title, 0, 1, 0, 0, binding.Id, steps);
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                outcomeUnknown = true;
                await MarkOutcomeUnknownAsync(
                    operation, batch, ex, binding.Name, "update", targetEntity.Id, targetEntity.Title);
                throw;
            }

            await AuditSuccessAsync(
                userId, operation.UndoOfOperationId is null ? "outlook.event.update" : "outlook.event.undo",
                targetEntity.Id, operation);

            return result;
        }
        catch (OutlookReauthenticationRequiredException)
        {
            return await HandleReauthAsync(
                connection, batch, operation, binding.Name, "update", localEvent.Id, localEvent.Title);
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            try
            {
                return await HandleConflictAsync(
                    binding, conflictEvent, binding.GraphCalendarId, graphTargetId, batch, operation,
                    binding.Name, "update", ct);
            }
            catch (OperationCanceledException oce)
            {
                await FailBatchAsync(
                    batch, operation, oce, binding.Name, "update", conflictEvent.Id, conflictEvent.Title);
                throw;
            }
            catch (OutlookReauthenticationRequiredException)
            {
                return await HandleReauthAsync(
                    connection, batch, operation, binding.Name, "update", conflictEvent.Id, conflictEvent.Title);
            }
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException && !outcomeUnknown)
        {
            await FailBatchAsync(
                batch, operation, ex, binding.Name, "update", localEvent.Id, localEvent.Title);
            throw;
        }
    }

    private async Task<OutlookWriteResult> DeleteAsync(
        Guid userId, OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding, OutlookWriteRequest request,
        OutlookWritebackOperationEntity operation, CancellationToken ct)
    {
        if (request.EventId is null)
            throw new DomainException(02009, "删除事件必须包含 EventId。");
        if (request.Scope is not ("instance" or "series"))
            throw new DomainException(02009, "Scope 必须是 instance 或 series。");

        var target = await ResolveWriteTargetAsync(userId, binding, request, ct);
        var localEvent = target.LocalEvent;

        operation.EventId = localEvent.Id;
        operation.Scope = request.Scope;
        operation.PreviousDraftJson = request.Scope == "instance"
            ? SerializeDraft(
                target.PreviousDraft
                ?? OutlookEventDraftFactory.FromEntity(target.ValidationEntity))
            : null;
        operation.UndoState = operation.UndoOfOperationId is null
            && request.Scope == "instance"
            && !target.IsOccurrenceWrite
            ? "available"
            : "unavailable";

        var batch = NewBatch(userId, connection.Id, binding.Id, binding.Name, "delete", localEvent.Id, localEvent.Title);
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        var startReplay = await SaveStartAsync(batch, operation, operation.PayloadHash, ct);
        if (startReplay is not null)
            return startReplay;

        var graphTargetId = target.GraphTargetId;
        var now = _timeProvider.GetUtcNow();
        var conflictEvent = target.IsSyntheticOccurrence
            ? new EventEntity { Id = request.EventId ?? localEvent.Id }
            : localEvent;

        try
        {
            var expectedEtag = await ResolveWriteEtagAsync(
                binding, target.EtagEntity, request.Scope, request.ExpectedEtag, ct);
            await _graph.DeleteEventAsync(
                connection.Id, binding.GraphCalendarId, graphTargetId,
                expectedEtag, ct);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            return await HandleReauthAsync(
                connection, batch, operation, binding.Name, "delete", localEvent.Id, localEvent.Title);
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            try
            {
                return await HandleConflictAsync(
                    binding, conflictEvent, binding.GraphCalendarId, graphTargetId, batch, operation,
                    binding.Name, "delete", ct);
            }
            catch (OperationCanceledException oce)
            {
                await FailBatchAsync(
                    batch, operation, oce, binding.Name, "delete", conflictEvent.Id, conflictEvent.Title);
                throw;
            }
            catch (OutlookReauthenticationRequiredException)
            {
                return await HandleReauthAsync(
                    connection, batch, operation, binding.Name, "delete", conflictEvent.Id, conflictEvent.Title);
            }
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException)
        {
            await FailBatchAsync(
                batch, operation, ex, binding.Name, "delete", localEvent.Id, localEvent.Title);
            throw;
        }

        int affectedCount;
        EventEntity resultEntity;
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
            resultEntity = localEvent;
        }
        else if (target.IsOccurrenceWrite)
        {
            var cancelled = target.ExistingOccurrence;
            if (cancelled is null)
            {
                cancelled = await FindOccurrenceExceptionAsync(
                    target.Master?.Id, target.RecurrenceId, ct);
            }
            if (cancelled is null)
            {
                cancelled = new EventEntity
                {
                    Id = Guid.NewGuid(),
                    CalendarId = binding.PimCalendarId,
                    Uid = target.ValidationEntity.Uid,
                    Title = target.ValidationEntity.Title,
                    Description = target.ValidationEntity.Description,
                    Location = target.ValidationEntity.Location,
                    DtStart = target.ValidationEntity.DtStart,
                    DtEnd = target.ValidationEntity.DtEnd,
                    IsAllDay = target.ValidationEntity.IsAllDay,
                    AllDayStartDate = target.ValidationEntity.AllDayStartDate,
                    AllDayEndDateExclusive = target.ValidationEntity.AllDayEndDateExclusive,
                    TimeZoneId = target.ValidationEntity.TimeZoneId,
                    SourceTimeZoneId = target.ValidationEntity.SourceTimeZoneId,
                    OriginalStartTimeZone = target.ValidationEntity.OriginalStartTimeZone,
                    OriginalEndTimeZone = target.ValidationEntity.OriginalEndTimeZone,
                    IsException = true,
                    IsSeriesMaster = false,
                    SeriesMasterId = target.Master?.Id,
                    RecurrenceId = target.RecurrenceId,
                    RRule = null,
                    Source = "outlook",
                    Status = "CANCELLED",
                    OutlookEventId = target.ValidationEntity.OutlookEventId,
                    OutlookCalendarBindingId = binding.Id,
                    OutlookConnectionId = connection.Id,
                    OutlookEventType = target.ValidationEntity.OutlookEventType,
                    OutlookSeriesMasterId = target.ValidationEntity.OutlookSeriesMasterId,
                    OutlookEtag = target.ValidationEntity.OutlookEtag,
                    IsMuted = target.ValidationEntity.IsMuted,
                    CreatedAt = now,
                    UpdatedAt = now,
                    DtStamp = now
                };
                _db.Set<EventEntity>().Add(cancelled);
            }
            else
            {
                cancelled.DeletedAt = null;
                cancelled.DeletedByOperationId = null;
                cancelled.DeletedByOperationKind = null;
                cancelled.Status = "CANCELLED";
                cancelled.UpdatedAt = now;
                cancelled.DtStamp = now;
                if (target.Master is not null)
                    cancelled.SeriesMasterId = target.Master.Id;
                if (!string.IsNullOrEmpty(target.RecurrenceId))
                    cancelled.RecurrenceId = target.RecurrenceId;
            }
            affectedCount = 1;
            resultEntity = cancelled;
        }
        else
        {
            ApplySoftDelete(localEvent, request.ClientOperationId, now);
            affectedCount = 1;
            resultEntity = localEvent;
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
            resultEntity.Id, resultEntity.Title, 0, 0, affectedCount, 0, 0, binding.Id);
        operation.EventId = resultEntity.Id;
        operation.ResultEventId = resultEntity.Id;
        operation.ResultStatus = "deleted";
        var result = BuildSuccessResult(operation, "deleted", null);
        operation.ResultJson = SerializeResult(result);
        operation.State = "completed";
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.CompletedAt = now;
        operation.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await MarkOutcomeUnknownAsync(
                operation, batch, ex, binding.Name, "delete", localEvent.Id, localEvent.Title);
            throw;
        }

        await AuditSuccessAsync(
            userId, operation.UndoOfOperationId is null ? "outlook.event.delete" : "outlook.event.undo",
            resultEntity.Id, operation);

        return result;
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

    private sealed record WriteTarget(
        EventEntity LocalEvent,
        EventEntity ValidationEntity,
        EventEntity EtagEntity,
        EventEntity? Master,
        EventEntity? ExistingOccurrence,
        string GraphTargetId,
        string? RecurrenceId,
        CreateEventRequest? PreviousDraft,
        bool IsOccurrenceWrite,
        bool IsSyntheticOccurrence,
        bool CanUndo);

    /// <summary>
    /// Resolves what "this event" means for the wire request. A synthetic occurrence
    /// id is not a Graph event id: scope=instance on a series must resolve the concrete
    /// occurrence (or its persisted exception) and must never fall back to the master.
    /// </summary>
    private async Task<WriteTarget> ResolveWriteTargetAsync(
        Guid userId,
        OutlookCalendarBindingEntity binding,
        OutlookWriteRequest request,
        CancellationToken ct)
    {
        EventEntity? localEvent = null;
        if (request.OriginalEventId is { } original && original != Guid.Empty)
        {
            try { localEvent = await LoadEventAsync(original, userId, ct); }
            catch (DomainException) { localEvent = null; }
        }
        if (localEvent is null && request.EventId is { } eventId)
            localEvent = await LoadEventAsync(eventId, userId, ct);
        if (localEvent is null)
            throw new DomainException(02009, "事件不存在。");

        ValidateEventBinding(localEvent, binding.Id);

        if (request.Scope != "instance")
        {
            return new WriteTarget(
                localEvent,
                localEvent,
                localEvent,
                null,
                null,
                GetGraphTargetId(localEvent, request.Scope),
                localEvent.RecurrenceId,
                null,
                IsOccurrenceWrite: localEvent.IsException
                    || !string.IsNullOrEmpty(localEvent.OutlookSeriesMasterId),
                IsSyntheticOccurrence: false,
                CanUndo: false);
        }

        var master = await ResolveMasterAsync(localEvent, binding.Id, ct);

        if (localEvent.IsException && !string.IsNullOrEmpty(localEvent.OutlookEventId))
        {
            return new WriteTarget(
                localEvent,
                localEvent,
                localEvent,
                master,
                localEvent,
                localEvent.OutlookEventId!,
                NormalizeRecurrenceId(localEvent.RecurrenceId),
                OutlookEventDraftFactory.FromEntity(localEvent),
                IsOccurrenceWrite: true,
                IsSyntheticOccurrence: false,
                CanUndo: true);
        }

        // Legacy Outlook occurrence rows already carry the concrete Graph occurrence id.
        if (string.Equals(localEvent.OutlookEventType, "occurrence", StringComparison.OrdinalIgnoreCase)
            && !localEvent.IsSeriesMaster
            && string.IsNullOrEmpty(localEvent.RRule)
            && !string.IsNullOrEmpty(localEvent.OutlookEventId))
        {
            return new WriteTarget(
                localEvent,
                localEvent,
                localEvent,
                master,
                null,
                localEvent.OutlookEventId!,
                NormalizeRecurrenceId(localEvent.RecurrenceId),
                OutlookEventDraftFactory.FromEntity(localEvent),
                IsOccurrenceWrite: true,
                IsSyntheticOccurrence: false,
                CanUndo: true);
        }

        if (IsSeriesMasterLike(localEvent))
        {
            var recurrenceId = NormalizeRecurrenceId(
                request.RecurrenceId ?? request.Draft?.RecurrenceId);
            if (string.IsNullOrEmpty(recurrenceId))
                throw new DomainException(02009, "修改或删除单次重复日程必须提供 RecurrenceId。");
            if (!DateTimeOffset.TryParse(
                    recurrenceId,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var occurrenceStart))
                throw new DomainException(02009, "RecurrenceId 格式无效。");

            var exception = await FindOccurrenceExceptionAsync(localEvent.Id, recurrenceId, ct);
            if (exception is not null)
            {
                if (string.IsNullOrEmpty(exception.OutlookEventId))
                    throw new DomainException(02009, "该重复日程例外缺少 Outlook 关联，无法写回。");
                return new WriteTarget(
                    exception,
                    exception,
                    exception,
                    localEvent,
                    exception,
                    exception.OutlookEventId!,
                    recurrenceId,
                    OutlookEventDraftFactory.FromEntity(exception),
                    IsOccurrenceWrite: true,
                    IsSyntheticOccurrence: false,
                    CanUndo: true);
            }

            if (string.IsNullOrEmpty(localEvent.OutlookEventId))
                throw new DomainException(02009, "系列主事件缺少 Outlook 关联，无法写回单次。");

            var occurrence = await FindOccurrenceAsync(
                binding.ConnectionId,
                binding.GraphCalendarId,
                localEvent.OutlookEventId!,
                occurrenceStart,
                ct)
                ?? throw new DomainException(
                    02009,
                    "Outlook 中找不到该重复日程发生项，可能已被移动或删除，请刷新后重试。");

            var snapshot = new EventEntity
            {
                Id = Guid.NewGuid(),
                CalendarId = binding.PimCalendarId,
                IsException = true,
                IsSeriesMaster = false,
                SeriesMasterId = localEvent.Id,
                RecurrenceId = recurrenceId
            };
            OutlookEventMapper.ApplyGraphEvent(
                snapshot,
                occurrence,
                binding.Id,
                binding.PimCalendarId,
                binding.ConnectionId,
                Guid.NewGuid());
            snapshot.IsException = true;
            snapshot.IsSeriesMaster = false;
            snapshot.SeriesMasterId = localEvent.Id;
            snapshot.RecurrenceId = recurrenceId;
            snapshot.RRule = null;
            if (string.IsNullOrEmpty(snapshot.OutlookEventId))
                throw new DomainException(02009, "Outlook 发生项缺少事件 ID，无法写回单次。");

            return new WriteTarget(
                localEvent,
                snapshot,
                snapshot,
                localEvent,
                null,
                snapshot.OutlookEventId!,
                recurrenceId,
                OutlookEventDraftFactory.FromEntity(snapshot),
                IsOccurrenceWrite: true,
                IsSyntheticOccurrence: true,
                CanUndo: true);
        }

        if (string.IsNullOrEmpty(localEvent.OutlookEventId))
            throw new DomainException(02009, "事件缺少 Outlook 关联，无法写回。");

        return new WriteTarget(
            localEvent,
            localEvent,
            localEvent,
            null,
            null,
            localEvent.OutlookEventId!,
            NormalizeRecurrenceId(localEvent.RecurrenceId),
            OutlookEventDraftFactory.FromEntity(localEvent),
            IsOccurrenceWrite: false,
            IsSyntheticOccurrence: false,
            CanUndo: true);
    }

    private async Task<EventEntity?> ResolveMasterAsync(
        EventEntity evt, Guid bindingId, CancellationToken ct)
    {
        if (evt.SeriesMasterId is { } masterId)
        {
            return await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    e => e.Id == masterId && e.OutlookCalendarBindingId == bindingId,
                    ct);
        }
        if (!string.IsNullOrEmpty(evt.OutlookSeriesMasterId))
        {
            return await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    e => e.OutlookEventId == evt.OutlookSeriesMasterId
                        && e.OutlookCalendarBindingId == bindingId,
                    ct);
        }
        return null;
    }

    private async Task<EventEntity?> FindOccurrenceExceptionAsync(
        Guid? masterId, string? recurrenceId, CancellationToken ct)
    {
        if (masterId is null || string.IsNullOrWhiteSpace(recurrenceId))
            return null;
        var candidates = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.SeriesMasterId == masterId && e.IsException)
            .ToListAsync(ct);
        return candidates
            .Where(e => RecurrenceIdsEqual(e.RecurrenceId, recurrenceId))
            .OrderBy(e => e.DeletedAt is null ? 0 : 1)
            .ThenByDescending(e => e.UpdatedAt)
            .FirstOrDefault();
    }

    private async Task<JsonElement?> FindOccurrenceAsync(
        Guid connectionId,
        string graphCalendarId,
        string seriesMasterId,
        DateTimeOffset occurrenceStart,
        CancellationToken ct)
    {
        try
        {
            await foreach (var page in _graph.GetEventInstancesAsync(
                connectionId,
                graphCalendarId,
                seriesMasterId,
                occurrenceStart.AddDays(-1),
                occurrenceStart.AddDays(1),
                ct))
            {
                foreach (var item in page.Items)
                {
                    if (!item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;
                    if (item.TryGetProperty("seriesMasterId", out var seriesElement)
                        && seriesElement.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(seriesElement.GetString())
                        && !string.Equals(
                            seriesElement.GetString(),
                            seriesMasterId,
                            StringComparison.Ordinal))
                        continue;
                    var originalStart = ReadGraphOriginalStart(item);
                    if (originalStart is not null
                        && Math.Abs((originalStart.Value - occurrenceStart).TotalSeconds) <= 1)
                        return item;
                }
            }
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        return null;
    }

    private static DateTimeOffset? ReadGraphOriginalStart(JsonElement item)
    {
        string? raw = null;
        if (item.TryGetProperty("originalStart", out var original))
        {
            if (original.ValueKind == JsonValueKind.String)
                raw = original.GetString();
            else if (original.ValueKind == JsonValueKind.Object
                && original.TryGetProperty("dateTime", out var dateTime)
                && dateTime.ValueKind == JsonValueKind.String)
                raw = dateTime.GetString();
        }
        if (string.IsNullOrEmpty(raw)
            && item.TryGetProperty("start", out var start)
            && start.ValueKind == JsonValueKind.Object
            && start.TryGetProperty("dateTime", out var startDateTime)
            && startDateTime.ValueKind == JsonValueKind.String)
            raw = startDateTime.GetString();

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? NormalizeRecurrenceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : value.Trim();
    }

    private static bool RecurrenceIdsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;
        return DateTimeOffset.TryParse(
                left,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var leftParsed)
            && DateTimeOffset.TryParse(
                right,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var rightParsed)
            && Math.Abs((leftParsed - rightParsed).TotalSeconds) <= 1;
    }

    private static bool IsSeriesMasterLike(EventEntity evt)
        => evt.IsSeriesMaster
            || !string.IsNullOrEmpty(evt.RRule)
            || string.Equals(evt.OutlookEventType, "seriesMaster", StringComparison.OrdinalIgnoreCase);

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

    private static void ValidateUpdateAllDayDates(EventEntity existing, CreateEventRequest draft)
    {
        if (!draft.IsAllDay || (draft.AllDayStartDate.HasValue && draft.AllDayEndDateExclusive.HasValue))
            return;

        // Historical all-day rows predate independent dates. They may keep their old
        // absolute bounds for metadata-only updates, but may not silently change time.
        var isUnchangedLegacyAllDay = existing.IsAllDay
            && !existing.AllDayStartDate.HasValue
            && !existing.AllDayEndDateExclusive.HasValue
            && draft.DtStart.ToUniversalTime() == existing.DtStart.ToUniversalTime()
            && draft.DtEnd.ToUniversalTime() == existing.DtEnd.ToUniversalTime();
        if (isUnchangedLegacyAllDay)
            return;

        throw new DomainException(
            02009,
            "全天事件必须提供 allDayStartDate 与 allDayEndDateExclusive；仅历史全天事件保持原绝对边界时可省略。");
    }

    private static string GetGraphTargetId(EventEntity evt, string scope)
        => scope == "series"
            ? evt.OutlookSeriesMasterId ?? evt.OutlookEventId!
            : evt.OutlookEventId!;

    private async Task<string> ResolveWriteEtagAsync(
        OutlookCalendarBindingEntity binding, EventEntity evt, string scope,
        string? requestEtag, CancellationToken ct)
    {
        // A caller-supplied If-Match token is authoritative: using the locally cached
        // token instead would silently overwrite a remote change the caller has seen.
        if (!string.IsNullOrWhiteSpace(requestEtag))
            return requestEtag.Trim();

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

        if (!string.IsNullOrWhiteSpace(evt.OutlookEtag))
            return evt.OutlookEtag;

        var graphTargetId = GetGraphTargetId(evt, scope);
        var latest = await _graph.GetEventAsync(
            binding.ConnectionId, binding.GraphCalendarId, graphTargetId, ct)
            ?? throw new DomainException(02009, "Outlook 中已不存在该事件，请刷新后再试。");
        var latestEtag = latest.TryGetProperty("@odata.etag", out var etag)
            && etag.ValueKind == JsonValueKind.String
            ? etag.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(latestEtag))
            throw new DomainException(02009, "无法从 Outlook 读取最新版本标记，请稍后重试。");
        return latestEtag;
    }

    private async Task<OutlookWriteResult> HandleConflictAsync(
        OutlookCalendarBindingEntity binding, EventEntity localEvent,
        string graphCalendarId, string graphEventId,
        OutlookSyncBatchEntity batch, OutlookWritebackOperationEntity operationRecord,
        string calendarName, string operation, CancellationToken ct)
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
            var failedResult = new OutlookWriteResult(
                "error", null, null, null, resultCode, resultMessage,
                operationRecord.ClientOperationId, false, false, resultMessage);
            operationRecord.State = "failed";
            operationRecord.ErrorCode = resultCode;
            operationRecord.ErrorMessage = resultMessage;
            operationRecord.ResultJson = SerializeResult(failedResult);
            operationRecord.UpdatedAt = now;
            await _db.SaveChangesAsync(CancellationToken.None);
            return failedResult;
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
        var conflictResult = new OutlookWriteResult(
            "conflict", null, latestEvent, latestEtag,
            "CONFLICT", "事件在 Outlook 中已被修改。请刷新后重新编辑。",
            operationRecord.ClientOperationId, false, false,
            "事件在 Outlook 中已被修改。请刷新后重新编辑；系统不会静默覆盖远端改动。");
        operationRecord.State = "conflict";
        operationRecord.ErrorCode = "CONFLICT";
        operationRecord.ErrorMessage = conflictResult.ErrorMessage;
        operationRecord.ResultJson = SerializeResult(conflictResult);
        operationRecord.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(CancellationToken.None);

        return conflictResult;
    }

    private async Task<OutlookWriteResult> HandleReauthAsync(
        OutlookConnectionEntity connection, OutlookSyncBatchEntity batch,
        OutlookWritebackOperationEntity operationRecord,
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

        var result = new OutlookWriteResult(
            "reauth-required", null, null, null, "REAUTH_REQUIRED",
            "Microsoft 需要重新授权。", operationRecord.ClientOperationId, false, false,
            "Microsoft 授权已失效，请重新授权后使用同一 clientOperationId 重试。");
        operationRecord.State = "failed";
        operationRecord.ErrorCode = "REAUTH_REQUIRED";
        operationRecord.ErrorMessage = result.ErrorMessage;
        operationRecord.ResultJson = SerializeResult(result);
        operationRecord.UpdatedAt = now;
        await _db.SaveChangesAsync(CancellationToken.None);
        return result;
    }

    private static void ApplySoftDelete(EventEntity evt, Guid operationId, DateTimeOffset now)
    {
        evt.DeletedAt = now;
        evt.DeletedByOperationId = operationId;
        evt.DeletedByOperationKind = "outlook-writeback";
        evt.UpdatedAt = now;
    }

    private async Task AuditSuccessAsync(
        Guid userId, string action, Guid resourceId,
        OutlookWritebackOperationEntity? operation = null)
    {
        try
        {
            var metadata = operation is null
                ? null
                : new Dictionary<string, string>
                {
                    ["clientOperationId"] = operation.ClientOperationId.ToString("D"),
                    ["bindingId"] = operation.CalendarBindingId.ToString("D"),
                    ["operation"] = operation.Operation,
                    ["requiresConfirmation"] = "false"
                };
            await _audit.RecordSuccessAsync(
                userId, action, "calendar_event", resourceId, metadata, CancellationToken.None);
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

    private async Task FailBatchAsync(
        OutlookSyncBatchEntity batch, OutlookWritebackOperationEntity operationRecord, Exception ex,
        string calendarName, string operation, Guid? eventId = null, string? eventTitle = null)
    {
        var now = _timeProvider.GetUtcNow();
        batch.Status = "failed";
        batch.FailureCount = 1;
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.ConfirmationCount = 0;
        string code;
        string message;
        if (ex is GraphRequestException grex)
        {
            code = $"graph-{(int)grex.StatusCode!.Value}";
            message = "Outlook 暂时无法完成写回，请使用同一 clientOperationId 稍后重试。";
            SetBatchError(batch, code, "Graph write request failed.");
        }
        else if (ex is OperationCanceledException)
        {
            code = "cancelled";
            message = "写回请求已取消，可安全地使用同一 clientOperationId 重试。";
            SetBatchError(batch, code, "Writeback request was cancelled.");
        }
        else
        {
            code = "unknown";
            message = "Outlook 写回失败，请使用同一 clientOperationId 重试。";
            SetBatchError(batch, code, "An unexpected error occurred.");
        }

        PopulateBatchHistory(batch, calendarName, operation, "failed", eventId, eventTitle,
            0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "failed", timestamp = now }
        });
        var result = new OutlookWriteResult(
            "error", null, null, null, code.ToUpperInvariant(), message,
            operationRecord.ClientOperationId, false, false, message);
        operationRecord.State = "failed";
        operationRecord.ErrorCode = code;
        operationRecord.ErrorMessage = message;
        operationRecord.ResultJson = SerializeResult(result);
        operationRecord.UpdatedAt = now;
        operationRecord.CompletedAt = null;
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception saveEx)
        {
            _logger.LogWarning(saveEx, "Failed to persist failure history for batch {BatchId}", batch.Id);
        }
    }

    private async Task MarkOutcomeUnknownAsync(
        OutlookWritebackOperationEntity operationRecord,
        OutlookSyncBatchEntity batch,
        Exception ex,
        string calendarName,
        string operation,
        Guid? eventId = null,
        string? eventTitle = null)
    {
        var now = _timeProvider.GetUtcNow();
        batch.Status = "failed";
        batch.FailureCount = 1;
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.ConfirmationCount = 0;
        SetBatchError(batch, "remote-outcome-unknown", "Graph may have applied the write.");
        PopulateBatchHistory(batch, calendarName, operation, "failed", eventId, eventTitle,
            0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = "remote-write", status = "unknown", timestamp = now }
        });

        var result = new OutlookWriteResult(
            "conflict",
            null,
            null,
            null,
            "OPERATION_OUTCOME_UNKNOWN",
            "Outlook 可能已经写入，但本地保存失败。请先在 Outlook 核对，勿直接重复提交。",
            operationRecord.ClientOperationId,
            false,
            false,
            "Outlook 可能已经写入，但本地保存失败。请先在 Outlook 核对，勿直接重复提交。");
        operationRecord.State = "unknown";
        operationRecord.ErrorCode = "OPERATION_OUTCOME_UNKNOWN";
        operationRecord.ErrorMessage = result.ErrorMessage;
        operationRecord.ResultJson = SerializeResult(result);
        operationRecord.UpdatedAt = now;

        try
        {
            _logger.LogWarning(
                ex,
                "Outlook writeback outcome is unknown for operation {OperationRecordId}",
                operationRecord.Id);
            _db.ChangeTracker.Clear();
            var persistedOperation = await _db.Set<OutlookWritebackOperationEntity>()
                .FirstOrDefaultAsync(o => o.Id == operationRecord.Id, CancellationToken.None);
            var persistedBatch = await _db.Set<OutlookSyncBatchEntity>()
                .FirstOrDefaultAsync(b => b.Id == batch.Id, CancellationToken.None);
            if (persistedOperation is not null)
            {
                persistedOperation.State = operationRecord.State;
                persistedOperation.ErrorCode = operationRecord.ErrorCode;
                persistedOperation.ErrorMessage = operationRecord.ErrorMessage;
                persistedOperation.ResultJson = operationRecord.ResultJson;
                persistedOperation.UpdatedAt = now;
            }
            if (persistedBatch is not null)
            {
                persistedBatch.Status = batch.Status;
                persistedBatch.FailureCount = batch.FailureCount;
                persistedBatch.FinishedAt = batch.FinishedAt;
                persistedBatch.UpdatedAt = batch.UpdatedAt;
                persistedBatch.ConfirmationCount = 0;
                persistedBatch.ErrorsJson = batch.ErrorsJson;
                persistedBatch.ErrorSummary = batch.ErrorSummary;
                persistedBatch.PerCalendarJson = batch.PerCalendarJson;
                persistedBatch.StepsJson = batch.StepsJson;
            }
            if (persistedOperation is not null || persistedBatch is not null)
                await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception saveEx)
        {
            _logger.LogWarning(
                saveEx,
                "Failed to persist unknown outcome for writeback operation {OperationRecordId}",
                operationRecord.Id);
        }
    }

    private async Task<OutlookWriteResult?> SaveStartAsync(
        OutlookSyncBatchEntity batch,
        OutlookWritebackOperationEntity operation,
        string payloadHash,
        CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrent = await _db.Set<OutlookWritebackOperationEntity>()
                .FirstOrDefaultAsync(
                    o => o.UserId == operation.UserId
                        && o.ClientOperationId == operation.ClientOperationId,
                    CancellationToken.None);
            if (concurrent is null)
                throw;
            return await ReplayOnlyAsync(concurrent, payloadHash, CancellationToken.None);
        }
    }

    private async Task<OutlookWriteResult?> TryReplayOrResumeAsync(
        OutlookWritebackOperationEntity operation,
        string payloadHash,
        CancellationToken ct)
    {
        if (!string.Equals(operation.PayloadHash, payloadHash, StringComparison.Ordinal))
            return IdempotencyConflict(operation);

        switch (operation.State)
        {
            case "completed":
            case "conflict":
                return ReplayCached(operation);
            case "processing":
                return await ReplayProcessingAsync(operation, ct);
            case "unknown":
                return OutcomeUnknown(operation);
            case "failed":
                operation.State = "processing";
                operation.AttemptCount++;
                operation.ErrorCode = null;
                operation.ErrorMessage = null;
                operation.ResultJson = null;
                operation.CompletedAt = null;
                operation.UpdatedAt = _timeProvider.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return null;
            default:
                return OperationInProgress(operation);
        }
    }

    private async Task<OutlookWriteResult> ReplayOnlyAsync(
        OutlookWritebackOperationEntity operation,
        string payloadHash,
        CancellationToken ct)
    {
        var replay = await TryReplayOrResumeAsync(operation, payloadHash, ct);
        return replay ?? OperationInProgress(operation);
    }

    private static OutlookWriteResult IdempotencyConflict(OutlookWritebackOperationEntity operation)
        => new(
            "conflict",
            ErrorCode: "IDEMPOTENCY_CONFLICT",
            ErrorMessage: "同一 clientOperationId 已用于不同的写回内容。请使用新的 clientOperationId。",
            ClientOperationId: operation.ClientOperationId,
            UserMessage: "同一 clientOperationId 已用于不同的写回内容。请使用新的 clientOperationId。");

    private static OutlookWriteResult OperationInProgress(OutlookWritebackOperationEntity operation)
        => new(
            "conflict",
            ErrorCode: "OPERATION_IN_PROGRESS",
            ErrorMessage: "同一 clientOperationId 的写回仍在处理中，请勿并发重试。",
            ClientOperationId: operation.ClientOperationId,
            UserMessage: "同一 clientOperationId 的写回仍在处理中，请勿并发重试。");

    private static OutlookWriteResult OutcomeUnknown(OutlookWritebackOperationEntity operation)
        => new(
            "conflict",
            ErrorCode: "OPERATION_OUTCOME_UNKNOWN",
            ErrorMessage: "该操作的上一次结果未知。请先在 Outlook 核对，勿直接重复提交。",
            ClientOperationId: operation.ClientOperationId,
            UserMessage: "该操作的上一次结果未知。请先在 Outlook 核对，勿直接重复提交。");

    private static OutlookWriteResult ReplayCached(OutlookWritebackOperationEntity operation)
    {
        var cached = DeserializeResult(operation.ResultJson);
        if (cached is null)
        {
            return operation.State == "conflict"
                ? new OutlookWriteResult(
                    "conflict", null, null, null,
                    operation.ErrorCode ?? "CONFLICT",
                    operation.ErrorMessage ?? "冲突。",
                    operation.ClientOperationId)
                : new OutlookWriteResult(
                    operation.ResultStatus ?? "completed", null, null, null, null, null,
                    operation.ClientOperationId);
        }

        var undoAvailable = cached.Status is "created" or "updated" or "deleted"
            && operation.UndoState == "available";
        var replayEvent = cached.Event is null
            ? null
            : cached.Event with
            {
                ClientOperationId = operation.ClientOperationId,
                RequiresConfirmation = false,
                UndoAvailable = undoAvailable,
                Replayed = true
            };
        return cached with
        {
            Event = replayEvent,
            ClientOperationId = operation.ClientOperationId,
            RequiresConfirmation = false,
            UndoAvailable = undoAvailable,
            Replayed = true
        };
    }

    private OutlookWriteResult BuildSuccessResult(
        OutlookWritebackOperationEntity operation,
        string status,
        EventEntity? entity)
    {
        var undoAvailable = operation.UndoState == "available";
        var message = status switch
        {
            "created" => "已在 Outlook 中创建。",
            "updated" => "已写回 Outlook。",
            "deleted" => $"已从 Outlook 删除；clientOperationId={operation.ClientOperationId:D}。",
            _ => "Outlook 写回完成。"
        };
        var eventResponse = entity is null
            ? null
            : MapEvent(entity) with
            {
                ClientOperationId = operation.ClientOperationId,
                RequiresConfirmation = false,
                UndoAvailable = undoAvailable,
                UserMessage = message
            };
        return new OutlookWriteResult(
            status,
            eventResponse,
            null,
            null,
            null,
            null,
            operation.ClientOperationId,
            false,
            undoAvailable,
            message);
    }

    private static string SerializeResult(OutlookWriteResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    private static OutlookWriteResult? DeserializeResult(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<OutlookWriteResult>(json, JsonOptions);

    private static string SerializeDraft(CreateEventRequest draft)
        => JsonSerializer.Serialize(draft, JsonOptions);

    private static CreateEventRequest DeserializeDraft(string json)
        => JsonSerializer.Deserialize<CreateEventRequest>(json, JsonOptions)
            ?? throw new DomainException(02009, "撤销所需的日程快照损坏。");

    private static string ComputePayloadHash(
        OutlookWriteRequest request,
        Guid? undoOfOperationId)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            operation = request.Operation,
            calendarBindingId = request.CalendarBindingId,
            eventId = request.EventId,
            draft = request.Draft,
            scope = request.Scope,
            expectedEtag = request.ExpectedEtag,
            originalEventId = request.OriginalEventId,
            recurrenceId = request.RecurrenceId,
            undoOfOperationId
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
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
