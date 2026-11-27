using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Notifications;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notifications;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notifications;

[Trait("DataSource", "RealDb")]
public sealed class NotificationEndpointsE2ETests
{
    [SkippableFact]
    public async Task Preferences_ArePersistedAndPartialUpdatesKeepOtherValues()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var user = Guid.NewGuid();
        var client = Authed(factory, user);

        var defaults = await ReadDataAsync(await client.GetAsync("/api/v1/notifications/preferences"));
        Assert.True(defaults.GetProperty("inAppEnabled").GetBoolean());
        Assert.True(defaults.GetProperty("pushEnabled").GetBoolean());

        var updated = await ReadDataAsync(await client.PutAsync(
            "/api/v1/notifications/preferences",
            Json("""{"taskCompleted":false,"calendarReminder":false}""")));
        Assert.False(updated.GetProperty("taskCompleted").GetBoolean());
        Assert.False(updated.GetProperty("calendarReminder").GetBoolean());
        Assert.True(updated.GetProperty("pushEnabled").GetBoolean());

        var reread = await ReadDataAsync(await client.GetAsync("/api/v1/notifications/preferences"));
        Assert.False(reread.GetProperty("taskCompleted").GetBoolean());
        Assert.False(reread.GetProperty("calendarReminder").GetBoolean());
    }

    [SkippableFact]
    public async Task DevicePreferences_AreScopedToTheRequestingDevice()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory, Guid.NewGuid());

        var updated = await ReadDataAsync(await SendWithDeviceAsync(
            client,
            HttpMethod.Put,
            "/api/v1/notifications/preferences",
            "browser-a",
            """{"taskCompleted":false}"""));
        Assert.False(updated.GetProperty("taskCompleted").GetBoolean());
        Assert.True(updated.GetProperty("inAppEnabled").GetBoolean());

        var deviceA = await ReadDataAsync(await SendWithDeviceAsync(
            client,
            HttpMethod.Get,
            "/api/v1/notifications/preferences",
            "browser-a"));
        var deviceB = await ReadDataAsync(await SendWithDeviceAsync(
            client,
            HttpMethod.Get,
            "/api/v1/notifications/preferences",
            "browser-b"));
        var account = await ReadDataAsync(await client.GetAsync(
            "/api/v1/notifications/preferences"));

        Assert.False(deviceA.GetProperty("taskCompleted").GetBoolean());
        Assert.True(deviceB.GetProperty("taskCompleted").GetBoolean());
        Assert.True(account.GetProperty("taskCompleted").GetBoolean());
    }

    [SkippableFact]
    public async Task Subscription_UpsertIsIdempotent_AndRevocationIsUserScoped()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var ownerId = Guid.NewGuid();
        var owner = Authed(factory, ownerId);
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();
        var body = JsonSerializer.Serialize(new
        {
            deviceId = "browser-a",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = p256Dh, auth }
        });

        var first = await ReadDataAsync(await owner.PostAsync(
            "/api/v1/notifications/subscriptions", Json(body)));
        var second = await ReadDataAsync(await owner.PostAsync(
            "/api/v1/notifications/subscriptions", Json(body)));
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        var subscriptionId = first.GetProperty("id").GetGuid();
        Assert.Equal("fcm.googleapis.com", first.GetProperty("endpointHost").GetString());

        var subscriptions = await ReadDataAsync(
            await owner.GetAsync("/api/v1/notifications/subscriptions"));
        Assert.Equal(1, subscriptions.GetArrayLength());

        var stranger = Authed(factory, Guid.NewGuid());
        Assert.Empty((await ReadDataAsync(
            await stranger.GetAsync("/api/v1/notifications/subscriptions"))).EnumerateArray());
        var denied = await stranger.DeleteAsync(
            $"/api/v1/notifications/subscriptions/{subscriptionId}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var revoked = await ReadDataAsync(await owner.DeleteAsync(
            $"/api/v1/notifications/subscriptions/{subscriptionId}"));
        Assert.Equal("Revoked", revoked.GetProperty("status").GetString());
        var revokedAgain = await ReadDataAsync(await owner.DeleteAsync(
            $"/api/v1/notifications/subscriptions/{subscriptionId}"));
        Assert.Equal("Revoked", revokedAgain.GetProperty("status").GetString());

        Assert.Equal(1, await CountSubscriptionsAsync(temp));
    }

    [SkippableFact]
    public async Task CrossAccountEndpointConflict_Returns409_AndPreservesOwnerSubscriptionAndDeliveries()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var ownerId = Guid.NewGuid();
        var owner = Authed(factory, ownerId);
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var (ownerP256Dh, ownerAuth) = NotificationTestSupport.SubscriptionKeys();
        var ownerBody = JsonSerializer.Serialize(new
        {
            deviceId = "owner-browser",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = ownerP256Dh, auth = ownerAuth }
        });
        var created = await ReadDataAsync(await owner.PostAsync(
            "/api/v1/notifications/subscriptions", Json(ownerBody)));
        var subscriptionId = created.GetProperty("id").GetGuid();

        for (var index = 0; index < 3; index++)
        {
            await ReadDataAsync(await owner.PostAsync(
                "/api/v1/notifications/test", Json("{}")));
        }
        using (var scope = factory.Services.CreateScope())
        {
            var worker = scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorker>();
            await worker.FanoutOnceAsync(CancellationToken.None);
        }

        var now = DateTimeOffset.UtcNow;
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var deliveries = await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .Where(d => d.SubscriptionId == subscriptionId)
                .OrderBy(d => d.Id)
                .ToListAsync();
            Assert.Equal(3, deliveries.Count);
            deliveries[0].Status = NotificationDeliveryStatuses.Pending;
            deliveries[0].NextAttemptAt = now.AddMinutes(-2);
            deliveries[1].Status = NotificationDeliveryStatuses.Retrying;
            deliveries[1].NextAttemptAt = now.AddMinutes(-1);
            deliveries[2].Status = NotificationDeliveryStatuses.Processing;
            deliveries[2].LockedUntil = now.AddMinutes(-1);
            deliveries[2].LockedBy = "ownership-regression";
            await db.SaveChangesAsync();
        }

        NotificationSubscriptionEntity before;
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            before = await db.NotificationSubscriptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(s => s.Id == subscriptionId);
        }

        var strangerId = Guid.NewGuid();
        var stranger = Authed(factory, strangerId);
        var (strangerP256Dh, strangerAuth) = NotificationTestSupport.SubscriptionKeys();
        var conflictBody = JsonSerializer.Serialize(new
        {
            deviceId = "stranger-browser",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = strangerP256Dh, auth = strangerAuth }
        });
        var conflict = await stranger.PostAsync(
            "/api/v1/notifications/subscriptions", Json(conflictBody));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var raw = await conflict.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(raw))
        {
            Assert.Equal(40906, document.RootElement.GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
            Assert.Equal(
                "该推送地址已归属其他账号，本版本仅允许原账号继续使用，不支持跨账号换绑",
                document.RootElement.GetProperty("message").GetString());
        }
        Assert.DoesNotContain(endpoint, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerP256Dh, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerAuth, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(strangerP256Dh, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(strangerAuth, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(strangerId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner-browser", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("fcm.googleapis.com", raw, StringComparison.OrdinalIgnoreCase);

        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var after = await db.NotificationSubscriptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(s => s.Id == subscriptionId);
            Assert.Equal(ownerId, after.UserId);
            Assert.Equal(before.DeviceId, after.DeviceId);
            Assert.Equal(before.EndpointCiphertext, after.EndpointCiphertext);
            Assert.Equal(before.EndpointHash, after.EndpointHash);
            Assert.Equal(before.EndpointHost, after.EndpointHost);
            Assert.Equal(before.P256DhCiphertext, after.P256DhCiphertext);
            Assert.Equal(before.AuthCiphertext, after.AuthCiphertext);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.CreatedAt, after.CreatedAt);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(1, await db.NotificationSubscriptions
                .IgnoreQueryFilters()
                .CountAsync());

            var statuses = await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .Where(d => d.SubscriptionId == subscriptionId)
                .Select(d => d.Status)
                .ToListAsync();
            Assert.Equal(3, statuses.Count);
            Assert.DoesNotContain(NotificationDeliveryStatuses.Suppressed, statuses);
            Assert.Contains(NotificationDeliveryStatuses.Pending, statuses);
            Assert.Contains(NotificationDeliveryStatuses.Retrying, statuses);
            Assert.Contains(NotificationDeliveryStatuses.Processing, statuses);
        }

        var fake = new FakePushTransport();
        var clock = new ManualTimeProvider(now.AddMinutes(1));
        var protector = factory.Services.GetRequiredService<ISecretProtector>();
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var worker = NotificationTestSupport.NewWorker(
                db, fake, clock, protector: protector);
            await worker.DeliverOnceAsync(CancellationToken.None);
        }

        Assert.Equal(3, fake.Calls);
        await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            var statuses = await db.NotificationDeliveries
                .IgnoreQueryFilters()
                .Where(d => d.SubscriptionId == subscriptionId)
                .Select(d => d.Status)
                .ToListAsync();
            Assert.All(statuses, status =>
                Assert.Equal(NotificationDeliveryStatuses.Delivered, status));
        }
    }

    [SkippableFact]
    public async Task InvalidSubscription_IsRejected_AndNotPersisted()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory, Guid.NewGuid());
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();

        var invalid = await client.PostAsync(
            "/api/v1/notifications/subscriptions",
            Json(JsonSerializer.Serialize(new
            {
                deviceId = "browser-x",
                channel = "WebPush",
                endpoint = "https://127.0.0.1/push",
                keys = new { p256dh = p256Dh, auth }
            })));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, await CountSubscriptionsAsync(temp));
    }

    [SkippableFact]
    public async Task ConcurrentSubscriptionUpserts_ConvergeToOneRow()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var user = Guid.NewGuid();
        var first = Authed(factory, user);
        var second = Authed(factory, user);
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();
        var body = JsonSerializer.Serialize(new
        {
            deviceId = "browser-concurrent",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = p256Dh, auth }
        });

        var responses = await Task.WhenAll(
            first.PostAsync("/api/v1/notifications/subscriptions", Json(body)),
            second.PostAsync("/api/v1/notifications/subscriptions", Json(body)));

        foreach (var response in responses)
        {
            var payload = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {payload}");
        }
        Assert.Equal(1, await CountSubscriptionsAsync(temp));
        var listed = await ReadDataAsync(
            await first.GetAsync("/api/v1/notifications/subscriptions"));
        Assert.Equal(1, listed.GetArrayLength());
    }

    [SkippableFact]
    public async Task ConcurrentCrossAccountSubscriptionUpserts_ReturnOneOwnerAndOne40906()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var ownerId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var owner = Authed(factory, ownerId);
        var stranger = Authed(factory, strangerId);
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var (ownerP256Dh, ownerAuth) = NotificationTestSupport.SubscriptionKeys();
        var (strangerP256Dh, strangerAuth) = NotificationTestSupport.SubscriptionKeys();
        var ownerBody = JsonSerializer.Serialize(new
        {
            deviceId = "owner-race-browser",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = ownerP256Dh, auth = ownerAuth }
        });
        var strangerBody = JsonSerializer.Serialize(new
        {
            deviceId = "stranger-race-browser",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = strangerP256Dh, auth = strangerAuth }
        });

        var responses = await Task.WhenAll(
            owner.PostAsync("/api/v1/notifications/subscriptions", Json(ownerBody)),
            stranger.PostAsync("/api/v1/notifications/subscriptions", Json(strangerBody)));
        var ownerResponse = responses[0];
        var strangerResponse = responses[1];
        Assert.NotEqual(ownerResponse.IsSuccessStatusCode, strangerResponse.IsSuccessStatusCode);

        var winnerId = ownerResponse.IsSuccessStatusCode ? ownerId : strangerId;
        var loserId = ownerResponse.IsSuccessStatusCode ? strangerId : ownerId;
        var loserResponse = ownerResponse.IsSuccessStatusCode ? strangerResponse : ownerResponse;
        Assert.Equal(HttpStatusCode.Conflict, loserResponse.StatusCode);
        var raw = await loserResponse.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(raw))
        {
            Assert.Equal(40906, document.RootElement.GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
            Assert.DoesNotContain(
                "撤销后",
                document.RootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain(endpoint, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerP256Dh, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerAuth, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(strangerP256Dh, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(strangerAuth, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(strangerId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await CountSubscriptionsAsync(temp));
        await using var db = NotificationTestSupport.NewDbContext(temp.ConnectionString);
        var subscription = await db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(winnerId, subscription.UserId);
        Assert.NotEqual(loserId, subscription.UserId);
        Assert.Equal("Active", subscription.Status);
    }

    [SkippableFact]
    public async Task RevokedEndpoint_RemainsOwned_AndOnlyOriginalAccountCanReactivate()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var ownerId = Guid.NewGuid();
        var owner = Authed(factory, ownerId);
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var (ownerP256Dh, ownerAuth) = NotificationTestSupport.SubscriptionKeys();
        var body = JsonSerializer.Serialize(new
        {
            deviceId = "revoked-owner-browser",
            channel = "WebPush",
            endpoint,
            keys = new { p256dh = ownerP256Dh, auth = ownerAuth }
        });
        var created = await ReadDataAsync(await owner.PostAsync(
            "/api/v1/notifications/subscriptions", Json(body)));
        var subscriptionId = created.GetProperty("id").GetGuid();
        var revoked = await ReadDataAsync(await owner.DeleteAsync(
            $"/api/v1/notifications/subscriptions/{subscriptionId}"));
        Assert.Equal("Revoked", revoked.GetProperty("status").GetString());

        var stranger = Authed(factory, Guid.NewGuid());
        var (strangerP256Dh, strangerAuth) = NotificationTestSupport.SubscriptionKeys();
        var conflict = await stranger.PostAsync(
            "/api/v1/notifications/subscriptions",
            Json(JsonSerializer.Serialize(new
            {
                deviceId = "revoked-stranger-browser",
                channel = "WebPush",
                endpoint,
                keys = new { p256dh = strangerP256Dh, auth = strangerAuth }
            })));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var raw = await conflict.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(raw))
        {
            Assert.Equal(40906, document.RootElement.GetProperty("code").GetInt32());
        }

        var reactivated = await ReadDataAsync(await owner.PostAsync(
            "/api/v1/notifications/subscriptions", Json(body)));
        Assert.Equal(subscriptionId, reactivated.GetProperty("id").GetGuid());
        Assert.Equal("Active", reactivated.GetProperty("status").GetString());
        Assert.Equal(1, await CountSubscriptionsAsync(temp));
    }

    [SkippableFact]
    public async Task TestNotification_AppearsInInAppList_CanBeRead_AndStreamsOverSse()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory, Guid.NewGuid());

        var test = await ReadDataAsync(await client.PostAsync(
            "/api/v1/notifications/test", Json("{}")));
        Assert.True(test.GetProperty("enqueued").GetBoolean());

        var list = await ReadDataAsync(await client.GetAsync("/api/v1/notifications"));
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal("notification.test", item.GetProperty("type").GetString());
        Assert.True(item.GetProperty("cursor").GetInt64() > 0);
        var id = item.GetProperty("id").GetGuid();

        await ReadDataAsync(await client.PostAsync(
            $"/api/v1/notifications/{id}/read", Json("{}")));
        list = await ReadDataAsync(await client.GetAsync("/api/v1/notifications"));
        Assert.Equal(0, list.GetProperty("unreadCount").GetInt32());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stream = await client.GetAsync(
            "/api/v1/notifications/stream?after=0",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.StartsWith("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        await using var body = await stream.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var sawNotification = false;
        for (var line = 0; line < 40 && !sawNotification; line++)
        {
            var text = await reader.ReadLineAsync(cts.Token);
            if (text is null) break;
            if (text.Contains("\"type\":\"notification.test\"", StringComparison.Ordinal))
                sawNotification = true;
        }
        Assert.True(sawNotification, "SSE 未返回已入队的站内通知");
    }

    [SkippableFact]
    public async Task CompletedTaskThroughApi_EnqueuesInAppNotification()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory, Guid.NewGuid());

        var created = await client.PostAsync(
            "/api/v1/calendar/tasks",
            Json("""{"title":"API 完成任务","priority":1,"status":"COMPLETED"}"""));
        var raw = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, $"{(int)created.StatusCode} {raw}");

        var list = await ReadDataAsync(await client.GetAsync("/api/v1/notifications"));
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal("calendar.task.completed", item.GetProperty("type").GetString());
        Assert.Equal("任务已完成：API 完成任务", item.GetProperty("title").GetString());
    }

    [SkippableFact]
    public async Task DispatcherServices_AreResolvableFromProductionRegistration()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NotificationEventScanner>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorker>());
    }

    [SkippableFact]
    public async Task NotificationRoutes_RequireAuthentication()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var anonymous = factory.CreateClient();

        foreach (var path in new[]
                 {
                     "/api/v1/notifications",
                     "/api/v1/notifications/preferences",
                     "/api/v1/notifications/subscriptions",
                     "/api/v1/notifications/stream"
                 })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task CrossUserReadAndDeliveryRetry_AreDeniedOrEmpty()
    {
        var (temp, factory) = await NewHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var ownerId = Guid.NewGuid();
        var owner = Authed(factory, ownerId);
        var (ownerP256Dh, ownerAuth) = NotificationTestSupport.SubscriptionKeys();
        await ReadDataAsync(await owner.PostAsync(
            "/api/v1/notifications/subscriptions",
            Json(JsonSerializer.Serialize(new
            {
                deviceId = "browser-isolation",
                channel = "WebPush",
                endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}",
                keys = new
                {
                    p256dh = ownerP256Dh,
                    auth = ownerAuth
                }
            }))));
        await ReadDataAsync(await owner.PostAsync("/api/v1/notifications/test", Json("{}")));

        using (var scope = factory.Services.CreateScope())
        {
            var worker = scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorker>();
            await worker.FanoutOnceAsync(CancellationToken.None);
        }

        var ownerItems = await ReadDataAsync(await owner.GetAsync("/api/v1/notifications"));
        var notificationId = ownerItems.GetProperty("items")[0].GetProperty("id").GetGuid();
        var ownerDeliveries = await ReadDataAsync(
            await owner.GetAsync("/api/v1/notifications/deliveries"));
        var deliveryId = ownerDeliveries.GetProperty("items")[0].GetProperty("id").GetGuid();

        var stranger = Authed(factory, Guid.NewGuid());
        var strangerList = await ReadDataAsync(await stranger.GetAsync("/api/v1/notifications"));
        Assert.Equal(0, strangerList.GetProperty("items").GetArrayLength());
        Assert.Equal(0, strangerList.GetProperty("unreadCount").GetInt32());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.PostAsync(
                $"/api/v1/notifications/{notificationId}/read", Json("{}"))).StatusCode);
        var strangerReadAll = await ReadDataAsync(
            await stranger.PostAsync("/api/v1/notifications/read-all", Json("{}")));
        Assert.Equal(0, strangerReadAll.GetProperty("updated").GetInt32());

        var strangerDeliveries = await ReadDataAsync(
            await stranger.GetAsync("/api/v1/notifications/deliveries"));
        Assert.Equal(0, strangerDeliveries.GetProperty("items").GetArrayLength());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.PostAsync(
                $"/api/v1/notifications/deliveries/{deliveryId}/retry", Json("{}"))).StatusCode);
    }

    private static async Task<(TempMigrationDatabase Temp, WebApplicationFactory<Program> Factory)>
        NewHostAsync()
    {
        PimDbContext.RegisterModuleAssembly(
            typeof(Pim.Module.Calendar.Entities.ReminderEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync("test_notification_endpoints");
        await temp.MigrateAsync();
        var keys = TestVapidKeys();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", temp.ConnectionString);
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-notification-dp-{Guid.NewGuid():N}"));
            builder.UseSetting("Notifications:Dispatcher:Enabled", "false");
            builder.UseSetting("Notifications:WebPush:PublicKey", keys.PublicKey);
            builder.UseSetting("Notifications:WebPush:PrivateKey", keys.PrivateKey);
            builder.UseSetting("Notifications:WebPush:Subject", "mailto:test@example.invalid");
            builder.UseSetting("Notifications:WebPush:AllowedEndpointHosts:0", "fcm.googleapis.com");
        });
        return (temp, factory);
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, Guid userId)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<JwtService>()
            .GenerateAccessToken(userId, $"notify-{userId:N}"[..24], "user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> SendWithDeviceAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string deviceId,
        string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Pim-Device-Id", deviceId);
        if (json is not null) request.Content = Json(json);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {raw}");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<int> CountSubscriptionsAsync(TempMigrationDatabase temp)
    {
        await using var command = temp.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM notification_subscriptions";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static (string PublicKey, string PrivateKey) TestVapidKeys()
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (NotificationTestSupport.Base64Url(publicKey),
            NotificationTestSupport.Base64Url(parameters.D!));
    }
}
