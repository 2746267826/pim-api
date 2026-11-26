using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notifications;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notifications;

public sealed class NotificationQueryService(
    PimDbContext db,
    ICurrentUserService currentUser,
    TimeProvider clock)
{
    private Guid UserId => currentUser.UserId ?? throw new DomainException(40101, "请先登录");

    public async Task<NotificationListResponse> ListAsync(
        int limit,
        long? beforeCursor,
        CancellationToken ct)
    {
        var userId = UserId;
        limit = Math.Clamp(limit, 1, 100);
        var query = db.NotificationEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.InAppVisible);
        if (beforeCursor is > 0)
            query = query.Where(e => e.Sequence < beforeCursor.Value);
        var entities = await query
            .OrderByDescending(e => e.Sequence)
            .Take(limit)
            .ToListAsync(ct);
        var items = entities.Select(Map).ToList();
        var unread = await db.NotificationEvents
            .AsNoTracking()
            .CountAsync(e => e.UserId == userId && e.InAppVisible && e.ReadAt == null, ct);
        return new NotificationListResponse(items, unread);
    }

    public async Task<NotificationReadResponse> MarkReadAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        var entity = await db.NotificationEvents
            .SingleOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct)
            ?? throw new DomainException(40401, "通知不存在");
        if (entity.ReadAt is null)
        {
            entity.ReadAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new NotificationReadResponse(entity.Id, entity.ReadAt);
    }

    public async Task<NotificationReadAllResponse> MarkAllReadAsync(CancellationToken ct)
    {
        var userId = UserId;
        var now = clock.GetUtcNow();
        if (db.Database.IsRelational())
        {
            var updated = await db.NotificationEvents
                .Where(e => e.UserId == userId && e.InAppVisible && e.ReadAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ReadAt, now), ct);
            return new NotificationReadAllResponse(updated);
        }

        var items = await db.NotificationEvents
            .Where(e => e.UserId == userId && e.InAppVisible && e.ReadAt == null)
            .ToListAsync(ct);
        foreach (var item in items) item.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return new NotificationReadAllResponse(items.Count);
    }

    public async Task<NotificationDeliveryListResponse> ListDeliveriesAsync(
        int limit,
        string? status,
        CancellationToken ct)
    {
        var userId = UserId;
        limit = Math.Clamp(limit, 1, 100);
        var query = db.NotificationDeliveries
            .AsNoTracking()
            .Where(d => d.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim();
            if (!NotificationDeliveryStatuses.All.Contains(normalized, StringComparer.Ordinal))
                throw new DomainException(40001, "投递状态筛选无效");
            query = query.Where(d => d.Status == normalized);
        }

        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .Select(d => new NotificationDeliveryDto(
                d.Id,
                d.EventId,
                d.Event.EventType,
                d.Event.Title,
                d.Channel,
                d.DeviceId,
                d.Status,
                d.AttemptCount,
                d.NextAttemptAt,
                d.LastErrorCode,
                d.LastErrorMessage,
                d.CreatedAt,
                d.DeliveredAt,
                d.DeadLetteredAt))
            .ToListAsync(ct);
        return new NotificationDeliveryListResponse(items);
    }

    public async Task<NotificationDeliveryDto> RetryDeliveryAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        var delivery = await db.NotificationDeliveries
            .Include(d => d.Event)
            .SingleOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct)
            ?? throw new DomainException(40401, "投递记录不存在");
        if (delivery.Status is NotificationDeliveryStatuses.Pending
            or NotificationDeliveryStatuses.Retrying
            or NotificationDeliveryStatuses.Processing)
            return MapDelivery(delivery);
        if (delivery.Status != NotificationDeliveryStatuses.DeadLetter &&
            delivery.Status != NotificationDeliveryStatuses.Skipped)
            throw new DomainException(40901, "仅失败或跳过的投递可以重试");

        var subscription = await db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == delivery.SubscriptionId, ct);
        if (subscription is null || subscription.Status != "Active" ||
            subscription.UserId != userId)
            throw new DomainException(40901, "推送订阅已失效，请重新启用浏览器推送");

        var now = clock.GetUtcNow();
        delivery.Status = NotificationDeliveryStatuses.Pending;
        delivery.AttemptCount = 0;
        delivery.NextAttemptAt = now;
        delivery.LockedUntil = null;
        delivery.LockedBy = null;
        delivery.LastErrorCode = null;
        delivery.LastErrorMessage = null;
        delivery.DeadLetteredAt = null;
        delivery.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return MapDelivery(delivery);
    }

    public async Task<IReadOnlyList<NotificationDto>> ReadStreamBatchAsync(
        Guid userId,
        long afterCursor,
        int limit,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var entities = await db.NotificationEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId &&
                        e.InAppVisible &&
                        e.Sequence > afterCursor)
            .OrderBy(e => e.Sequence)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(Map).ToList();
    }

    public async Task<long> LatestCursorAsync(Guid userId, CancellationToken ct) =>
        await db.NotificationEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.InAppVisible)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

    private static NotificationDto Map(NotificationEventEntity entity) =>
        new(
            entity.Id,
            entity.Sequence,
            entity.EventType,
            entity.Title,
            entity.Body,
            entity.DetailUrl,
            entity.ReadAt is not null,
            entity.CreatedAt,
            entity.ReadAt);

    private static NotificationDeliveryDto MapDelivery(NotificationDeliveryEntity delivery) =>
        new(
            delivery.Id,
            delivery.EventId,
            delivery.Event.EventType,
            delivery.Event.Title,
            delivery.Channel,
            delivery.DeviceId,
            delivery.Status,
            delivery.AttemptCount,
            delivery.NextAttemptAt,
            delivery.LastErrorCode,
            delivery.LastErrorMessage,
            delivery.CreatedAt,
            delivery.DeliveredAt,
            delivery.DeadLetteredAt);
}
