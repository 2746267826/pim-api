using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Exceptions;
using Pim.Core.Notifications;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notifications;

public sealed class NotificationSubscriptionService(
    PimDbContext db,
    ICurrentUserService currentUser,
    ISecretProtector protector,
    IWebPushTransport transport,
    IOptions<WebPushOptions> options,
    TimeProvider clock,
    ILogger<NotificationSubscriptionService> logger)
{
    private const int MaxUpsertAttempts = 2;
    private const string EndpointOwnershipConflictMessage =
        "该推送地址已归属其他账号，本版本仅允许原账号继续使用，不支持跨账号换绑";

    private Guid UserId => currentUser.UserId ?? throw new DomainException(40101, "请先登录");

    public NotificationCapabilitiesDto GetCapabilities() =>
        new(
            transport.IsConfigured,
            transport.PublicKey,
            true,
            NotificationEventTypes.All);

    public async Task<IReadOnlyList<NotificationSubscriptionDto>> ListAsync(CancellationToken ct)
    {
        var userId = UserId;
        var subscriptions = await db.NotificationSubscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.Status == "Active")
            .ThenByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        return subscriptions.Select(Map).ToList();
    }

    public async Task<NotificationSubscriptionDto> UpsertAsync(
        CreateNotificationSubscriptionRequest request,
        CancellationToken ct)
    {
        var userId = UserId;
        var deviceId = NotificationDeviceId.NormalizeRequired(request.DeviceId);
        if (!string.Equals(request.Channel, NotificationChannels.WebPush, StringComparison.Ordinal))
            throw new DomainException(40001, "不支持的推送通道");
        if (!WebPushEndpointValidator.TryValidate(
                request.Endpoint,
                options.Value.AllowedEndpointHosts,
                out var endpoint,
                out var endpointError))
            throw new DomainException(40001, $"推送地址无效：{endpointError}");
        var p256Dh = DecodeKey(request.Keys?.P256Dh, 65, "p256dh");
        var auth = DecodeKey(request.Keys?.Auth, 16, "auth");

        var now = clock.GetUtcNow();
        var endpointHash = HashEndpoint(endpoint.AbsoluteUri);
        for (var attempt = 1; attempt <= MaxUpsertAttempts; attempt++)
        {
            try
            {
                var subscription = await UpsertOnceAsync(
                    userId, deviceId, endpoint, endpointHash, p256Dh, auth, now, ct);
                logger.LogInformation(
                    "Notification subscription {SubscriptionId} upserted",
                    subscription.Id);
                return Map(subscription);
            }
            catch (Exception error) when (IsUniqueViolation(error))
            {
                db.ChangeTracker.Clear();
                if (attempt < MaxUpsertAttempts)
                {
                    // 同一浏览器端点可能被两个并发请求同时插入；唯一键落败方清空跟踪后重试。
                    // 日志只记录账号，不记录 endpoint/hash 等唯一键细节。
                    logger.LogWarning(
                        "Notification subscription upsert hit a unique conflict; retrying for user {UserId}",
                        userId);
                    continue;
                }

                return await ResolveFinalUniqueConflictAsync(userId, endpointHash, ct);
            }
        }

        throw new InvalidOperationException("通知订阅 upsert 重试流程未返回结果");
    }

    private async Task<NotificationSubscriptionDto> ResolveFinalUniqueConflictAsync(
        Guid userId,
        string endpointHash,
        CancellationToken ct)
    {
        var existing = await ResolveExistingAfterUniqueConflictAsync(userId, endpointHash, ct);
        // 同账号并发 upsert 的落败方：另一请求已写入同一 endpoint，按幂等返回现有订阅。
        logger.LogInformation(
            "Notification subscription upsert converged to existing subscription {SubscriptionId}",
            existing.Id);
        return Map(existing);
    }

    private async Task<NotificationSubscriptionEntity> UpsertOnceAsync(
        Guid userId,
        string deviceId,
        Uri endpoint,
        string endpointHash,
        byte[] p256Dh,
        byte[] auth,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var transaction = db.Database.IsNpgsql()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (transaction is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({endpointHash}, 0))",
                ct);
        }

        var existing = await db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                s => s.Channel == NotificationChannels.WebPush && s.EndpointHash == endpointHash,
                ct);

        NotificationSubscriptionEntity subscription;
        if (existing is null)
        {
            subscription = new NotificationSubscriptionEntity
            {
                UserId = userId,
                CreatedAt = now
            };
            if (db.Database.IsNpgsql())
            {
                var inserted = await TryInsertSubscriptionAsync(
                    subscription, userId, deviceId, endpoint, endpointHash, p256Dh, auth, now, ct);
                if (inserted)
                {
                    subscription = await db.NotificationSubscriptions
                        .IgnoreQueryFilters()
                        .SingleOrDefaultAsync(s => s.Id == subscription.Id, ct)
                        ?? throw new DomainException(40906, EndpointOwnershipConflictMessage);
                }
                else
                {
                    subscription = await ResolveExistingAfterUniqueConflictAsync(
                        userId, endpointHash, ct);
                }
            }
            else
            {
                db.NotificationSubscriptions.Add(subscription);
            }
        }
        else
        {
            subscription = existing;
            if (subscription.UserId != userId)
            {
                logger.LogWarning(
                    "Notification subscription endpoint ownership conflict for user {UserId}",
                    userId);
                throw new DomainException(
                    40906,
                    EndpointOwnershipConflictMessage);
            }
        }

        var otherDeviceSubscriptions = await db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId &&
                        s.Channel == NotificationChannels.WebPush &&
                        s.DeviceId == deviceId &&
                        s.Id != subscription.Id &&
                        s.Status == "Active")
            .ToListAsync(ct);
        foreach (var duplicate in otherDeviceSubscriptions)
        {
            duplicate.Status = "Revoked";
            duplicate.RevokedAt = now;
            duplicate.UpdatedAt = now;
            duplicate.Version = Guid.NewGuid();
            await SuppressPendingDeliveriesAsync(duplicate.Id, "subscription_replaced", ct);
        }

        subscription.DeviceId = deviceId;
        subscription.Channel = NotificationChannels.WebPush;
        subscription.EndpointCiphertext = protector.Protect(endpoint.AbsoluteUri);
        subscription.EndpointHash = endpointHash;
        subscription.EndpointHost = endpoint.IdnHost.ToLowerInvariant();
        subscription.P256DhCiphertext = protector.Protect(WebPushCrypto.Base64UrlEncode(p256Dh));
        subscription.AuthCiphertext = protector.Protect(WebPushCrypto.Base64UrlEncode(auth));
        subscription.Status = "Active";
        subscription.RevokedAt = null;
        subscription.LastSeenAt = now;
        subscription.UpdatedAt = now;
        subscription.Version = Guid.NewGuid();
        subscription.LastErrorCode = null;
        subscription.LastErrorMessage = null;

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return subscription;
    }

    private async Task<NotificationSubscriptionEntity> ResolveExistingAfterUniqueConflictAsync(
        Guid userId,
        string endpointHash,
        CancellationToken ct)
    {
        var existing = await db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                s => s.Channel == NotificationChannels.WebPush && s.EndpointHash == endpointHash,
                ct);
        if (existing is not null && existing.UserId == userId)
            return existing;

        // 跨账号冲突，或最终仍无法确认归属：都收敛为安全的 40906，不让 23505 冒泡成 500。
        logger.LogWarning(
            "Notification subscription endpoint ownership conflict for user {UserId}",
            userId);
        throw new DomainException(40906, EndpointOwnershipConflictMessage);
    }

    private async Task<bool> TryInsertSubscriptionAsync(
        NotificationSubscriptionEntity subscription,
        Guid userId,
        string deviceId,
        Uri endpoint,
        string endpointHash,
        byte[] p256Dh,
        byte[] auth,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var endpointCiphertext = protector.Protect(endpoint.AbsoluteUri);
        var p256DhCiphertext = protector.Protect(WebPushCrypto.Base64UrlEncode(p256Dh));
        var authCiphertext = protector.Protect(WebPushCrypto.Base64UrlEncode(auth));
        var endpointHost = endpoint.IdnHost.ToLowerInvariant();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        // 原生 INSERT 不经过 EF SaveChanges，避免并发首插落败时 EF 以 Error 级别
        // 记录含 endpoint_hash 的唯一键异常；ON CONFLICT 让正常并发首插无需抛错。
        await using var command = new NpgsqlCommand("""
            INSERT INTO notification_subscriptions
                (id, user_id, device_id, channel, endpoint_ciphertext, endpoint_hash,
                 endpoint_host, p256dh_ciphertext, auth_ciphertext, status, version,
                 created_at, updated_at, last_seen_at, revoked_at, last_error_code, last_error_message)
            VALUES
                (@id, @user_id, @device_id, @channel, @endpoint_ciphertext, @endpoint_hash,
                 @endpoint_host, @p256dh_ciphertext, @auth_ciphertext, @status, @version,
                 @created_at, @updated_at, @last_seen_at, NULL, NULL, NULL)
            ON CONFLICT (channel, endpoint_hash) DO NOTHING
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", subscription.Id);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("device_id", deviceId);
        command.Parameters.AddWithValue("channel", NotificationChannels.WebPush);
        command.Parameters.AddWithValue("endpoint_ciphertext", endpointCiphertext);
        command.Parameters.AddWithValue("endpoint_hash", endpointHash);
        command.Parameters.AddWithValue("endpoint_host", endpointHost);
        command.Parameters.AddWithValue("p256dh_ciphertext", p256DhCiphertext);
        command.Parameters.AddWithValue("auth_ciphertext", authCiphertext);
        command.Parameters.AddWithValue("status", "Active");
        command.Parameters.AddWithValue("version", subscription.Version);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("last_seen_at", now);
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<NotificationSubscriptionDto?> RevokeAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        var subscription = await db.NotificationSubscriptions
            .SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (subscription is null) return null;

        var now = clock.GetUtcNow();
        if (subscription.Status != "Revoked")
        {
            subscription.Status = "Revoked";
            subscription.RevokedAt = now;
            subscription.UpdatedAt = now;
            subscription.Version = Guid.NewGuid();
            await SuppressPendingDeliveriesAsync(subscription.Id, "subscription_revoked", ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Notification subscription {SubscriptionId} revoked", subscription.Id);
        }
        return Map(subscription);
    }

    public async Task<NotificationSubscriptionRevokeResponse> RevokeCurrentAsync(
        RevokeCurrentNotificationSubscriptionRequest request,
        CancellationToken ct)
    {
        var userId = UserId;
        var deviceId = NotificationDeviceId.NormalizeOptional(request.DeviceId);
        var endpointHash = NormalizeEndpointHash(request.Endpoint);
        if (deviceId.Length == 0 && endpointHash is null)
            throw new DomainException(40001, "缺少设备标识或推送地址");

        var query = db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId &&
                        s.Channel == NotificationChannels.WebPush &&
                        s.Status == "Active");
        if (deviceId.Length > 0)
            query = query.Where(s => s.DeviceId == deviceId);
        if (endpointHash is not null)
            query = query.Where(s => s.EndpointHash == endpointHash);

        var subscriptions = await query
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (subscriptions.Count == 0)
            return new NotificationSubscriptionRevokeResponse(0);

        var now = clock.GetUtcNow();
        var revoked = 0;
        if (db.Database.IsRelational())
        {
            foreach (var subscriptionId in subscriptions)
            {
                var version = Guid.NewGuid();
                var updated = await db.NotificationSubscriptions
                    .IgnoreQueryFilters()
                    .Where(s => s.Id == subscriptionId &&
                                s.UserId == userId &&
                                s.Status == "Active")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, "Revoked")
                        .SetProperty(s => s.RevokedAt, now)
                        .SetProperty(s => s.UpdatedAt, now)
                        .SetProperty(s => s.Version, version), ct);
                if (updated == 0) continue;

                await SuppressPendingDeliveriesAsync(
                    subscriptionId, "logout_revoked", ct);
                revoked += updated;
            }
        }
        else
        {
            var entities = await db.NotificationSubscriptions
                .IgnoreQueryFilters()
                .Where(s => subscriptions.Contains(s.Id) && s.Status == "Active")
                .ToListAsync(ct);
            foreach (var subscription in entities)
            {
                subscription.Status = "Revoked";
                subscription.RevokedAt = now;
                subscription.UpdatedAt = now;
                subscription.Version = Guid.NewGuid();
                await SuppressPendingDeliveriesAsync(
                    subscription.Id, "logout_revoked", ct);
                revoked += 1;
            }
            if (revoked > 0)
                await db.SaveChangesAsync(ct);
        }

        if (revoked > 0)
        {
            logger.LogInformation(
                "Revoked {Count} browser notification subscriptions for user {UserId} at logout",
                revoked,
                userId);
        }
        return new NotificationSubscriptionRevokeResponse(revoked);
    }

    private static string? NormalizeEndpointHash(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;
        if (endpoint.Length > 2048 ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new DomainException(40001, "推送地址无效");
        }

        return HashEndpoint(parsed.AbsoluteUri);
    }

    private async Task SuppressPendingDeliveriesAsync(
        Guid subscriptionId,
        string errorCode,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (db.Database.IsRelational())
        {
            await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .Where(d => d.SubscriptionId == subscriptionId &&
                            (d.Status == NotificationDeliveryStatuses.Pending ||
                             d.Status == NotificationDeliveryStatuses.Retrying ||
                             d.Status == NotificationDeliveryStatuses.Processing))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Status, NotificationDeliveryStatuses.Suppressed)
                    .SetProperty(d => d.LastErrorCode, errorCode)
                    .SetProperty(d => d.LastErrorMessage, "推送订阅已撤销或转移，未发送")
                    .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(d => d.LockedBy, (string?)null)
                    .SetProperty(d => d.UpdatedAt, now), ct);
            return;
        }

        var deliveries = await db.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.SubscriptionId == subscriptionId &&
                        (d.Status == NotificationDeliveryStatuses.Pending ||
                         d.Status == NotificationDeliveryStatuses.Retrying ||
                         d.Status == NotificationDeliveryStatuses.Processing))
            .ToListAsync(ct);
        foreach (var delivery in deliveries)
        {
            delivery.Status = NotificationDeliveryStatuses.Suppressed;
            delivery.LastErrorCode = errorCode;
            delivery.LastErrorMessage = "推送订阅已撤销或转移，未发送";
            delivery.LockedUntil = null;
            delivery.LockedBy = null;
            delivery.UpdatedAt = now;
        }
    }

    private static NotificationSubscriptionDto Map(NotificationSubscriptionEntity entity) =>
        new(
            entity.Id,
            entity.DeviceId,
            entity.Channel,
            entity.EndpointHost,
            entity.Status,
            entity.CreatedAt,
            entity.LastSeenAt,
            entity.RevokedAt);

    private static byte[] DecodeKey(string? value, int expectedLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new DomainException(40001, $"推送订阅密钥 {name} 无效");
        try
        {
            var bytes = WebPushCrypto.Base64UrlDecode(value);
            if (bytes.Length != expectedLength)
                throw new DomainException(40001, $"推送订阅密钥 {name} 长度无效");
            return bytes;
        }
        catch (FormatException)
        {
            throw new DomainException(40001, $"推送订阅密钥 {name} 编码无效");
        }
    }

    private static string HashEndpoint(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();

    private static bool IsUniqueViolation(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "23505" }) return true;
        }
        return false;
    }
}
