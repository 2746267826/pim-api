using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Notifications;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Notifications;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notifications;

[Trait("DataSource", "RealDb")]
public sealed class NotificationDeliveryRealDbTests
{
    [SkippableFact]
    public async Task EventAndSubscription_AreIdempotent_AndCiphertextDoesNotLeakSecrets()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var databaseScope = temp;
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            _ = await NotificationTestSupport.SubscribeAsync(
                db, user, fake, clock, endpoint, "device-idempotent");
            _ = await NotificationTestSupport.SubscribeAsync(
                db, user, fake, clock, endpoint, "device-idempotent");
        }

        var draft = NotificationEventDrafts.TaskCompleted(
            Guid.NewGuid(), clock.UtcNow, "幂等任务");
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var writer = NotificationTestSupport.NewWriter(db, clock);
            Assert.Equal(
                NotificationEnqueueOutcome.Enqueued,
                await writer.TryEnqueueAsync(user, draft, CancellationToken.None));
            Assert.Equal(
                NotificationEnqueueOutcome.Duplicate,
                await writer.TryEnqueueAsync(user, draft, CancellationToken.None));
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            Assert.Equal(1, await db.NotificationSubscriptions.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await db.NotificationEvents.IgnoreQueryFilters().CountAsync());
            Assert.True(await db.NotificationEvents.IgnoreQueryFilters()
                .Select(e => e.Sequence).SingleAsync() > 0);

            var worker = NotificationTestSupport.NewWorker(db, fake, clock);
            await worker.FanoutOnceAsync(CancellationToken.None);
            await worker.FanoutOnceAsync(CancellationToken.None);
            Assert.Equal(1, await db.NotificationDeliveries.IgnoreQueryFilters().CountAsync());
        }

        await using var raw = new Npgsql.NpgsqlConnection(temp.ConnectionString);
        await raw.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT endpoint_ciphertext, p256dh_ciphertext, auth_ciphertext FROM notification_subscriptions",
            raw);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var combined = $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}";
        Assert.DoesNotContain(endpoint, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", combined, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RetryableFailure_BacksOff_ThenDelivers()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        fake.Results.Enqueue(PushSendResult.Retryable("test_retry"));
        fake.Results.Enqueue(PushSendResult.Retryable("test_retry"));
        fake.Results.Enqueue(PushSendResult.Delivered());
        var deliveryId = await PrepareOneDeliveryAsync(temp, clock, user, fake);

        await using var db = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        var worker = NotificationTestSupport.NewWorker(
            db, fake, clock, maxAttempts: 3, baseDelaySeconds: 60);
        await worker.DeliverOnceAsync(CancellationToken.None);

        var first = await NotificationTestSupport.ReadDeliveryAsync(temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Retrying, first.Status);
        Assert.True(first.NextAttemptAt >= clock.UtcNow.AddSeconds(50));

        clock.Advance(TimeSpan.FromMinutes(5));
        await worker.DeliverOnceAsync(CancellationToken.None);
        assertRetryingOrDelivered(await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId));

        clock.Advance(TimeSpan.FromMinutes(5));
        await worker.DeliverOnceAsync(CancellationToken.None);
        var final = await NotificationTestSupport.ReadDeliveryAsync(temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Delivered, final.Status);
        Assert.Equal(3, final.AttemptCount);
        Assert.Equal(3, fake.Calls);

        static void assertRetryingOrDelivered(NotificationDeliveryEntity delivery) =>
            Assert.True(delivery.Status is NotificationDeliveryStatuses.Retrying
                or NotificationDeliveryStatuses.Delivered);
    }

    [SkippableFact]
    public async Task ExhaustedRetries_DeadLetter_ManualRetryRecovers()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        fake.Results.Enqueue(PushSendResult.Retryable("test_retry"));
        fake.Results.Enqueue(PushSendResult.Retryable("test_retry"));
        var deliveryId = await PrepareOneDeliveryAsync(temp, clock, user, fake);

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var worker = NotificationTestSupport.NewWorker(db, fake, clock, maxAttempts: 2);
            await worker.DeliverOnceAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
            await worker.DeliverOnceAsync(CancellationToken.None);
        }
        var dead = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.DeadLetter, dead.Status);
        Assert.Equal("retry_exhausted", dead.LastErrorCode);
        Assert.NotNull(dead.DeadLetteredAt);
        Assert.Equal("多次重试仍未送达，已进入死信", dead.LastErrorMessage);

        fake.Results.Enqueue(PushSendResult.Delivered());
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var query = new NotificationQueryService(
                db, new FixedCurrentUser(user), clock);
            var retried = await query.RetryDeliveryAsync(deliveryId, CancellationToken.None);
            Assert.Equal(NotificationDeliveryStatuses.Pending, retried.Status);
            Assert.Equal(0, retried.AttemptCount);
            Assert.Null(retried.LastErrorMessage);
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var worker = NotificationTestSupport.NewWorker(db, fake, clock, maxAttempts: 2);
            await worker.DeliverOnceAsync(CancellationToken.None);
        }
        var delivered = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Delivered, delivered.Status);
        Assert.Equal(1, delivered.AttemptCount);
        Assert.Null(delivered.DeadLetteredAt);
        Assert.Equal(3, fake.Calls);
    }

    [SkippableFact]
    public async Task ConcurrentWorkers_ClaimDeliveryOnlyOnce()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        var deliveryId = await PrepareOneDeliveryAsync(temp, clock, user, fake);
        fake.Handler = async (_, _, ct) =>
        {
            await Task.Delay(250, ct);
            return PushSendResult.Delivered();
        };

        await using var firstDb = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        await using var secondDb = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        var first = NotificationTestSupport.NewWorker(firstDb, fake, clock);
        var second = NotificationTestSupport.NewWorker(secondDb, fake, clock);
        await Task.WhenAll(
            first.DeliverOnceAsync(CancellationToken.None),
            second.DeliverOnceAsync(CancellationToken.None));

        Assert.Equal(1, fake.Calls);
        Assert.Equal(
            NotificationDeliveryStatuses.Delivered,
            (await NotificationTestSupport.ReadDeliveryAsync(
                temp.ConnectionString, deliveryId)).Status);
    }

    [SkippableFact]
    public async Task Revocation_SuppressesPendingDelivery_WithoutSending()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        var deliveryId = await PrepareOneDeliveryAsync(temp, clock, user, fake);

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var subscriptions = new NotificationSubscriptionService(
                db,
                new FixedCurrentUser(user),
                NotificationTestSupport.Secrets,
                fake,
                Microsoft.Extensions.Options.Options.Create(new WebPushOptions
                {
                    AllowedEndpointHosts = ["fcm.googleapis.com"]
                }),
                clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationSubscriptionService>.Instance);
            var list = await subscriptions.ListAsync(CancellationToken.None);
            var revoked = await subscriptions.RevokeAsync(list.Single().Id, CancellationToken.None);
            Assert.NotNull(revoked);
            Assert.Equal("Revoked", revoked!.Status);
            Assert.NotNull(await subscriptions.RevokeAsync(list.Single().Id, CancellationToken.None));
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var worker = NotificationTestSupport.NewWorker(db, fake, clock);
            await worker.DeliverOnceAsync(CancellationToken.None);
        }

        var delivery = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Suppressed, delivery.Status);
        Assert.Equal("subscription_revoked", delivery.LastErrorCode);
        Assert.Equal(0, fake.Calls);
    }

    [SkippableFact]
    public async Task MissingProvider_SkipsPush_ButKeepsInAppNotification()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        fake.IsConfigured = false;
        var deliveryId = await PrepareOneDeliveryAsync(temp, clock, user, fake);

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var query = new NotificationQueryService(db, new FixedCurrentUser(user), clock);
            var inApp = await query.ListAsync(20, null, CancellationToken.None);
            Assert.Single(inApp.Items);
            Assert.Equal(1, inApp.UnreadCount);

            var worker = NotificationTestSupport.NewWorker(db, fake, clock);
            await worker.DeliverOnceAsync(CancellationToken.None);
        }

        var delivery = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.Skipped, delivery.Status);
        Assert.Equal("provider_not_configured", delivery.LastErrorCode);
        Assert.Equal(0, fake.Calls);
    }

    [SkippableFact]
    public async Task DevicePreference_SuppressesOnlyThatDevicesPush()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var databaseScope = temp;
        Guid deviceBSubscriptionId;

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            _ = await NotificationTestSupport.SubscribeAsync(
                db, user, fake, clock, deviceId: "device-a");
            deviceBSubscriptionId = (await NotificationTestSupport.SubscribeAsync(
                db, user, fake, clock, deviceId: "device-b")).Id;

            var preferences = new NotificationPreferenceService(
                db, new FixedCurrentUser(user), clock);
            var updated = await preferences.UpdateAsync(
                new UpdateNotificationPreferencesRequest(null, null, false, null, null, null),
                "device-a",
                CancellationToken.None);
            Assert.False(updated.TaskCompleted);
            Assert.True((await preferences.GetAsync("device-b", CancellationToken.None))
                .TaskCompleted);
            Assert.True((await preferences.GetAsync(null, CancellationToken.None))
                .TaskCompleted);
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            Assert.Equal(
                NotificationEnqueueOutcome.Enqueued,
                await NotificationTestSupport.NewWriter(db, clock).TryEnqueueAsync(
                    user,
                    NotificationEventDrafts.TaskCompleted(
                        Guid.NewGuid(), clock.UtcNow, "设备偏好任务"),
                    CancellationToken.None));

            // 事件创建时 A 已关闭；即使在 fanout 前重新打开，也不能补推关闭期间产生的旧事件。
            var preferences = new NotificationPreferenceService(
                db, new FixedCurrentUser(user), clock);
            _ = await preferences.UpdateAsync(
                new UpdateNotificationPreferencesRequest(null, null, true, null, null, null),
                "device-a",
                CancellationToken.None);

            await NotificationTestSupport.NewWorker(db, fake, clock)
                .FanoutOnceAsync(CancellationToken.None);
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var delivery = Assert.Single(await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .ToListAsync());
            Assert.Equal(deviceBSubscriptionId, delivery.SubscriptionId);
        }
        Assert.Equal(0, fake.Calls);
    }

    [SkippableFact]
    public async Task CategoryDisabledAtCreation_IsPersistedSuppressed_AndDoesNotReplay()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var databaseScope = temp;
        var draft = NotificationEventDrafts.TaskCompleted(
            Guid.NewGuid(), clock.UtcNow, "关闭期间完成的任务");

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var preferences = new NotificationPreferenceService(
                db, new FixedCurrentUser(user), clock);
            _ = await preferences.UpdateAsync(
                new UpdateNotificationPreferencesRequest(null, null, false, null, null, null),
                null,
                CancellationToken.None);
            Assert.Equal(
                NotificationEnqueueOutcome.Disabled,
                await NotificationTestSupport.NewWriter(db, clock)
                    .TryEnqueueAsync(user, draft, CancellationToken.None));
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var stored = Assert.Single(await db.NotificationEvents
                .IgnoreQueryFilters()
                .Where(e => e.UserId == user)
                .ToListAsync());
            Assert.False(stored.InAppVisible);
            Assert.False(stored.PushEligible);
            Assert.Equal("Completed", stored.FanoutStatus);

            var preferences = new NotificationPreferenceService(
                db, new FixedCurrentUser(user), clock);
            _ = await preferences.UpdateAsync(
                new UpdateNotificationPreferencesRequest(null, null, true, null, null, null),
                null,
                CancellationToken.None);
            Assert.Equal(
                NotificationEnqueueOutcome.Duplicate,
                await NotificationTestSupport.NewWriter(db, clock)
                    .TryEnqueueAsync(user, draft, CancellationToken.None));
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            Assert.Equal(1, await db.NotificationEvents.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, fake.Calls);
    }

    [SkippableFact]
    public async Task LastError_IsSafe_AndNeverPersistsProviderSecretText()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        fake.Handler = (_, _, _) => Task.FromResult(
            new PushSendResult(
                PushSendOutcome.PermanentFailure,
                "provider_rejected",
                "RAW-SECRET-SHOULD-NOT-PERSIST"));
        var deliveryId = await PrepareOneDeliveryAsync(temp, clock, user, fake);

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            await NotificationTestSupport.NewWorker(db, fake, clock)
                .DeliverOnceAsync(CancellationToken.None);
        }

        var delivery = await NotificationTestSupport.ReadDeliveryAsync(
            temp.ConnectionString, deliveryId);
        Assert.Equal(NotificationDeliveryStatuses.DeadLetter, delivery.Status);
        Assert.Equal("provider_rejected", delivery.LastErrorCode);
        Assert.DoesNotContain("RAW-SECRET", delivery.LastErrorMessage, StringComparison.Ordinal);
        Assert.Equal("推送服务拒绝了该条通知", delivery.LastErrorMessage);
    }

    [SkippableFact]
    public async Task DueReminder_EnqueuesOnce_AndMarksScheduledOccurrence()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        var reminder = new ReminderEntity
        {
            UserId = user,
            RelatedObjectType = "event",
            RelatedObjectId = Guid.NewGuid(),
            Title = "喝水",
            Body = "休息一下",
            TriggerReason = "scheduled",
            ScheduledAt = clock.UtcNow.AddMinutes(-1),
            Status = "Open"
        };
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            db.Set<ReminderEntity>().Add(reminder);
            await db.SaveChangesAsync();
        }

        var storage = Path.Combine(Path.GetTempPath(), $"pim-notification-notes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
        try
        {
            await using var db = NotificationTestSupport.NewDbContext(temp.ConnectionString);
            var scanner = new NotificationEventScanner(
                db,
                NotificationTestSupport.NewWriter(db, clock),
                new NoteAsrTaskStore(Options.Create(new NoteMaterialUploadOptions { StorageRoot = storage })),
                new NoteDerivedStore(
                    Options.Create(new NoteMaterialUploadOptions { StorageRoot = storage }),
                    clock),
                Options.Create(new NoteMaterialUploadOptions { StorageRoot = storage }),
                clock,
                NullLogger<NotificationEventScanner>.Instance);

            await scanner.ScanDueRemindersAsync(CancellationToken.None);
            await scanner.ScanDueRemindersAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var events = await db.NotificationEvents.IgnoreQueryFilters()
                .Where(e => e.UserId == user).ToListAsync();
            var notification = Assert.Single(events);
            Assert.Equal(NotificationEventTypes.CalendarReminderDue, notification.EventType);
            Assert.Equal("提醒：喝水", notification.Title);
            Assert.Equal<DateTimeOffset?>(
                reminder.ScheduledAt,
                (await db.Set<ReminderEntity>().IgnoreQueryFilters()
                    .SingleAsync(r => r.Id == reminder.Id)).NotifiedScheduledAt);
        }
    }

    [SkippableFact]
    public async Task CompletedTaskCreation_EnqueuesTaskCompletedNotification()
    {
        var (temp, clock, user, fake) = await NewDatabaseAsync();
        await using var _ = temp;
        await using var db = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        var service = new CalendarService(
            db,
            new FixedCurrentUser(user),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance),
            new EventAttachmentService(db),
            clock,
            null,
            NullLogger<CalendarService>.Instance,
            NotificationTestSupport.NewWriter(db, clock));

        await service.CreateTaskAsync(
            new CreateTaskRequest(
                null,
                "写周报",
                null,
                1,
                null,
                null,
                null,
                null,
                TaskStatuses.Completed),
            CancellationToken.None);

        var notification = Assert.Single(await db.NotificationEvents
            .IgnoreQueryFilters()
            .Where(e => e.UserId == user)
            .ToListAsync());
        Assert.Equal(NotificationEventTypes.TaskCompleted, notification.EventType);
        Assert.Equal("任务已完成：写周报", notification.Title);
        Assert.Equal("/tasks", notification.DetailUrl);
        Assert.True(notification.InAppVisible);
        Assert.True(notification.PushEligible);
        Assert.Equal(0, fake.Calls);
    }

    private async Task<(TempMigrationDatabase Temp, ManualTimeProvider Clock, Guid User, FakePushTransport Fake)>
        NewDatabaseAsync()
    {
        Pim.Infrastructure.Data.PimDbContext.RegisterModuleAssembly(
            typeof(Pim.Module.Calendar.Entities.ReminderEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync("test_notification_delivery");
        await temp.MigrateAsync();
        return (temp, new ManualTimeProvider(DateTimeOffset.Parse("2026-11-26T00:00:00Z")),
            Guid.NewGuid(), new FakePushTransport());
    }

    private static async Task<Guid> PrepareOneDeliveryAsync(
        TempMigrationDatabase temp,
        ManualTimeProvider clock,
        Guid user,
        FakePushTransport fake)
    {
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            _ = await NotificationTestSupport.SubscribeAsync(
                db, user, fake, clock, deviceId: "device-delivery");
            var writer = NotificationTestSupport.NewWriter(db, clock);
            Assert.Equal(
                NotificationEnqueueOutcome.Enqueued,
                await writer.TryEnqueueAsync(
                    user,
                    NotificationEventDrafts.TaskCompleted(
                        Guid.NewGuid(), clock.UtcNow, "投递任务"),
                    CancellationToken.None));
            await NotificationTestSupport.NewWorker(db, fake, clock)
                .FanoutOnceAsync(CancellationToken.None);
        }

        await using var read = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        return await read.NotificationDeliveries.IgnoreQueryFilters()
            .Select(d => d.Id).SingleAsync();
    }
}
