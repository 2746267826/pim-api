using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// Bridges the public event endpoints to the controlled Outlook writeback service.
/// A null result means the event/calendar is local and the caller must continue
/// through <see cref="CalendarService"/> unchanged.
/// </summary>
public sealed class OutlookCalendarWritebackGateway
{
    private readonly PimDbContext _db;
    private readonly OutlookEventWriteService _writeService;

    public OutlookCalendarWritebackGateway(
        PimDbContext db,
        OutlookEventWriteService writeService)
    {
        _db = db;
        _writeService = writeService;
    }

    public async Task<OutlookWriteResult?> CreateAsync(
        Guid userId,
        CreateEventRequest request,
        Guid clientOperationId,
        CancellationToken ct)
    {
        var calendar = request.CalendarId == Guid.Empty
            ? await _db.Set<CalendarEntity>()
                .Where(c => c.UserId == userId && c.Kind == "calendar")
                .OrderByDescending(c => c.IsDefault)
                .FirstOrDefaultAsync(ct)
            : await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == request.CalendarId && c.UserId == userId, ct);
        if (calendar is null)
            return null;

        var binding = await FindBindingAsync(userId, calendar.Id, ct);
        if (binding is null)
            return null;

        var draft = request with { CalendarId = calendar.Id };
        var requestHash = OutlookEventWriteService.ComputeRequestHash(
            "create", null, draft, "instance", null, null, null);
        return await ExecuteSafelyAsync(
            userId,
            new OutlookWriteRequest(
                "create",
                binding.Id,
                null,
                draft,
                "instance",
                clientOperationId),
            requestHash,
            clientOperationId,
            ct);
    }

    public async Task<OutlookWriteResult?> UpdateAsync(
        Guid userId,
        Guid eventId,
        UpdateEventRequest request,
        string? scope,
        string? recurrenceId,
        Guid? originalEventId,
        Guid clientOperationId,
        string? expectedEtag,
        CancellationToken ct)
    {
        var effectiveRecurrenceId = recurrenceId ?? request.RecurrenceId;
        var scopeHint = NormalizeScopeHint(scope);
        var requestHash = OutlookEventWriteService.ComputeRequestHash(
            "update",
            eventId,
            request,
            scopeHint,
            originalEventId,
            effectiveRecurrenceId,
            expectedEtag);
        var entity = await ResolveEventAsync(
            userId, eventId, scope, originalEventId, effectiveRecurrenceId, ct);
        if (entity is null)
        {
            return await _writeService.TryReplayWithoutEventAsync(
                userId,
                "update",
                clientOperationId,
                eventId,
                scopeHint,
                requestHash,
                ct);
        }

        var binding = await FindBindingAsync(userId, entity.CalendarId, ct);
        if (binding is null)
            return null;

        if (request.CalendarId != Guid.Empty && request.CalendarId != entity.CalendarId)
            throw new DomainException(02009, "Outlook 日程暂不支持跨日历移动，请在目标日历中重新创建。");

        var draft = OutlookEventDraftFactory.FromUpdate(entity, request) with
        {
            CalendarId = entity.CalendarId
        };
        return await ExecuteSafelyAsync(
            userId,
            new OutlookWriteRequest(
                "update",
                binding.Id,
                eventId,
                draft,
                NormalizeScope(scope, entity),
                clientOperationId,
                ExpectedEtag: expectedEtag,
                OriginalEventId: originalEventId,
                RecurrenceId: effectiveRecurrenceId),
            requestHash,
            clientOperationId,
            ct);
    }

    public async Task<OutlookWriteResult?> DeleteAsync(
        Guid userId,
        Guid eventId,
        string? scope,
        string? recurrenceId,
        Guid? originalEventId,
        Guid clientOperationId,
        string? expectedEtag,
        CancellationToken ct)
    {
        var scopeHint = NormalizeScopeHint(scope);
        var requestHash = OutlookEventWriteService.ComputeRequestHash(
            "delete",
            eventId,
            null,
            scopeHint,
            originalEventId,
            recurrenceId,
            expectedEtag);
        var entity = await ResolveEventAsync(
            userId, eventId, scope, originalEventId, recurrenceId, ct);
        if (entity is null)
        {
            return await _writeService.TryReplayWithoutEventAsync(
                userId,
                "delete",
                clientOperationId,
                eventId,
                scopeHint,
                requestHash,
                ct);
        }

        var binding = await FindBindingAsync(userId, entity.CalendarId, ct);
        if (binding is null)
            return null;

        return await ExecuteSafelyAsync(
            userId,
            new OutlookWriteRequest(
                "delete",
                binding.Id,
                eventId,
                null,
                NormalizeScope(scope, entity),
                clientOperationId,
                ExpectedEtag: expectedEtag,
                OriginalEventId: originalEventId,
                RecurrenceId: recurrenceId),
            requestHash,
            clientOperationId,
            ct);
    }

    private async Task<EventEntity?> ResolveEventAsync(
        Guid userId,
        Guid eventId,
        string? scope,
        Guid? originalEventId,
        string? recurrenceId,
        CancellationToken ct)
    {
        var urlEntity = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == userId, ct);

        EventEntity? originalEvent = null;
        if (originalEventId is { } original && original != Guid.Empty)
        {
            originalEvent = await _db.Set<EventEntity>()
                .Include(e => e.Calendar)
                .FirstOrDefaultAsync(
                    e => e.Id == original && e.Calendar.UserId == userId,
                    ct);
            if (originalEvent is null)
            {
                throw new DomainException(
                    02009,
                    "originalEventId 对应的事件不存在或不属于当前用户。");
            }
        }

        // A missing event with no originalEventId stays nullable so the caller can
        // replay a completed delete receipt for the same clientOperationId.
        if (originalEvent is null)
            return urlEntity;

        OutlookWriteTargetIdentity.Validate(
            eventId, urlEntity, originalEvent, scope, recurrenceId);
        return urlEntity ?? originalEvent;
    }

    private async Task<OutlookCalendarBindingEntity?> FindBindingAsync(
        Guid userId, Guid calendarId, CancellationToken ct)
    {
        var connectionIds = _db.Set<OutlookConnectionEntity>()
            .Where(c => c.UserId == userId)
            .Select(c => c.Id);
        return await _db.Set<OutlookCalendarBindingEntity>()
            .FirstOrDefaultAsync(
                b => b.PimCalendarId == calendarId && connectionIds.Contains(b.ConnectionId),
                ct);
    }

    private async Task<OutlookWriteResult> ExecuteSafelyAsync(
        Guid userId,
        OutlookWriteRequest request,
        string requestHash,
        Guid clientOperationId,
        CancellationToken ct)
    {
        try
        {
            return await _writeService.ExecuteAsync(userId, request, ct, requestHash);
        }
        catch (HttpRequestException)
        {
            return ProviderUnavailable(clientOperationId);
        }
    }

    private static OutlookWriteResult ProviderUnavailable(Guid clientOperationId)
        => new(
            "error",
            ErrorCode: "PROVIDER_UNAVAILABLE",
            ErrorMessage: "Outlook 服务暂时不可用，请使用同一 clientOperationId 稍后重试。",
            ClientOperationId: clientOperationId,
            UserMessage: "Outlook 服务暂时不可用，请使用同一 clientOperationId 稍后重试。",
            NoChangesApplied: false);

    private static string NormalizeScope(string? scope, EventEntity entity)
    {
        if (string.Equals(scope, "this", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "instance", StringComparison.OrdinalIgnoreCase))
            return "instance";
        if (string.Equals(scope, "series", StringComparison.OrdinalIgnoreCase))
            return "series";
        return entity.IsSeriesMaster || !string.IsNullOrEmpty(entity.RRule)
            ? "series"
            : "instance";
    }

    private static string? NormalizeScopeHint(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return null;
        if (string.Equals(scope, "this", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "instance", StringComparison.OrdinalIgnoreCase))
            return "instance";
        if (string.Equals(scope, "series", StringComparison.OrdinalIgnoreCase))
            return "series";
        return scope.Trim();
    }
}
