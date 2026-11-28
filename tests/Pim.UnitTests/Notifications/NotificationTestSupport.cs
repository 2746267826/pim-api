using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Notifications;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notifications;
using Pim.Infrastructure.Secrets;

namespace Pim.UnitTests.Notifications;

internal sealed class FixedCurrentUser(Guid? userId) : ICurrentUserService
{
    public Guid? UserId { get; } = userId;
    public string? Role => "user";
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan span) => UtcNow = UtcNow.Add(span);
}

internal sealed class FakePushTransport : IWebPushTransport
{
    public bool IsConfigured { get; set; } = true;
    public string? PublicKey => "test-public-key";
    public ConcurrentQueue<PushSendResult> Results { get; } = new();
    public Func<PushSubscriptionSecret, PushNotificationMessage, CancellationToken, Task<PushSendResult>>? Handler { get; set; }
    public int Calls;

    public Task<PushSendResult> SendAsync(
        PushSubscriptionSecret subscription,
        PushNotificationMessage message,
        CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Handler is not null) return Handler(subscription, message, ct);
        if (Results.TryDequeue(out var result)) return Task.FromResult(result);
        return Task.FromResult(PushSendResult.Delivered());
    }
}

internal static class NotificationTestSupport
{
    private static readonly ISecretProtector Protector =
        new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());

    public static PimDbContext NewDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    public static ISecretProtector Secrets => Protector;

    public static NotificationDeliveryWorker NewWorker(
        PimDbContext db,
        IWebPushTransport transport,
        ManualTimeProvider clock,
        int maxAttempts = 3,
        int baseDelaySeconds = 0,
        int providerTimeoutSeconds = 15,
        int deliveryBatchSize = 20,
        int lockSeconds = 120,
        ISecretProtector? protector = null)
    {
        var options = Options.Create(new NotificationDispatcherOptions
        {
            MaxAttempts = maxAttempts,
            BaseDelaySeconds = baseDelaySeconds,
            MaxDelaySeconds = 3600,
            DeliveryBatchSize = deliveryBatchSize,
            FanoutBatchSize = 50,
            ProviderTimeoutSeconds = providerTimeoutSeconds,
            LockSeconds = lockSeconds
        });
        return new NotificationDeliveryWorker(
            db,
            transport,
            protector ?? Protector,
            options,
            clock,
            NullLogger<NotificationDeliveryWorker>.Instance);
    }

    public static NotificationEventWriter NewWriter(
        PimDbContext db,
        ManualTimeProvider clock) =>
        new(db, clock, NullLogger<NotificationEventWriter>.Instance);

    public static async Task<NotificationSubscriptionDto> SubscribeAsync(
        PimDbContext db,
        Guid userId,
        IWebPushTransport transport,
        ManualTimeProvider clock,
        string? endpoint = null,
        string? deviceId = null,
        string? endpointHost = null)
    {
        var service = new NotificationSubscriptionService(
            db,
            new FixedCurrentUser(userId),
            Protector,
            transport,
            Options.Create(new WebPushOptions
            {
                AllowedEndpointHosts = [endpointHost ?? "fcm.googleapis.com"]
            }),
            clock,
            NullLogger<NotificationSubscriptionService>.Instance);
        var (p256Dh, auth) = SubscriptionKeys();
        return await service.UpsertAsync(
            new CreateNotificationSubscriptionRequest(
                deviceId ?? $"device-{Guid.NewGuid():N}",
                NotificationChannels.WebPush,
                endpoint ?? $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}",
                new NotificationSubscriptionKeysDto(p256Dh, auth)),
            CancellationToken.None);
    }

    public static (string P256Dh, string Auth) SubscriptionKeys()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdh.ExportParameters(false);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (Base64Url(publicKey), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    public static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<NotificationDeliveryEntity> ReadDeliveryAsync(
        string connectionString,
        Guid deliveryId)
    {
        await using var db = NewDbContext(connectionString);
        return await db.NotificationDeliveries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(d => d.Id == deliveryId);
    }
}
