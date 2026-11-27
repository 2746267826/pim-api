using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
public sealed class NotificationSubscriptionConflictTests
{
    [SkippableFact]
    public async Task SecondUniqueConflict_ConvergesTo40906_WithoutLeakingEndpointDetails()
    {
        PimDbContext.RegisterModuleAssembly(
            typeof(Pim.Module.Calendar.Entities.ReminderEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync("test_notification_conflict");
        await using var _ = temp;
        await temp.MigrateAsync();

        var userId = Guid.NewGuid();
        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var endpointHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();
        var logger = new RecordingLogger<NotificationSubscriptionService>();
        await using var db = new ConflictInjectingDbContext(temp.ConnectionString, endpointHash);
        var service = new NotificationSubscriptionService(
            db,
            new FixedCurrentUser(userId),
            NotificationTestSupport.Secrets,
            new FakePushTransport(),
            Options.Create(new WebPushOptions
            {
                AllowedEndpointHosts = ["fcm.googleapis.com"]
            }),
            new ManualTimeProvider(DateTimeOffset.UtcNow),
            logger);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.UpsertAsync(
            new CreateNotificationSubscriptionRequest(
                "conflict-browser",
                NotificationChannels.WebPush,
                endpoint,
                new NotificationSubscriptionKeysDto(p256Dh, auth)),
            CancellationToken.None));

        Assert.Equal(40906, error.ErrorCode);
        Assert.Equal(
            "该推送地址已归属其他账号，本版本仅允许原账号继续使用，不支持跨账号换绑",
            error.Message);
        Assert.Equal(2, db.InjectedConflicts);
        await using (var verify = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            Assert.Equal(0, await verify.NotificationSubscriptions
                .IgnoreQueryFilters()
                .CountAsync());
        }

        var log = string.Join("\n", logger.Messages);
        Assert.DoesNotContain(endpointHash, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(endpoint, log, StringComparison.Ordinal);
        Assert.DoesNotContain(p256Dh, log, StringComparison.Ordinal);
        Assert.DoesNotContain(auth, log, StringComparison.Ordinal);
        Assert.DoesNotContain("23505", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IX_notification_subscriptions",
            log,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ConflictInjectingDbContext : PimDbContext
    {
        private readonly string _connectionString;
        private readonly string _endpointHash;

        public ConflictInjectingDbContext(string connectionString, string endpointHash)
            : base(new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(connectionString)
                .Options)
        {
            _connectionString = connectionString;
            _endpointHash = endpointHash;
        }

        public int InjectedConflicts { get; private set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var conflictId = await InsertConflictAsync(cancellationToken);
            try
            {
                var result = await base.SaveChangesAsync(cancellationToken);
                await DeleteConflictAsync(conflictId, cancellationToken);
                return result;
            }
            catch (DbUpdateException)
            {
                await DeleteConflictAsync(conflictId, cancellationToken);
                InjectedConflicts++;
                throw;
            }
        }

        private async Task<Guid> InsertConflictAsync(CancellationToken ct)
        {
            var id = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notification_subscriptions
                    (id, user_id, device_id, channel, endpoint_ciphertext, endpoint_hash,
                     endpoint_host, p256dh_ciphertext, auth_ciphertext, status, version,
                     created_at, updated_at)
                VALUES
                    (@id, @user_id, @device_id, @channel, @endpoint_ciphertext, @endpoint_hash,
                     @endpoint_host, @p256dh_ciphertext, @auth_ciphertext, @status, @version,
                     @created_at, @updated_at)
                """;
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("user_id", Guid.NewGuid());
            command.Parameters.AddWithValue("device_id", "conflict-owner");
            command.Parameters.AddWithValue("channel", NotificationChannels.WebPush);
            command.Parameters.AddWithValue("endpoint_ciphertext", "encrypted");
            command.Parameters.AddWithValue("endpoint_hash", _endpointHash);
            command.Parameters.AddWithValue("endpoint_host", "fcm.googleapis.com");
            command.Parameters.AddWithValue("p256dh_ciphertext", "encrypted");
            command.Parameters.AddWithValue("auth_ciphertext", "encrypted");
            command.Parameters.AddWithValue("status", "Active");
            command.Parameters.AddWithValue("version", Guid.NewGuid());
            command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync(ct);
            return id;
        }

        private async Task DeleteConflictAsync(Guid id, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM notification_subscriptions WHERE id = @id";
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null) Messages.Add(exception.ToString());
        }
    }
}
