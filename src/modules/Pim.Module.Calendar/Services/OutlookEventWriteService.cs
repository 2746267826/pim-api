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
        Guid userId, OutlookWriteRequest request, CancellationToken ct,
        string? requestHash = null)
        => await ExecuteCoreAsync(userId, request, null, requestHash, ct);

    public async Task<OutlookWriteResult> ExecuteUndoAsync(
        Guid userId, OutlookWriteRequest request, Guid undoOfOperationId, CancellationToken ct)
        => await ExecuteCoreAsync(userId, request, undoOfOperationId, null, ct);

    private async Task<OutlookWriteResult> ExecuteCoreAsync(
        Guid userId, OutlookWriteRequest request, Guid? undoOfOperationId,
        string? requestHash, CancellationToken ct)
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
            var replay = await TryReplayOrResumeAsync(
                existing, payloadHash, undoOfOperationId, ct);
            if (replay is not null)
                return replay;
            if (existing.State == "failed"
                && existing.ErrorCode == "LOCAL_STATE_PENDING"
                && request.Operation == "delete")
            {
                return await ResumeDeleteLocalAsync(
                    userId, connection, binding, request, existing);
            }
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
            RequestHash = requestHash,
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
        operation.RequestHash ??= requestHash;

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
                        && o.ClientOperationId == recoveredId,
                    ct);
            if (recovered is { State: "completed" })
            {
                original.UndoState = "used";
                original.UpdatedAt = _timeProvider.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return ReplayCached(recovered);
            }

            // The child row is the real lease holder once it exists: its UpdatedAt is
            // stamped when the undo starts. Only fall back to the parent's timestamp
            // when the child row was never created.
            if (recovered is { State: "processing" }
                && !IsProcessingLeaseExpired(recovered.UpdatedAt))
            {
                return OperationInProgress(original);
            }
            if (recovered is null && !IsProcessingLeaseExpired(original.UpdatedAt))
                return OperationInProgress(original);

            // The process holding "in-progress" died. Reclaim the deterministic undo
            // operation once its lease expires instead of returning OPERATION_IN_PROGRESS
            // forever. A stale Graph attempt is retryable with the same operation id:
            // DELETE is idempotent, PATCH still uses If-Match, and CREATE reuses the
            // original transaction id.
            if (recovered is { State: "processing" })
            {
                recovered.State = "failed";
                recovered.ErrorCode = "UNDO_LEASE_EXPIRED";
                recovered.ErrorMessage =
                    "上一次撤销进程已中断，正在使用同一撤销 operation id 重新尝试。";
                recovered.ResultJson = null;
                recovered.CompletedAt = null;
                recovered.UpdatedAt = _timeProvider.GetUtcNow();
            }

            original.UndoState = "available";
            original.UpdatedAt = _timeProvider.GetUtcNow();
            await _db.SaveChangesAsync(ct);

            if (recovered is { State: "unknown" })
                return OutcomeUnknown(recovered);
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
                    var createdEvent = await _db.Set<EventEntity>()
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            e => e.Id == eventId && e.Calendar.UserId == userId,
                            ct)
                        ?? throw new DomainException(02009, "原创建操作的日程已不存在，无法撤销。");
                    // Graph returns type=seriesMaster for a create that carried an
                    // RRule even though the request scope is instance. Undo must
                    // delete the actual series instead of claiming an instance
                    // delete that would fail or write the master by mistake.
                    var undoScope = OutlookWriteTargetIdentity.IsSeriesMasterLike(createdEvent)
                        ? "series"
                        : original.Scope;
                    undoRequest = new OutlookWriteRequest(
                        "delete",
                        original.CalendarBindingId,
                        eventId,
                        null,
                        undoScope,
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
                    // A conflicted undo proved the locally cached ETag is stale.
                    // Retry must send the ETag returned by the conflict read, not the
                    // pre-conflict token that Graph already rejected.
                    var expectedEtag =
                        GetConflictLatestEtag(priorUndo) ?? eventEntity.OutlookEtag;
                    undoRequest = new OutlookWriteRequest(
                        "update",
                        original.CalendarBindingId,
                        eventId,
                        DeserializeDraft(original.PreviousDraftJson),
                        original.Scope,
                        undoClientOperationId,
                        ExpectedEtag: expectedEtag);
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
        string? requestHash,
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
        if (string.IsNullOrWhiteSpace(operation.RequestHash)
            || string.IsNullOrWhiteSpace(requestHash)
            || !string.Equals(operation.RequestHash, requestHash, StringComparison.Ordinal))
            return IdempotencyConflict(operation);

        if (operation.State == "failed"
            && operation.ErrorCode == "LOCAL_STATE_PENDING"
            && string.Equals(operation.Operation, "delete", StringComparison.OrdinalIgnoreCase))
        {
            return await ResumePendingDeleteWithoutEventAsync(userId, operation, ct);
        }

        return operation.State switch
        {
            "completed" or "conflict" => ReplayCached(operation),
            "processing" => await ReplayProcessingAsync(operation, ct),
            "unknown" => OutcomeUnknown(operation),
            _ => null
        };
    }

    private async Task<OutlookWriteResult> ResumePendingDeleteWithoutEventAsync(
        Guid userId,
        OutlookWritebackOperationEntity operation,
        CancellationToken ct)
    {
        var binding = await _db.Set<OutlookCalendarBindingEntity>()
            .FirstOrDefaultAsync(b => b.Id == operation.CalendarBindingId, ct);
        var connection = binding is null
            ? null
            : await _db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(
                    c => c.Id == binding.ConnectionId && c.UserId == userId,
                    ct);
        if (binding is null || connection is null)
            return LocalStatePendingResult(operation);

        var request = new OutlookWriteRequest(
            "delete",
            binding.Id,
            operation.EventId,
            null,
            operation.Scope,
            operation.ClientOperationId);
        return await ResumeDeleteLocalAsync(
            userId, connection, binding, request, operation);
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
        if (!IsProcessingLeaseExpired(operation.UpdatedAt))
            return OperationInProgress(operation);

        operation.State = "unknown";
        operation.ErrorCode = "OPERATION_OUTCOME_UNKNOWN";
        operation.ErrorMessage = "该操作的处理进程已中断，Outlook 侧结果未知。请先在 Outlook 核对，勿直接重复提交。";
        operation.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return OutcomeUnknown(operation);
    }

    private bool IsProcessingLeaseExpired(DateTimeOffset updatedAt)
        => _timeProvider.GetUtcNow() - updatedAt >= ProcessingLease;

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
        catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
        {
            await FailBatchAsync(
                batch, operation, oce, binding.Name, "create",
                callerCancelled: true);
            throw;
        }
        catch (Exception ex) when (ex is not OutlookReauthenticationRequiredException)
        {
            return await FailBatchAsync(batch, operation, ex, binding.Name, "create");
        }

        OutlookWriteResult result;
        EventEntity localEvent;
        var now = _timeProvider.GetUtcNow();
        try
        {
            var graphId = graphResult.GetProperty("id").GetString()!;
            var existingEvent = await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .Where(e => e.OutlookConnectionId == connection.Id && e.OutlookEventId == graphId)
                .FirstOrDefaultAsync(CancellationToken.None);

            localEvent = existingEvent ?? new EventEntity();
            var generation = Guid.NewGuid();
            try
            {
                OutlookEventMapper.ApplyGraphEvent(
                    localEvent, graphResult, binding.Id, binding.PimCalendarId,
                    connection.Id, generation);
            }
            catch
            {
                if (existingEvent is not null)
                    _db.Entry(existingEvent).Reload();
                throw;
            }

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
            result = BuildSuccessResult(operation, "created", localEvent);
            operation.ResultJson = SerializeResult(result);
            operation.State = "completed";
            operation.ErrorCode = null;
            operation.ErrorMessage = null;
            operation.CompletedAt = now;
            operation.UpdatedAt = now;
            SetBatchSummary(batch, binding.Name, "create", "completed", localEvent.Id, localEvent.Title,
                1, 0, 0, 0, binding.Id, steps);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            return await MarkOutcomeUnknownAsync(operation, batch, ex, binding.Name, "create");
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

        WriteTarget target;
        try
        {
            target = await ResolveWriteTargetAsync(userId, binding, request, ct);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            var preflightBatch = NewBatch(
                userId, connection.Id, binding.Id, binding.Name,
                "update", request.EventId);
            _db.Set<OutlookSyncBatchEntity>().Add(preflightBatch);
            return await HandleReauthAsync(
                connection, preflightBatch, operation, binding.Name, "update", request.EventId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsProviderFailure(ex, ct) || IsGraphPayloadFailure(ex))
        {
            var preflightBatch = NewBatch(
                userId, connection.Id, binding.Id, binding.Name,
                "update", request.EventId);
            _db.Set<OutlookSyncBatchEntity>().Add(preflightBatch);
            return await FailBatchAsync(
                preflightBatch, operation, ex, binding.Name, "update",
                request.EventId, null,
                noChangesAppliedOverride: true,
                remoteWriteAttempted: false);
        }
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
        var conflictEvent = target.IsSyntheticOccurrence
            ? new EventEntity { Id = request.EventId ?? localEvent.Id }
            : localEvent;

        JsonElement graphResult;
        var remoteWriteAttempted = false;
        try
        {
            var expectedEtag = await ResolveWriteEtagAsync(
                binding, target.EtagEntity, request.Scope, request.ExpectedEtag, ct);
            remoteWriteAttempted = true;
            graphResult = await _graph.UpdateEventAsync(
                connection.Id, binding.GraphCalendarId, graphTargetId,
                expectedEtag, payload, ct);
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
            catch (OutlookReauthenticationRequiredException)
            {
                return await HandleReauthAsync(
                    connection, batch, operation, binding.Name, "update", conflictEvent.Id, conflictEvent.Title);
            }
            catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
            {
                await FailBatchAsync(
                    batch, operation, oce, binding.Name, "update",
                    conflictEvent.Id, conflictEvent.Title,
                    callerCancelled: true,
                    noChangesAppliedOverride: true,
                    remoteWriteAttempted: remoteWriteAttempted);
                throw;
            }
            catch (Exception conflictEx)
            {
                return await FailBatchAsync(
                    batch, operation, conflictEx, binding.Name, "update",
                    conflictEvent.Id, conflictEvent.Title,
                    noChangesAppliedOverride: true);
            }
        }
        catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
        {
            await FailBatchAsync(
                batch, operation, oce, binding.Name, "update",
                localEvent.Id, localEvent.Title,
                callerCancelled: true,
                noChangesAppliedOverride: remoteWriteAttempted ? null : true,
                remoteWriteAttempted: remoteWriteAttempted);
            throw;
        }
        catch (Exception ex)
        {
            return await FailBatchAsync(
                batch, operation, ex, binding.Name, "update", localEvent.Id, localEvent.Title,
                remoteWriteAttempted: remoteWriteAttempted);
        }

        try
        {
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
            await _db.SaveChangesAsync(CancellationToken.None);

            await AuditSuccessAsync(
                userId, operation.UndoOfOperationId is null ? "outlook.event.update" : "outlook.event.undo",
                targetEntity.Id, operation);

            return result;
        }
        catch (Exception ex)
        {
            return await MarkOutcomeUnknownAsync(
                operation, batch, ex, binding.Name, "update", localEvent.Id, localEvent.Title);
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

        WriteTarget target;
        try
        {
            target = await ResolveWriteTargetAsync(userId, binding, request, ct);
        }
        catch (OutlookReauthenticationRequiredException)
        {
            var preflightBatch = NewBatch(
                userId, connection.Id, binding.Id, binding.Name,
                "delete", request.EventId);
            _db.Set<OutlookSyncBatchEntity>().Add(preflightBatch);
            return await HandleReauthAsync(
                connection, preflightBatch, operation, binding.Name, "delete", request.EventId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsProviderFailure(ex, ct) || IsGraphPayloadFailure(ex))
        {
            var preflightBatch = NewBatch(
                userId, connection.Id, binding.Id, binding.Name,
                "delete", request.EventId);
            _db.Set<OutlookSyncBatchEntity>().Add(preflightBatch);
            return await FailBatchAsync(
                preflightBatch, operation, ex, binding.Name, "delete",
                request.EventId, null,
                noChangesAppliedOverride: true,
                remoteWriteAttempted: false);
        }
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
        operation.ResultJson = SerializePendingDeleteFinalization(
            BuildPendingDeleteFinalization(batch.Id, target));
        var startReplay = await SaveStartAsync(batch, operation, operation.PayloadHash, ct);
        if (startReplay is not null)
            return startReplay;

        var graphTargetId = target.GraphTargetId;
        var now = _timeProvider.GetUtcNow();
        var conflictEvent = target.IsSyntheticOccurrence
            ? new EventEntity { Id = request.EventId ?? localEvent.Id }
            : localEvent;

        var remoteWriteAttempted = false;
        try
        {
            var expectedEtag = await ResolveWriteEtagAsync(
                binding, target.EtagEntity, request.Scope, request.ExpectedEtag, ct);
            remoteWriteAttempted = true;
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
            catch (OutlookReauthenticationRequiredException)
            {
                return await HandleReauthAsync(
                    connection, batch, operation, binding.Name, "delete", conflictEvent.Id, conflictEvent.Title);
            }
            catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
            {
                await FailBatchAsync(
                    batch, operation, oce, binding.Name, "delete",
                    conflictEvent.Id, conflictEvent.Title,
                    callerCancelled: true,
                    noChangesAppliedOverride: true,
                    remoteWriteAttempted: remoteWriteAttempted);
                throw;
            }
            catch (Exception conflictEx)
            {
                return await FailBatchAsync(
                    batch, operation, conflictEx, binding.Name, "delete",
                    conflictEvent.Id, conflictEvent.Title,
                    noChangesAppliedOverride: true);
            }
        }
        catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
        {
            await FailBatchAsync(
                batch, operation, oce, binding.Name, "delete",
                localEvent.Id, localEvent.Title,
                callerCancelled: true,
                noChangesAppliedOverride: remoteWriteAttempted ? null : true,
                remoteWriteAttempted: remoteWriteAttempted);
            throw;
        }
        catch (Exception ex)
        {
            return await FailBatchAsync(
                batch, operation, ex, binding.Name, "delete", localEvent.Id, localEvent.Title,
                remoteWriteAttempted: remoteWriteAttempted);
        }

        return await TryFinalizeDeleteLocalAsync(
            userId, connection, binding, request, target, batch, operation, now);
    }

    /// <summary>
    /// A Graph DELETE already succeeded before the local commit failed. The durable
    /// pending-finalization payload lets the same clientOperationId finish the local
    /// soft-delete without issuing a second Graph DELETE.
    /// </summary>
    private async Task<OutlookWriteResult> ResumeDeleteLocalAsync(
        Guid userId,
        OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding,
        OutlookWriteRequest request,
        OutlookWritebackOperationEntity operation)
    {
        var pending = DeserializePendingDeleteFinalization(operation.ResultJson);
        if (pending is null)
            return LocalStatePendingResult(operation);
        var pendingJson = operation.ResultJson!;

        OutlookSyncBatchEntity? batch = null;
        WriteTarget? target = null;
        try
        {
            batch = await _db.Set<OutlookSyncBatchEntity>()
                .FirstOrDefaultAsync(b => b.Id == pending.BatchId, CancellationToken.None);
            if (batch is null)
            {
                batch = NewBatch(
                    userId, connection.Id, binding.Id, binding.Name, "delete",
                    operation.EventId, null);
                _db.Set<OutlookSyncBatchEntity>().Add(batch);
            }

            target = await RehydrateDeleteTargetAsync(
                userId, binding, operation, pending);
        }
        catch (Exception ex)
        {
            batch ??= NewBatch(
                userId, connection.Id, binding.Id, binding.Name, "delete",
                operation.EventId, null);
            if (_db.Entry(batch).State == EntityState.Detached)
                _db.Set<OutlookSyncBatchEntity>().Add(batch);
            return await MarkLocalStatePendingAsync(
                operation, batch, ex, binding.Name, "delete",
                pending.LocalEventId, null);
        }

        var now = _timeProvider.GetUtcNow();
        operation.State = "processing";
        operation.AttemptCount++;
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.CompletedAt = null;
        operation.UpdatedAt = now;

        batch.Status = "running";
        batch.FailureCount = 0;
        batch.FinishedAt = null;
        batch.UpdatedAt = now;
        batch.ErrorsJson = "[]";
        batch.ErrorSummary = null;

        if (target is null)
        {
            try
            {
                return await CompleteDeleteWithoutLocalTargetAsync(
                    binding.Name, batch, operation, now);
            }
            catch (Exception ex)
            {
                operation.ResultJson = pendingJson;
                return await MarkLocalStatePendingAsync(
                    operation, batch, ex, binding.Name, "delete",
                    pending.LocalEventId, null);
            }
        }

        return await TryFinalizeDeleteLocalAsync(
            userId, connection, binding, request, target, batch, operation, now);
    }

    private async Task<WriteTarget?> RehydrateDeleteTargetAsync(
        Guid userId,
        OutlookCalendarBindingEntity binding,
        OutlookWritebackOperationEntity operation,
        PendingDeleteFinalization pending)
    {
        var localEvent = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.Id == pending.LocalEventId && e.Calendar.UserId == userId,
                CancellationToken.None);
        if (localEvent is null)
            return null;

        var master = pending.MasterId is { } masterId
            ? await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    e => e.Id == masterId && e.OutlookCalendarBindingId == binding.Id,
                    CancellationToken.None)
            : null;
        var existingOccurrence = pending.ExistingOccurrenceId is { } occurrenceId
            ? await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    e => e.Id == occurrenceId && e.OutlookCalendarBindingId == binding.Id,
                    CancellationToken.None)
            : null;

        var validation = pending.IsOccurrenceWrite
            ? RehydrateOccurrenceValidationEntity(
                operation, pending, localEvent, binding, master)
            : localEvent;
        return new WriteTarget(
            localEvent,
            validation,
            validation,
            master,
            existingOccurrence,
            pending.GraphTargetId,
            pending.RecurrenceId,
            null,
            pending.IsOccurrenceWrite,
            IsSyntheticOccurrence: false,
            CanUndo: false);
    }

    private static EventEntity RehydrateOccurrenceValidationEntity(
        OutlookWritebackOperationEntity operation,
        PendingDeleteFinalization pending,
        EventEntity localEvent,
        OutlookCalendarBindingEntity binding,
        EventEntity? master)
    {
        CreateEventRequest? draft = null;
        if (!string.IsNullOrWhiteSpace(operation.PreviousDraftJson))
            draft = DeserializeDraft(operation.PreviousDraftJson);

        return new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = draft?.CalendarId ?? localEvent.CalendarId,
            Uid = draft?.Uid ?? localEvent.Uid,
            Title = draft?.Title ?? localEvent.Title,
            Description = draft?.Description ?? localEvent.Description,
            DescriptionFormat = draft?.DescriptionFormat,
            Location = draft?.Location ?? localEvent.Location,
            DtStart = draft?.DtStart ?? localEvent.DtStart,
            DtEnd = draft?.DtEnd ?? localEvent.DtEnd,
            IsAllDay = draft?.IsAllDay ?? localEvent.IsAllDay,
            AllDayStartDate = draft?.AllDayStartDate ?? localEvent.AllDayStartDate,
            AllDayEndDateExclusive =
                draft?.AllDayEndDateExclusive ?? localEvent.AllDayEndDateExclusive,
            TimeZoneId = draft?.TimeZoneId ?? localEvent.TimeZoneId,
            SourceTimeZoneId =
                pending.ValidationSourceTimeZoneId ?? localEvent.SourceTimeZoneId,
            OriginalStartTimeZone =
                pending.ValidationOriginalStartTimeZone ?? localEvent.OriginalStartTimeZone,
            OriginalEndTimeZone =
                pending.ValidationOriginalEndTimeZone ?? localEvent.OriginalEndTimeZone,
            IsException = true,
            IsSeriesMaster = false,
            SeriesMasterId = master?.Id,
            RecurrenceId = pending.RecurrenceId,
            RRule = null,
            Source = "outlook",
            Status = "CONFIRMED",
            OutlookEventId = pending.ValidationOutlookEventId ?? pending.GraphTargetId,
            OutlookCalendarBindingId = binding.Id,
            OutlookConnectionId = binding.ConnectionId,
            OutlookEventType = pending.ValidationOutlookEventType,
            OutlookSeriesMasterId = pending.ValidationOutlookSeriesMasterId,
            OutlookEtag = pending.ValidationOutlookEtag,
            IsMuted = pending.ValidationIsMuted
        };
    }

    private async Task<OutlookWriteResult> TryFinalizeDeleteLocalAsync(
        Guid userId,
        OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding,
        OutlookWriteRequest request,
        WriteTarget target,
        OutlookSyncBatchEntity batch,
        OutlookWritebackOperationEntity operation,
        DateTimeOffset now)
    {
        var pendingJson = SerializePendingDeleteFinalization(
            BuildPendingDeleteFinalization(batch.Id, target));
        operation.ResultJson = pendingJson;
        try
        {
            return await FinalizeDeleteLocalAsync(
                userId, connection, binding, request, target, batch, operation, now);
        }
        catch (Exception ex)
        {
            operation.ResultJson = pendingJson;
            return await MarkLocalStatePendingAsync(
                operation, batch, ex, binding.Name, "delete",
                target.LocalEvent.Id, target.LocalEvent.Title);
        }
    }

    private async Task<OutlookWriteResult> FinalizeDeleteLocalAsync(
        Guid userId,
        OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding,
        OutlookWriteRequest request,
        WriteTarget target,
        OutlookSyncBatchEntity batch,
        OutlookWritebackOperationEntity operation,
        DateTimeOffset now)
    {
        var localEvent = target.LocalEvent;
        var graphTargetId = target.GraphTargetId;
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
                    target.Master?.Id, target.RecurrenceId, CancellationToken.None);
            }
            if (cancelled is null)
            {
                // A legacy occurrence row can already own the concrete Graph occurrence id
                // without being flagged as an exception. Inserting another row with the same
                // (binding, outlook_event_id) would violate the unique index after Graph has
                // already accepted the DELETE, so reuse that row and converge it to a
                // cancelled exception instead.
                cancelled = await FindOccurrenceByGraphIdAsync(
                    binding.Id, target.GraphTargetId, CancellationToken.None);
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
                cancelled.IsException = true;
                cancelled.IsSeriesMaster = false;
                cancelled.Status = "CANCELLED";
                cancelled.UpdatedAt = now;
                cancelled.DtStamp = now;
                if (target.Master is not null)
                    cancelled.SeriesMasterId = target.Master.Id;
                if (!string.IsNullOrEmpty(target.RecurrenceId))
                    cancelled.RecurrenceId = target.RecurrenceId;
                cancelled.RRule = null;
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
        await _db.SaveChangesAsync(CancellationToken.None);

        await AuditSuccessAsync(
            userId, operation.UndoOfOperationId is null ? "outlook.event.delete" : "outlook.event.undo",
            resultEntity.Id, operation);

        return result;
    }

    private async Task<OutlookWriteResult> CompleteDeleteWithoutLocalTargetAsync(
        string calendarName,
        OutlookSyncBatchEntity batch,
        OutlookWritebackOperationEntity operation,
        DateTimeOffset now)
    {
        batch.Status = "completed";
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.UpdatedCount = 0;
        batch.ConfirmationCount = 0;
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = "graph-delete", status = "success", timestamp = now },
            new { step = "local-target-missing", status = "success", timestamp = now }
        });
        PopulateBatchHistory(batch, calendarName, "delete", "completed",
            operation.EventId, null, 0, 0, 0, 0, 0, operation.CalendarBindingId);
        operation.ResultStatus = "deleted";
        var result = BuildSuccessResult(operation, "deleted", null);
        operation.ResultJson = SerializeResult(result);
        operation.State = "completed";
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.CompletedAt = now;
        operation.UpdatedAt = now;
        await _db.SaveChangesAsync(CancellationToken.None);
        return result;
    }

    private sealed record PendingDeleteFinalization(
        Guid BatchId,
        string GraphTargetId,
        Guid LocalEventId,
        Guid? MasterId,
        Guid? ExistingOccurrenceId,
        string? RecurrenceId,
        bool IsOccurrenceWrite,
        string? ValidationOutlookEventId,
        string? ValidationOutlookEventType,
        string? ValidationOutlookSeriesMasterId,
        string? ValidationOutlookEtag,
        string? ValidationSourceTimeZoneId,
        string? ValidationOriginalStartTimeZone,
        string? ValidationOriginalEndTimeZone,
        bool? ValidationIsMuted);

    private static PendingDeleteFinalization BuildPendingDeleteFinalization(
        Guid batchId, WriteTarget target)
        => new(
            batchId,
            target.GraphTargetId,
            target.LocalEvent.Id,
            target.Master?.Id,
            target.ExistingOccurrence?.Id,
            target.RecurrenceId,
            target.IsOccurrenceWrite,
            target.ValidationEntity.OutlookEventId,
            target.ValidationEntity.OutlookEventType,
            target.ValidationEntity.OutlookSeriesMasterId,
            target.ValidationEntity.OutlookEtag,
            target.ValidationEntity.SourceTimeZoneId,
            target.ValidationEntity.OriginalStartTimeZone,
            target.ValidationEntity.OriginalEndTimeZone,
            target.ValidationEntity.IsMuted);

    private static string SerializePendingDeleteFinalization(
        PendingDeleteFinalization pending)
        => JsonSerializer.Serialize(pending, JsonOptions);

    private static PendingDeleteFinalization? DeserializePendingDeleteFinalization(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PendingDeleteFinalization>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OutlookWriteResult LocalStatePendingResult(
        OutlookWritebackOperationEntity operation)
    {
        const string message =
            "Outlook 已删除，但本地待收敛目标缺失。请使用同一 clientOperationId 重试；系统不会重复删除远端。";
        return new OutlookWriteResult(
            "error",
            null,
            null,
            null,
            "LOCAL_STATE_PENDING",
            message,
            operation.ClientOperationId,
            false,
            false,
            message);
    }

    private async Task<EventEntity?> FindEventAsync(Guid eventId, Guid userId, CancellationToken ct)
    {
        var evt = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == userId, ct);
        if (evt is null)
            return null;
        if (evt.Calendar.DeletedAt is not null)
            throw new DomainException(02009, "事件所属日历已被删除。");
        return evt;
    }

    private async Task<EventEntity> LoadEventAsync(Guid eventId, Guid userId, CancellationToken ct)
        => await FindEventAsync(eventId, userId, ct)
            ?? throw new DomainException(02009, "事件不存在。");

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
        if (request.EventId is not { } requestedEventId)
            throw new DomainException(02009, "事件不存在。");

        // Expanded occurrence ids are derived and intentionally absent from the
        // events table. A null result is only the not-found case; a deleted
        // calendar must still fail closed instead of being treated as synthetic.
        var localEvent = await FindEventAsync(requestedEventId, userId, ct);

        EventEntity? originalEvent = null;
        if (request.OriginalEventId is { } originalId && originalId != Guid.Empty)
        {
            originalEvent = await FindEventAsync(originalId, userId, ct)
                ?? throw new DomainException(
                    02009,
                    "originalEventId 对应的事件不存在或不属于当前用户。");
        }

        OutlookWriteTargetIdentity.Validate(
            requestedEventId,
            localEvent,
            originalEvent,
            request.Scope,
            request.RecurrenceId ?? request.Draft?.RecurrenceId);
        localEvent ??= originalEvent!;

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
                ct);
            if (occurrence is null)
            {
                throw new GraphRequestException(
                    HttpStatusCode.NotFound,
                    "Occurrence was not found in Outlook.");
            }

            var snapshot = new EventEntity
            {
                Id = Guid.NewGuid(),
                CalendarId = binding.PimCalendarId,
                IsException = true,
                IsSeriesMaster = false,
                SeriesMasterId = localEvent.Id,
                RecurrenceId = recurrenceId
            };
            try
            {
                OutlookEventMapper.ApplyGraphEvent(
                    snapshot,
                    occurrence.Value,
                    binding.Id,
                    binding.PimCalendarId,
                    binding.ConnectionId,
                    Guid.NewGuid());
            }
            catch (Exception ex)
            {
                throw new MalformedGraphResponseException(ex);
            }
            snapshot.IsException = true;
            snapshot.IsSeriesMaster = false;
            snapshot.SeriesMasterId = localEvent.Id;
            snapshot.RecurrenceId = recurrenceId;
            snapshot.RRule = null;
            if (string.IsNullOrEmpty(snapshot.OutlookEventId))
                throw new MalformedGraphResponseException();

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

    private async Task<EventEntity?> FindOccurrenceByGraphIdAsync(
        Guid bindingId, string? outlookEventId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outlookEventId))
            return null;
        var candidates = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId
                && e.OutlookEventId == outlookEventId)
            .ToListAsync(ct);
        return candidates
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutlookReauthenticationRequiredException)
        {
            throw;
        }
        catch (GraphRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MalformedGraphResponseException(ex);
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
            binding.ConnectionId, binding.GraphCalendarId, graphTargetId, ct);
        if (latest is null)
        {
            throw new GraphRequestException(
                HttpStatusCode.NotFound,
                "Event was not found while resolving the write ETag.");
        }
        var latestEtag = latest.Value.TryGetProperty("@odata.etag", out var etag)
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
            string step, string stepStatus, string status = "error")
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
                status, null, null, null, resultCode, resultMessage,
                operationRecord.ClientOperationId, false, false, resultMessage,
                NoChangesApplied: true);
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
                "CONFLICT_RESOLVE_FAILED",
                "冲突后获取最新数据失败，请使用同一 clientOperationId 稍后重试。",
                "conflict-fetch", "failed");
        }

        if (latest is null)
        {
            return await FailConflictResolveAsync(
                "conflict-resolve-not-found", "Latest event not found on Graph.",
                "GRAPH_EVENT_NOT_FOUND", "冲突后最新事件在远端已不存在，请刷新日程列表。",
                "conflict-fetch", "not-found", status: "not-found");
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
                "CONFLICT_RESOLVE_FAILED",
                "冲突后获取最新数据失败，请使用同一 clientOperationId 稍后重试。",
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
            "事件在 Outlook 中已被修改。请刷新后重新编辑；系统不会静默覆盖远端改动。",
            NoChangesApplied: true);
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
            "Microsoft 授权已失效，请重新授权后使用同一 clientOperationId 重试。",
            NoChangesApplied: true);
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

    private sealed record FailureClassification(
        string Status, string ErrorCode, string UserMessage,
        bool NoChangesApplied, string State);

    private sealed class MalformedGraphResponseException : Exception
    {
        public MalformedGraphResponseException(Exception? inner = null)
            : base("Graph returned a malformed occurrence response.", inner) { }
    }

    private static bool IsProviderFailure(Exception ex, CancellationToken ct)
        => ex is HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static bool IsGraphPayloadFailure(Exception ex)
        => ex is MalformedGraphResponseException
            or JsonException
            or KeyNotFoundException
            or FormatException
            or InvalidOperationException
            or ArgumentException
            or NullReferenceException
            or IndexOutOfRangeException
            or OverflowException;

    /// <summary>
    /// Maps provider/local failures to a sanitized receipt. Deterministic Graph
    /// rejections are not reported as provider-unavailable 502s, and retry hints
    /// are only emitted for 429/5xx/network/timeout.
    /// </summary>
    private static FailureClassification ClassifyFailure(
        Exception ex, bool callerCancelled, bool remoteWriteAttempted = true)
    {
        if (callerCancelled)
        {
            return new(
                "error",
                "CANCELLED",
                "写回请求已被调用方取消；未完成的写入可稍后重试。",
                NoChangesApplied: !remoteWriteAttempted,
                State: "failed");
        }

        if (ex is GraphRequestException grex && grex.StatusCode is { } statusCode)
        {
            var code = (int)statusCode;
            return code switch
            {
                400 => new(
                    "invalid-request",
                    "GRAPH_BAD_REQUEST",
                    "Outlook 拒绝了该写回请求，请检查日程内容后重新提交。",
                    NoChangesApplied: true,
                    State: "failed"),
                403 => new(
                    "forbidden",
                    "GRAPH_FORBIDDEN",
                    "当前 Microsoft 账户没有修改该日历的权限，请在 Outlook 中检查日历权限。",
                    NoChangesApplied: true,
                    State: "failed"),
                404 => new(
                    "not-found",
                    "GRAPH_EVENT_NOT_FOUND",
                    "Outlook 中已不存在目标任务，请刷新日程列表。",
                    NoChangesApplied: true,
                    State: "failed"),
                409 or 412 => new(
                    "conflict",
                    "CONFLICT",
                    "事件在 Outlook 中已被修改，请刷新后重新编辑；系统不会静默覆盖远端改动。",
                    NoChangesApplied: true,
                    State: "conflict"),
                408 when remoteWriteAttempted => new(
                    "unknown",
                    "PROVIDER_TIMEOUT",
                    "Outlook 请求超时，远端结果未知。请使用同一 clientOperationId 重试，或先在 Outlook 核对。",
                    NoChangesApplied: false,
                    State: "failed"),
                408 => new(
                    "error",
                    "PROVIDER_TIMEOUT",
                    "读取 Outlook 最新状态超时，尚未写入任何内容；可安全使用同一 clientOperationId 重试。",
                    NoChangesApplied: true,
                    State: "failed"),
                429 => new(
                    "error",
                    "PROVIDER_RATE_LIMITED",
                    remoteWriteAttempted
                        ? "Outlook 请求过于频繁，请使用同一 clientOperationId 稍后重试。"
                        : "查询 Outlook 目标时触发限流，尚未写入任何内容；可稍后使用同一 clientOperationId 重试。",
                    NoChangesApplied: !remoteWriteAttempted,
                    State: "failed"),
                >= 500 => new(
                    "error",
                    "PROVIDER_UNAVAILABLE",
                    remoteWriteAttempted
                        ? "Outlook 服务暂时不可用，请使用同一 clientOperationId 稍后重试。"
                        : "Outlook 服务暂时不可用，尚未写入任何内容；可稍后使用同一 clientOperationId 重试。",
                    NoChangesApplied: !remoteWriteAttempted,
                    State: "failed"),
                _ => new(
                    "invalid-request",
                    "GRAPH_REQUEST_REJECTED",
                    $"Outlook 拒绝了该写回请求（HTTP {code}），请检查日程内容后重新提交。",
                    NoChangesApplied: true,
                    State: "failed")
            };
        }

        if (ex is OperationCanceledException)
        {
            return remoteWriteAttempted
                ? new(
                    "unknown",
                    "PROVIDER_TIMEOUT",
                    "Outlook 请求超时，远端结果未知。请使用同一 clientOperationId 重试，或先在 Outlook 核对。",
                    NoChangesApplied: false,
                    State: "failed")
                : new(
                    "error",
                    "PROVIDER_TIMEOUT",
                    "读取 Outlook 最新状态超时，尚未写入任何内容；可安全使用同一 clientOperationId 重试。",
                    NoChangesApplied: true,
                    State: "failed");
        }

        if (ex is HttpRequestException)
        {
            return new(
                "error",
                "PROVIDER_UNAVAILABLE",
                remoteWriteAttempted
                    ? "Outlook 服务暂时不可用，请使用同一 clientOperationId 稍后重试。"
                    : "连接 Outlook 失败，尚未写入任何内容；可稍后使用同一 clientOperationId 重试。",
                NoChangesApplied: !remoteWriteAttempted,
                State: "failed");
        }

        if (!remoteWriteAttempted && IsGraphPayloadFailure(ex))
        {
            return new(
                "error",
                "GRAPH_INVALID_RESPONSE",
                "Outlook 返回的日程数据不完整或格式无效，尚未写入任何内容；可安全使用同一 clientOperationId 重试。",
                NoChangesApplied: true,
                State: "failed");
        }

        if (!remoteWriteAttempted)
        {
            return new(
                "error",
                "WRITEBACK_PREFLIGHT_FAILED",
                "写回前的 Outlook 目标解析失败，尚未写入任何内容。",
                NoChangesApplied: true,
                State: "failed");
        }

        return new(
            "unknown",
            "WRITEBACK_FAILED",
            "Outlook 写回未完成，远端结果未知。请使用同一 clientOperationId 重试，或先在 Outlook 核对。",
            NoChangesApplied: false,
            State: "failed");
    }

    private async Task<OutlookWriteResult> FailBatchAsync(
        OutlookSyncBatchEntity batch, OutlookWritebackOperationEntity operationRecord, Exception ex,
        string calendarName, string operation, Guid? eventId = null, string? eventTitle = null,
        bool callerCancelled = false, bool? noChangesAppliedOverride = null,
        bool remoteWriteAttempted = true)
    {
        var now = _timeProvider.GetUtcNow();
        batch.Status = "failed";
        batch.FailureCount = 1;
        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.ConfirmationCount = 0;
        var failure = ClassifyFailure(ex, callerCancelled, remoteWriteAttempted);
        if (noChangesAppliedOverride is { } noChangesApplied)
            failure = failure with { NoChangesApplied = noChangesApplied };
        SetBatchError(batch, failure.ErrorCode, "Outlook writeback request failed.");

        PopulateBatchHistory(batch, calendarName, operation, "failed", eventId, eventTitle,
            0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "failed", timestamp = now }
        });
        var result = new OutlookWriteResult(
            failure.Status, null, null, null, failure.ErrorCode, failure.UserMessage,
            operationRecord.ClientOperationId, false, false, failure.UserMessage,
            NoChangesApplied: failure.NoChangesApplied);
        operationRecord.State = failure.State;
        operationRecord.ErrorCode = failure.ErrorCode;
        operationRecord.ErrorMessage = failure.UserMessage;
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

        return result;
    }

    private async Task<OutlookWriteResult> MarkOutcomeUnknownAsync(
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
            "unknown",
            null,
            null,
            null,
            "OPERATION_OUTCOME_UNKNOWN",
            "Outlook 可能已经写入，但本地保存失败。请先在 Outlook 核对，勿直接重复提交。",
            operationRecord.ClientOperationId,
            false,
            false,
            "Outlook 可能已经写入，但本地保存失败。请先在 Outlook 核对，勿直接重复提交。",
            NoChangesApplied: false);
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

        return result;
    }

    /// <summary>
    /// A DELETE has already succeeded at Graph when local persistence fails. Persist the
    /// retryable ledger state together with the pending-finalization target. The same
    /// clientOperationId then resumes only the local commit and never repeats the remote
    /// delete.
    /// </summary>
    private async Task<OutlookWriteResult> MarkLocalStatePendingAsync(
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
        SetBatchError(
            batch,
            "local-state-pending",
            "Graph DELETE succeeded; local state commit is pending.");
        PopulateBatchHistory(batch, calendarName, operation, "failed", eventId, eventTitle,
            0, 0, 0, 0, 1, GetBindingIdFromBatch(batch));
        batch.StepsJson = JsonSerializer.Serialize(new[]
        {
            new { step = $"graph-{operation}", status = "success", timestamp = now },
            new { step = "persist-local", status = "pending", timestamp = now }
        });

        const string message =
            "Outlook 已删除，但本地状态尚未保存。请使用同一 clientOperationId 重试；系统不会重复删除远端。";
        var result = new OutlookWriteResult(
            "error",
            null,
            null,
            null,
            "LOCAL_STATE_PENDING",
            message,
            operationRecord.ClientOperationId,
            false,
            false,
            message);
        operationRecord.State = "failed";
        operationRecord.ErrorCode = "LOCAL_STATE_PENDING";
        operationRecord.ErrorMessage = message;
        if (DeserializePendingDeleteFinalization(operationRecord.ResultJson) is null)
            operationRecord.ResultJson = SerializeResult(result);
        operationRecord.CompletedAt = null;
        operationRecord.UpdatedAt = now;

        try
        {
            _logger.LogWarning(
                ex,
                "Graph DELETE succeeded but local writeback commit failed for operation {OperationRecordId}",
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
                persistedOperation.CompletedAt = null;
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
                "Failed to persist retryable local-state outcome for writeback operation {OperationRecordId}",
                operationRecord.Id);
        }

        return result;
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
            return await ReplayOnlyAsync(
                concurrent, payloadHash, operation.UndoOfOperationId,
                CancellationToken.None);
        }
    }

    private async Task<OutlookWriteResult?> TryReplayOrResumeAsync(
        OutlookWritebackOperationEntity operation,
        string payloadHash,
        Guid? undoOfOperationId,
        CancellationToken ct)
    {
        var isUndoRetry = undoOfOperationId is not null
            && operation.UndoOfOperationId == undoOfOperationId
            && operation.State is "conflict" or "failed";
        if (!string.Equals(operation.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            // A failed/conflicted undo may legitimately need a refreshed ETag or local
            // snapshot. The child id is deterministic from the original operation, so
            // the user cannot choose a new id; reusing it must not be reported as a
            // forged idempotency conflict.
            if (!isUndoRetry)
                return IdempotencyConflict(operation);
            operation.PayloadHash = payloadHash;
        }

        switch (operation.State)
        {
            case "completed":
                return ReplayCached(operation);
            case "conflict":
                if (!isUndoRetry)
                    return ReplayCached(operation);
                await ResetForRetryAsync(operation, payloadHash, ct);
                return null;
            case "processing":
                return await ReplayProcessingAsync(operation, ct);
            case "unknown":
                return OutcomeUnknown(operation);
            case "failed" when operation.ErrorCode == "LOCAL_STATE_PENDING":
                // The Graph DELETE already succeeded. Keep the durable pending
                // finalization payload so the caller can finish local state
                // without issuing a second remote delete.
                return null;
            case "failed":
                await ResetForRetryAsync(operation, payloadHash, ct);
                return null;
            default:
                return OperationInProgress(operation);
        }
    }

    private async Task ResetForRetryAsync(
        OutlookWritebackOperationEntity operation,
        string payloadHash,
        CancellationToken ct)
    {
        operation.State = "processing";
        operation.PayloadHash = payloadHash;
        operation.AttemptCount++;
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.ResultJson = null;
        operation.CompletedAt = null;
        operation.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    private async Task<OutlookWriteResult> ReplayOnlyAsync(
        OutlookWritebackOperationEntity operation,
        string payloadHash,
        Guid? undoOfOperationId,
        CancellationToken ct)
    {
        var replay = await TryReplayOrResumeAsync(
            operation, payloadHash, undoOfOperationId, ct);
        return replay ?? OperationInProgress(operation);
    }

    private static OutlookWriteResult IdempotencyConflict(OutlookWritebackOperationEntity operation)
        => new(
            "conflict",
            ErrorCode: "IDEMPOTENCY_CONFLICT",
            ErrorMessage: "同一 clientOperationId 已用于不同的写回内容。请使用新的 clientOperationId。",
            ClientOperationId: operation.ClientOperationId,
            UserMessage: "同一 clientOperationId 已用于不同的写回内容。请使用新的 clientOperationId。",
            NoChangesApplied: true);

    private static OutlookWriteResult OperationInProgress(OutlookWritebackOperationEntity operation)
        => new(
            "conflict",
            ErrorCode: "OPERATION_IN_PROGRESS",
            ErrorMessage: "同一 clientOperationId 的写回仍在处理中，请勿并发重试。",
            ClientOperationId: operation.ClientOperationId,
            UserMessage: "同一 clientOperationId 的写回仍在处理中，请勿并发重试。");

    private static OutlookWriteResult OutcomeUnknown(OutlookWritebackOperationEntity operation)
        => new(
            "unknown",
            ErrorCode: "OPERATION_OUTCOME_UNKNOWN",
            ErrorMessage: "该操作的上一次结果未知。请先在 Outlook 核对，勿直接重复提交。",
            ClientOperationId: operation.ClientOperationId,
            UserMessage: "该操作的上一次结果未知。请先在 Outlook 核对，勿直接重复提交。",
            NoChangesApplied: false);

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

    private static string? GetConflictLatestEtag(
        OutlookWritebackOperationEntity? operation)
    {
        if (operation is not { State: "conflict" }
            || string.IsNullOrWhiteSpace(operation.ResultJson))
            return null;
        try
        {
            return DeserializeResult(operation.ResultJson)?.LatestEtag
                is { Length: > 0 } latestEtag
                ? latestEtag
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private OutlookWriteResult BuildSuccessResult(
        OutlookWritebackOperationEntity operation,
        string status,
        EventEntity? entity)
    {
        var undoAvailable = operation.UndoState == "available";
        var isSeries = entity is not null && OutlookWriteTargetIdentity.IsSeriesMasterLike(entity);
        var message = status switch
        {
            "created" => isSeries
                ? "已在 Outlook 中创建重复日程系列；如需撤销，将删除整个系列。"
                : "已在 Outlook 中创建。",
            "updated" => operation.Scope == "series"
                ? "已写回 Outlook 整个重复日程系列。"
                : "已写回 Outlook。",
            "deleted" => undoAvailable
                ? "已从 Outlook 删除；可使用 clientOperationId 调用撤销端点恢复。"
                : operation.Scope == "series"
                    ? "已删除整个 Outlook 重复日程系列（含后续发生项），不提供自动撤销。"
                    : "已删除本次 Outlook 重复日程发生项，不提供自动撤销。",
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

    /// <summary>
    /// Hashes the caller-supplied request before local event resolution. Unlike the
    /// resolved payload hash, this can be recomputed after the event row has been
    /// removed, so a replay can never substitute a different payload for the one
    /// that produced the cached receipt.
    /// </summary>
    public static string ComputeRequestHash(
        string operation,
        Guid? eventId,
        object? body,
        string? scope,
        Guid? originalEventId,
        string? recurrenceId,
        string? expectedEtag)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            operation,
            eventId,
            body,
            scope,
            originalEventId,
            recurrenceId,
            expectedEtag
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
