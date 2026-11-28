using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Exceptions;
using Pim.Core.Notifications;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notifications;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notifications;

[Trait("DataSource", "RealDb")]
public sealed class NotificationConcurrencyRegressionTests
{
    [SkippableFact]
    public async Task ManualRetry_DoesNotOverwriteProcessing_WhenWorkerClaimsBetweenReadAndWrite()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        var deliveryId = await PrepareDeliveryAsync(temp, clock, user, fake);
        await SetDeadLetterAsync(temp.ConnectionString, deliveryId);

        var interceptor = new MutateAfterDeliveryReadInterceptor(
            temp.ConnectionString,
            $"""
             UPDATE notification_deliveries
             SET status = 'Processing',
                 locked_until = now() + interval '5 minutes',
                 locked_by = 'worker-race'
             WHERE id = '{deliveryId:D}'
             """);
        await using var db = NewDbContext(temp.ConnectionString, interceptor);
        var query = new NotificationQueryService(db, new FixedCurrentUser(user), clock);

        var result = await query.RetryDeliveryAsync(deliveryId, CancellationToken.None);

        Assert.Equal(NotificationDeliveryStatuses.Processing, result.Status);
        var stored = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Processing, stored.Status);
        Assert.Equal(3, stored.AttemptCount);
        Assert.Equal("worker-race", stored.LockedBy);
        Assert.Equal(1, interceptor.Mutations);
    }

    [SkippableFact]
    public async Task ManualRetry_DoesNotOverwriteDelivered_WhenDeliveryCompletesBetweenReadAndWrite()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        var deliveryId = await PrepareDeliveryAsync(temp, clock, user, fake);
        await SetDeadLetterAsync(temp.ConnectionString, deliveryId);

        var deliveredAt = clock.UtcNow.AddSeconds(1);
        var interceptor = new MutateAfterDeliveryReadInterceptor(
            temp.ConnectionString,
            $"""
             UPDATE notification_deliveries
             SET status = 'Delivered',
                 delivered_at = '{deliveredAt:O}',
                 attempt_count = 1
             WHERE id = '{deliveryId:D}'
             """);
        await using var db = NewDbContext(temp.ConnectionString, interceptor);
        var query = new NotificationQueryService(db, new FixedCurrentUser(user), clock);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => query.RetryDeliveryAsync(deliveryId, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        var stored = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Delivered, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(deliveredAt, stored.DeliveredAt);
        Assert.Equal(1, interceptor.Mutations);
    }

    [SkippableFact]
    public async Task DeviceRebind_LocksDuplicateSubscriptionsBeforeSuppressingTheirDeliveries()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        var now = clock.UtcNow;
        var duplicateIds = new List<Guid>();
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            for (var index = 0; index < 2; index++)
            {
                var endpoint = $"https://fcm.googleapis.com/wp/rebind-{index}-{Guid.NewGuid():N}";
                var subscription = new NotificationSubscriptionEntity
                {
                    UserId = user,
                    DeviceId = "device-rebind",
                    Channel = NotificationChannels.WebPush,
                    EndpointCiphertext = "cipher",
                    EndpointHash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant(),
                    EndpointHost = "fcm.googleapis.com",
                    P256DhCiphertext = "cipher",
                    AuthCiphertext = "cipher",
                    Status = "Active",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                var notification = new NotificationEventEntity
                {
                    UserId = user,
                    EventType = NotificationEventTypes.TaskCompleted,
                    DedupeKey = $"rebind-{index}-{Guid.NewGuid():N}",
                    Title = "换绑回归",
                    Body = "body",
                    InAppVisible = true,
                    PushEligible = true,
                    FanoutStatus = "Completed",
                    NextFanoutAt = now,
                    CreatedAt = now,
                    FanoutCompletedAt = now
                };
                db.NotificationSubscriptions.Add(subscription);
                db.NotificationEvents.Add(notification);
                db.NotificationDeliveries.Add(new NotificationDeliveryEntity
                {
                    EventId = notification.Id,
                    Event = notification,
                    SubscriptionId = subscription.Id,
                    UserId = user,
                    Channel = NotificationChannels.WebPush,
                    DeviceId = subscription.DeviceId,
                    Status = NotificationDeliveryStatuses.Pending,
                    NextAttemptAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                duplicateIds.Add(subscription.Id);
            }
            await db.SaveChangesAsync();
        }

        var interceptor = new RecordingCommandInterceptor();
        await using (var db = NewDbContext(temp.ConnectionString, interceptor))
        {
            var service = new NotificationSubscriptionService(
                db,
                new FixedCurrentUser(user),
                NotificationTestSupport.Secrets,
                fake,
                Options.Create(new WebPushOptions
                {
                    AllowedEndpointHosts = ["fcm.googleapis.com"]
                }),
                clock,
                NullLogger<NotificationSubscriptionService>.Instance);
            var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();
            var result = await service.UpsertAsync(
                new CreateNotificationSubscriptionRequest(
                    "device-rebind",
                    NotificationChannels.WebPush,
                    $"https://fcm.googleapis.com/wp/new-{Guid.NewGuid():N}",
                    new NotificationSubscriptionKeysDto(p256Dh, auth)),
                CancellationToken.None);
            Assert.Equal("Active", result.Status);
        }

        var lockIndex = interceptor.Commands.FindIndex(command =>
            command.Contains("FROM notification_subscriptions", StringComparison.Ordinal) &&
            command.Contains("FOR UPDATE", StringComparison.Ordinal));
        var suppressIndex = interceptor.Commands.FindIndex(command =>
            command.Contains("UPDATE notification_deliveries", StringComparison.Ordinal));
        Assert.True(lockIndex >= 0, "换绑必须在抑制投递前显式锁定 duplicate subscription");
        Assert.True(suppressIndex >= 0, "换绑必须抑制 duplicate subscription 的未发送投递");
        Assert.True(
            lockIndex < suppressIndex,
            $"锁顺序必须是 subscription → delivery，实际 lock={lockIndex} suppress={suppressIndex}");

        await using var verify = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        var duplicates = await verify.NotificationSubscriptions
            .IgnoreQueryFilters()
            .Where(s => duplicateIds.Contains(s.Id))
            .ToListAsync();
        Assert.All(duplicates, subscription =>
            Assert.Equal("Revoked", subscription.Status));
        var deliveries = await verify.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => duplicateIds.Contains(d.SubscriptionId))
            .ToListAsync();
        Assert.All(deliveries, delivery =>
            Assert.Equal(NotificationDeliveryStatuses.Suppressed, delivery.Status));
    }

    private static PimDbContext NewDbContext(
        string connectionString,
        DbCommandInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptor)
            .Options);

    private static async Task SetDeadLetterAsync(string connectionString, Guid deliveryId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE notification_deliveries
            SET status = 'DeadLetter',
                attempt_count = 3,
                dead_lettered_at = now(),
                last_error_code = 'retry_exhausted',
                last_error_message = '多次重试仍未送达，已进入死信'
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", deliveryId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<(TempMigrationDatabase Temp, ManualTimeProvider Clock, Guid User, FakePushTransport Fake)>
        NewDatabaseAsync()
    {
        PimDbContext.RegisterModuleAssembly(
            typeof(Pim.Module.Calendar.Entities.ReminderEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync("test_notification_concurrency");
        await temp.MigrateAsync();
        return (
            temp,
            new ManualTimeProvider(DateTimeOffset.Parse("2026-11-28T00:00:00Z")),
            Guid.NewGuid(),
            new FakePushTransport());
    }

    private static async Task<Guid> PrepareDeliveryAsync(
        TempMigrationDatabase temp,
        ManualTimeProvider clock,
        Guid user,
        FakePushTransport fake)
    {
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            _ = await NotificationTestSupport.SubscribeAsync(
                db, user, fake, clock, deviceId: "device-retry-race");
            Assert.Equal(
                NotificationEnqueueOutcome.Enqueued,
                await NotificationTestSupport.NewWriter(db, clock).TryEnqueueAsync(
                    user,
                    NotificationEventDrafts.TaskCompleted(
                        Guid.NewGuid(), clock.UtcNow, "重试竞态"),
                    CancellationToken.None));
            await NotificationTestSupport.NewWorker(db, fake, clock)
                .FanoutOnceAsync(CancellationToken.None);
        }

        await using var read = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        return await read.NotificationDeliveries.IgnoreQueryFilters()
            .Select(d => d.Id)
            .SingleAsync();
    }

    private sealed class MutateAfterDeliveryReadInterceptor(
        string connectionString,
        string mutationSql) : DbCommandInterceptor
    {
        private int _pending = 1;
        public int Mutations { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "FROM notification_deliveries",
                    StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _pending, 0) == 1)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var update = new NpgsqlCommand(mutationSql, connection);
                Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
                Mutations++;
            }
            return result;
        }
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
