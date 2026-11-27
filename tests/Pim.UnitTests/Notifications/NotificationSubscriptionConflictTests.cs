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
        await using var temp = await TempMigrationDatabase.CreateAsync("test_notification_conflict");
        await temp.MigrateAsync();

        var endpoint = $"https://fcm.googleapis.com/wp/{Guid.NewGuid():N}";
        var endpointHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();
        await temp.ExecuteAsync($"""
            CREATE SEQUENCE notification_conflict_attempts;
            CREATE FUNCTION notification_conflict_trigger_fn()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                PERFORM nextval('notification_conflict_attempts');
                RAISE EXCEPTION 'forced unique violation'
                    USING ERRCODE = '23505',
                          CONSTRAINT = 'IX_notification_subscriptions_channel_endpoint_hash',
                          DETAIL = 'Key (channel, endpoint_hash)=(WebPush, {endpointHash}) already exists.';
                RETURN NEW;
            END;
            $function$;
            CREATE TRIGGER notification_conflict_trigger
            BEFORE INSERT ON notification_subscriptions
            FOR EACH ROW
            WHEN (NEW.channel = '{NotificationChannels.WebPush}'
                  AND NEW.endpoint_hash = '{endpointHash}')
            EXECUTE FUNCTION notification_conflict_trigger_fn();
            """);

        var userId = Guid.NewGuid();
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();
        var serviceLogs = new RecordingLogger<NotificationSubscriptionService>();
        var efLogs = new List<string>();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(temp.ConnectionString)
            .LogTo(efLogs.Add, LogLevel.Warning)
            .Options;
        await using var db = new PimDbContext(options);
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
            serviceLogs);

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
        Assert.Equal(
            2L,
            await ScalarAsync(
                temp.ConnectionString,
                "SELECT last_value FROM notification_conflict_attempts"));
        await using (var verify = NotificationTestSupport.NewDbContext(temp.ConnectionString))
        {
            Assert.Equal(0, await verify.NotificationSubscriptions
                .IgnoreQueryFilters()
                .CountAsync());
        }

        Assert.Contains(
            serviceLogs.Messages,
            message => message.Contains("unique conflict", StringComparison.Ordinal));
        Assert.Contains(
            serviceLogs.Messages,
            message => message.Contains("ownership conflict", StringComparison.Ordinal));
        var log = string.Join("\n", serviceLogs.Messages.Concat(efLogs));
        Assert.DoesNotContain(endpointHash, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(endpoint, log, StringComparison.Ordinal);
        Assert.DoesNotContain(p256Dh, log, StringComparison.Ordinal);
        Assert.DoesNotContain(auth, log, StringComparison.Ordinal);
        Assert.DoesNotContain("23505", log, StringComparison.Ordinal);
        Assert.DoesNotContain("PostgresException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate key", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "IX_notification_subscriptions",
            log,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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
