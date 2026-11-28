using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Notifications;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notifications;

public sealed class NotificationDeliveryWorker(
    PimDbContext db,
    IWebPushTransport transport,
    ISecretProtector protector,
    IOptions<NotificationDispatcherOptions> options,
    TimeProvider clock,
    ILogger<NotificationDeliveryWorker> logger)
{
    private NotificationDispatcherOptions Options => options.Value;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await FanoutOnceAsync(ct);
        await DeliverOnceAsync(ct);
    }

    public async Task FanoutOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var claimed = await ClaimEventsAsync(now, ct);
        foreach (var eventId in claimed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var notification = await db.NotificationEvents
                    .IgnoreQueryFilters()
                    .SingleOrDefaultAsync(e => e.Id == eventId, ct);
                if (notification is null) continue;

                var subscriptions = new List<NotificationSubscriptionEntity>();
                if (notification.PushEligible)
                {
                    var snapshot = NotificationEventSnapshot.Read(
                        notification.EligibleSubscriptionIdsJson);
                    if (snapshot is null || snapshot.Length > 0)
                    {
                        var query = db.NotificationSubscriptions
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(s => s.UserId == notification.UserId &&
                                        s.Channel == NotificationChannels.WebPush &&
                                        s.Status == "Active");
                        if (snapshot is not null)
                            query = query.Where(s => snapshot.Contains(s.Id));
                        subscriptions = await query.ToListAsync(ct);
                    }
                }

                var accountPreferences = notification.PushEligible
                    ? await db.NotificationPreferences
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .SingleOrDefaultAsync(p => p.UserId == notification.UserId, ct)
                    : null;
                var deviceOverrides =
                    NotificationDevicePreferences.Read(accountPreferences?.DeviceOverridesJson);
                foreach (var subscription in subscriptions)
                {
                    if (!NotificationDevicePreferences.AllowsPush(
                            notification.EventType,
                            subscription.DeviceId,
                            accountPreferences,
                            deviceOverrides))
                        continue;
                    await InsertDeliveryAsync(notification, subscription, now, ct);
                }

                await db.NotificationEvents
                    .Where(e => e.Id == eventId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(e => e.FanoutStatus, "Completed")
                        .SetProperty(e => e.FanoutCompletedAt, now)
                        .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(e => e.LastErrorCode, (string?)null)
                        .SetProperty(e => e.LastErrorMessage, (string?)null), ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await MarkFanoutFailureAsync(eventId, error, ct);
            }
        }
    }

    public async Task DeliverOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var claimed = await ClaimDeliveriesAsync(now, ct);
        foreach (var deliveryId in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverOneAsync(deliveryId, ct);
        }
    }

    private async Task DeliverOneAsync(Guid deliveryId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            var delivery = await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .Include(d => d.Event)
                .SingleOrDefaultAsync(d => d.Id == deliveryId, ct);
            if (delivery is null) return;
            var subscription = await db.NotificationSubscriptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == delivery.SubscriptionId, ct);
            await DeliverLoadedAsync(delivery, subscription, ct);
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var target = await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(d => d.Id == deliveryId)
                .Select(d => new { d.SubscriptionId, d.UserId })
                .SingleOrDefaultAsync(ct);
            if (target is null)
            {
                await transaction.CommitAsync(ct);
                return;
            }

            // Lock ordering is subscription -> delivery everywhere. Revocation
            // therefore either happens before this send or waits until the
            // delivery transaction has committed.
            var subscription = await db.NotificationSubscriptions
                .FromSqlInterpolated($"""
                    SELECT * FROM notification_subscriptions
                    WHERE id = {target.SubscriptionId}
                    FOR UPDATE
                    """)
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(ct);
            var delivery = await db.NotificationDeliveries
                .FromSqlInterpolated($"""
                    SELECT * FROM notification_deliveries
                    WHERE id = {deliveryId}
                    FOR UPDATE
                    """)
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(ct);
            if (delivery is null)
            {
                await transaction.CommitAsync(ct);
                return;
            }
            await db.Entry(delivery).Reference(d => d.Event).LoadAsync(ct);
            await DeliverLoadedAsync(delivery, subscription, ct);
            await transaction.CommitAsync(ct);
        });
    }

    private async Task DeliverLoadedAsync(
        NotificationDeliveryEntity delivery,
        NotificationSubscriptionEntity? subscription,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        try
        {
            if (delivery.Status != NotificationDeliveryStatuses.Processing)
                return;

            if (subscription is null ||
                subscription.Status != "Active" ||
                subscription.UserId != delivery.UserId)
            {
                await SetDeliveryStateAsync(
                    delivery,
                    NotificationDeliveryStatuses.Suppressed,
                    "subscription_revoked",
                    "推送订阅已撤销或转移，未发送",
                    now,
                    ct);
                return;
            }

            if (!transport.IsConfigured)
            {
                await SetDeliveryStateAsync(
                    delivery,
                    NotificationDeliveryStatuses.Skipped,
                    "provider_not_configured",
                    "服务端未配置 Web Push，站内通知已保留",
                    now,
                    ct);
                return;
            }

            if (delivery.AttemptCount > Math.Max(1, Options.MaxAttempts))
            {
                await SetDeliveryStateAsync(
                    delivery,
                    NotificationDeliveryStatuses.DeadLetter,
                    "retry_exhausted",
                    "多次重试仍未送达，已进入死信",
                    now,
                    ct,
                    deadLetter: true);
                return;
            }

            PushSubscriptionSecret secret;
            try
            {
                secret = new PushSubscriptionSecret(
                    protector.Unprotect(subscription.EndpointCiphertext),
                    WebPushCrypto.Base64UrlDecode(protector.Unprotect(subscription.P256DhCiphertext)),
                    WebPushCrypto.Base64UrlDecode(protector.Unprotect(subscription.AuthCiphertext)));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                subscription.Status = "Invalid";
                subscription.RevokedAt = now;
                subscription.UpdatedAt = now;
                subscription.LastErrorCode = "subscription_unreadable";
                subscription.LastErrorMessage = "推送订阅密钥无法解密，请重新启用浏览器推送";
                await db.SaveChangesAsync(ct);
                await SetDeliveryStateAsync(
                    delivery,
                    NotificationDeliveryStatuses.DeadLetter,
                    "subscription_unreadable",
                    "推送订阅密钥无法解密，请重新启用浏览器推送",
                    now,
                    ct,
                    deadLetter: true);
                return;
            }

            PushSendResult result;
            var sendTimeout = TimeSpan.FromSeconds(Math.Max(1, Options.ProviderTimeoutSeconds));
            using var sendBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<PushSendResult>? sendTask = null;
            try
            {
                // Bounded provider call while the delivery transaction holds
                // subscription → delivery locks. CancelAfter lets a cooperative provider
                // stop; WaitAsync also bounds a provider that ignores its token, so a hung
                // transport cannot hold revocation locks indefinitely.
                sendBudget.CancelAfter(sendTimeout);
                sendTask = transport.SendAsync(
                    secret,
                    new PushNotificationMessage(
                        delivery.Event.Title,
                        delivery.Event.Body,
                        delivery.Event.DetailUrl,
                        delivery.Event.Id,
                        delivery.Event.EventType),
                    sendBudget.Token);
                result = await sendTask.WaitAsync(sendTimeout, ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                error is OperationCanceledException or TimeoutException)
            {
                // Our own send budget expired, not a shutdown: retry instead of
                // dead-lettering a healthy delivery or failing the whole batch.
                logger.LogWarning(
                    "Push delivery {DeliveryId} exceeded the provider time budget",
                    delivery.Id);
                sendBudget.Cancel();
                if (sendTask is not null)
                    _ = sendTask.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                result = PushSendResult.Retryable("push_transport_timeout");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Push delivery {DeliveryId} raised {ErrorType}",
                    delivery.Id, error.GetType().Name);
                result = PushSendResult.Retryable("push_transport_error");
            }

            switch (result.Outcome)
            {
                case PushSendOutcome.Delivered:
                    delivery.Status = NotificationDeliveryStatuses.Delivered;
                    delivery.DeliveredAt = now;
                    delivery.LastErrorCode = null;
                    delivery.LastErrorMessage = null;
                    delivery.LockedUntil = null;
                    delivery.LockedBy = null;
                    delivery.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                    break;
                case PushSendOutcome.SubscriptionGone:
                {
                    var goneCode = SafeErrorCode(result.ErrorCode);
                    await MarkSubscriptionGoneAsync(subscription, goneCode, now, ct);
                    await SetDeliveryStateAsync(
                        delivery,
                        NotificationDeliveryStatuses.Suppressed,
                        goneCode,
                        SafeMessage(PushSendOutcome.SubscriptionGone),
                        now,
                        ct);
                    break;
                }
                case PushSendOutcome.PermanentFailure:
                    await SetDeliveryStateAsync(
                        delivery,
                        NotificationDeliveryStatuses.DeadLetter,
                        SafeErrorCode(result.ErrorCode),
                        SafeMessage(PushSendOutcome.PermanentFailure),
                        now,
                        ct,
                        deadLetter: true);
                    break;
                default:
                    if (delivery.AttemptCount >= Math.Max(1, Options.MaxAttempts))
                    {
                        await SetDeliveryStateAsync(
                            delivery,
                            NotificationDeliveryStatuses.DeadLetter,
                            "retry_exhausted",
                            "多次重试仍未送达，已进入死信",
                            now,
                            ct,
                            deadLetter: true);
                    }
                    else
                    {
                        delivery.Status = NotificationDeliveryStatuses.Retrying;
                        delivery.NextAttemptAt = now.Add(Backoff(delivery.AttemptCount, delivery.Id));
                        delivery.LockedUntil = null;
                        delivery.LockedBy = null;
                        delivery.LastErrorCode = SafeErrorCode(result.ErrorCode);
                        delivery.LastErrorMessage = SafeMessage(PushSendOutcome.RetryableFailure);
                        delivery.UpdatedAt = now;
                        await db.SaveChangesAsync(ct);
                    }
                    break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(
                "Push delivery {DeliveryId} failed internally ({ErrorType})",
                delivery.Id, error.GetType().Name);
            await SetDeliveryStateAsync(
                delivery,
                NotificationDeliveryStatuses.DeadLetter,
                "internal_error",
                "投递处理失败，请联系管理员",
                now,
                ct,
                deadLetter: true);
        }
    }

    private async Task SetDeliveryStateAsync(
        NotificationDeliveryEntity delivery,
        string status,
        string errorCode,
        string safeMessage,
        DateTimeOffset now,
        CancellationToken ct,
        bool deadLetter = false)
    {
        delivery.Status = status;
        delivery.LastErrorCode = errorCode;
        delivery.LastErrorMessage = safeMessage;
        delivery.LockedUntil = null;
        delivery.LockedBy = null;
        delivery.UpdatedAt = now;
        if (deadLetter) delivery.DeadLetteredAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkSubscriptionGoneAsync(
        NotificationSubscriptionEntity subscription,
        string errorCode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await db.NotificationSubscriptions
            .Where(s => s.Id == subscription.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, "Revoked")
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.UpdatedAt, now)
                .SetProperty(s => s.LastErrorCode, errorCode)
                .SetProperty(s => s.LastErrorMessage, "推送服务报告订阅已失效")
                .SetProperty(s => s.Version, Guid.NewGuid()), ct);
        await db.NotificationDeliveries
            .Where(d => d.SubscriptionId == subscription.Id &&
                        d.Id != Guid.Empty &&
                        (d.Status == NotificationDeliveryStatuses.Pending ||
                         d.Status == NotificationDeliveryStatuses.Retrying ||
                         d.Status == NotificationDeliveryStatuses.Processing))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.Status, NotificationDeliveryStatuses.Suppressed)
                .SetProperty(d => d.LastErrorCode, errorCode)
                .SetProperty(d => d.LastErrorMessage, "推送订阅已失效")
                .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(d => d.LockedBy, (string?)null)
                .SetProperty(d => d.UpdatedAt, now), ct);
    }

    private async Task InsertDeliveryAsync(
        NotificationEventEntity notification,
        NotificationSubscriptionEntity subscription,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH active_subscription AS (
                SELECT s.id AS subscription_id, s.device_id
                FROM notification_subscriptions s
                WHERE s.id = {subscription.Id}
                  AND s.user_id = {notification.UserId}
                  AND s.channel = {NotificationChannels.WebPush}
                  AND s.status = 'Active'
                FOR SHARE
            )
            INSERT INTO notification_deliveries
                (id, event_id, subscription_id, user_id, channel, device_id, status,
                 attempt_count, next_attempt_at, created_at, updated_at)
            SELECT
                {id}, {notification.Id}, a.subscription_id, {notification.UserId},
                {NotificationChannels.WebPush}, a.device_id,
                {NotificationDeliveryStatuses.Pending}, 0, {now}, {now}, {now}
            FROM active_subscription a
            ON CONFLICT (event_id, subscription_id) DO NOTHING
            """, ct);
    }

    private async Task<List<Guid>> ClaimEventsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await ClaimEventsNonRelationalAsync(now, ct);

        var ids = new List<Guid>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE notification_events
            SET fanout_status = 'Processing',
                locked_until = @locked_until,
                fanout_attempts = fanout_attempts + 1
            WHERE id IN (
                SELECT id FROM notification_events
                WHERE (fanout_status IN ('Pending', 'Retrying')
                       OR (fanout_status = 'Processing' AND locked_until < @now))
                  AND next_fanout_at <= @now
                ORDER BY next_fanout_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("locked_until", now.AddSeconds(Math.Max(10, Options.LockSeconds)));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("limit", Math.Max(1, Options.FanoutBatchSize));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        if (ids.Count > 0) db.ChangeTracker.Clear();
        return ids;
    }

    private async Task<List<Guid>> ClaimEventsNonRelationalAsync(
        DateTimeOffset now,
        CancellationToken ct)
    {
        var items = await db.NotificationEvents
            .IgnoreQueryFilters()
            .Where(e => (e.FanoutStatus == "Pending" || e.FanoutStatus == "Retrying") &&
                        e.NextFanoutAt <= now)
            .OrderBy(e => e.NextFanoutAt)
            .Take(Math.Max(1, Options.FanoutBatchSize))
            .ToListAsync(ct);
        foreach (var item in items)
        {
            item.FanoutStatus = "Processing";
            item.LockedUntil = now.AddSeconds(Math.Max(10, Options.LockSeconds));
            item.FanoutAttempts++;
        }
        await db.SaveChangesAsync(ct);
        return items.Select(e => e.Id).ToList();
    }

    private async Task<List<Guid>> ClaimDeliveriesAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await ClaimDeliveriesNonRelationalAsync(now, ct);

        var ids = new List<Guid>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE notification_deliveries
            SET status = 'Processing',
                locked_until = @locked_until,
                locked_by = @locked_by,
                attempt_count = attempt_count + 1,
                updated_at = @now
            WHERE id IN (
                SELECT id FROM notification_deliveries
                WHERE (status IN ('Pending', 'Retrying')
                       OR (status = 'Processing' AND locked_until < @now))
                  AND next_attempt_at <= @now
                ORDER BY next_attempt_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("locked_until", now.AddSeconds(Math.Max(10, Options.LockSeconds)));
        command.Parameters.AddWithValue("locked_by", Guid.NewGuid().ToString("N")[..16]);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("limit", Math.Max(1, Options.DeliveryBatchSize));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        if (ids.Count > 0) db.ChangeTracker.Clear();
        return ids;
    }

    private async Task<List<Guid>> ClaimDeliveriesNonRelationalAsync(
        DateTimeOffset now,
        CancellationToken ct)
    {
        var items = await db.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => (d.Status == NotificationDeliveryStatuses.Pending ||
                         d.Status == NotificationDeliveryStatuses.Retrying) &&
                        d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(Math.Max(1, Options.DeliveryBatchSize))
            .ToListAsync(ct);
        foreach (var item in items)
        {
            item.Status = NotificationDeliveryStatuses.Processing;
            item.LockedUntil = now.AddSeconds(Math.Max(10, Options.LockSeconds));
            item.AttemptCount++;
            item.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return items.Select(d => d.Id).ToList();
    }

    private async Task MarkFanoutFailureAsync(
        Guid eventId,
        Exception error,
        CancellationToken ct)
    {
        logger.LogWarning(
            "Notification fanout {EventId} failed ({ErrorType})",
            eventId, error.GetType().Name);
        var now = clock.GetUtcNow();
        var entity = await db.NotificationEvents
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (entity is null) return;
        var exhausted = entity.FanoutAttempts >= Math.Max(1, Options.MaxAttempts);
        entity.FanoutStatus = exhausted ? "Failed" : "Retrying";
        entity.NextFanoutAt = now.Add(Backoff(entity.FanoutAttempts, entity.Id));
        entity.LockedUntil = null;
        entity.LastErrorCode = "fanout_failed";
        entity.LastErrorMessage = exhausted
            ? "通知分发多次失败，已停止"
            : "通知分发失败，稍后自动重试";
        await db.SaveChangesAsync(ct);
    }

    private TimeSpan Backoff(int attempt, Guid id)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 10);
        var baseSeconds = Math.Max(0, Options.BaseDelaySeconds);
        var seconds = Math.Min(
            Math.Max(0, Options.MaxDelaySeconds),
            baseSeconds * Math.Pow(2, exponent));
        var jitter = (id.GetHashCode() & 0x7fffffff) % Math.Max(1, (int)Math.Ceiling(seconds * 0.25));
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static string SafeMessage(PushSendOutcome outcome) =>
        outcome switch
        {
            PushSendOutcome.SubscriptionGone => "推送订阅已失效，请重新启用浏览器推送",
            PushSendOutcome.PermanentFailure => "推送服务拒绝了该条通知",
            _ => "推送服务暂时不可用，稍后自动重试"
        };

    private static string SafeErrorCode(string? code)
    {
        var value = code?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 80 ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            return "push_error";
        return value;
    }
}
