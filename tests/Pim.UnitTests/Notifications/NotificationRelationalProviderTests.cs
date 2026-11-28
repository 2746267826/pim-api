using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Notifications;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Notifications;
using Xunit;

namespace Pim.UnitTests.Notifications;

/// <summary>
/// 关系型但非 PostgreSQL 的测试 provider（SQLite）不能走到任何 NpgsqlCommand /
/// NpgsqlConnection 强转分支；否则测试路径会以 InvalidCastException 失败。
/// </summary>
public sealed class NotificationRelationalProviderTests
{
    [Fact]
    public async Task ReminderScan_OnRelationalSqlite_DoesNotCastTheConnectionToNpgsql()
    {
        await using var context = await SqliteContext.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-28T00:00:00Z"));
        var storage = Path.Combine(Path.GetTempPath(), $"pim-notification-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
        try
        {
            var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = storage });
            var scanner = new NotificationEventScanner(
                context.Db,
                NotificationTestSupport.NewWriter(context.Db, clock),
                new NoteAsrTaskStore(options),
                new NoteDerivedStore(options, clock),
                options,
                clock,
                NullLogger<NotificationEventScanner>.Instance);

            await scanner.ScanDueRemindersAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public async Task DeliveryClaimAndTrackedRetry_OnRelationalSqlite_UseTheTrackedPath()
    {
        await using var context = await SqliteContext.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-28T00:00:00Z"));
        var user = Guid.NewGuid();
        var now = clock.UtcNow;
        var subscription = new NotificationSubscriptionEntity
        {
            UserId = user,
            DeviceId = "sqlite-device",
            Channel = NotificationChannels.WebPush,
            EndpointCiphertext = "cipher",
            EndpointHash = "hash",
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
            DedupeKey = $"sqlite-{Guid.NewGuid():N}",
            Title = "SQLite 关系型路径",
            Body = "body",
            InAppVisible = true,
            PushEligible = true,
            FanoutStatus = "Completed",
            NextFanoutAt = now,
            CreatedAt = now,
            FanoutCompletedAt = now
        };
        var delivery = new NotificationDeliveryEntity
        {
            EventId = notification.Id,
            Event = notification,
            SubscriptionId = subscription.Id,
            UserId = user,
            Channel = NotificationChannels.WebPush,
            DeviceId = subscription.DeviceId,
            Status = NotificationDeliveryStatuses.DeadLetter,
            AttemptCount = 3,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            DeadLetteredAt = now,
            LastErrorCode = "retry_exhausted",
            LastErrorMessage = "多次重试仍未送达，已进入死信"
        };
        context.Db.NotificationSubscriptions.Add(subscription);
        context.Db.NotificationEvents.Add(notification);
        context.Db.NotificationDeliveries.Add(delivery);
        await context.Db.SaveChangesAsync();

        var worker = NotificationTestSupport.NewWorker(
            context.Db,
            new FakePushTransport(),
            clock,
            deliveryBatchSize: 20,
            providerTimeoutSeconds: 15,
            lockSeconds: 120);
        Assert.Empty(await worker.ClaimDeliveriesAsync(
            clock.UtcNow,
            "sqlite-worker",
            CancellationToken.None));

        var query = new NotificationQueryService(
            context.Db,
            new FixedCurrentUser(user),
            clock);
        var reopened = await query.RetryDeliveryAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(NotificationDeliveryStatuses.Pending, reopened.Status);
        Assert.Equal(0, reopened.AttemptCount);
    }

    private sealed class SqliteContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public PimDbContext Db { get; }

        private SqliteContext(SqliteConnection connection, PimDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<SqliteContext> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new NotificationSqliteDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteContext(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// 只映射通知实体，并用 SQLite 支持的默认值，避免触达 PostgreSQL 专有的
    /// now() / identity 配置（与 mobile SQLite 测试上下文同一处理方式）。
    /// </summary>
    private sealed class NotificationSqliteDbContext(DbContextOptions<PimDbContext> options)
        : PimDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NotificationPreferenceEntity>(e =>
            {
                e.HasKey(x => x.UserId);
                e.Property(x => x.DeviceOverridesJson).HasDefaultValue("{}");
            });
            modelBuilder.Entity<NotificationSubscriptionEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.Channel, x.EndpointHash }).IsUnique();
            });
            modelBuilder.Entity<NotificationEventEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique();
            });
            modelBuilder.Entity<NotificationDeliveryEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.EventId, x.SubscriptionId }).IsUnique();
                e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId);
            });
        }
    }
}
