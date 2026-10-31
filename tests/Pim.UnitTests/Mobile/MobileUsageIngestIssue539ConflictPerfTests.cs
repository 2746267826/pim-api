using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Mobile;

[Collection("Mobile539ConflictPerfSyntheticPg")]
[Trait("DataSource", "RealDb")]
public sealed class MobileUsageIngestIssue539ConflictPerfTests
{
    private const string TempDatabasePrefix = "mobile539_conflict";
    private const int LocalPort = 55442;
    private const string LocalDatabase = "pim_mobile539_conflict";
    private const string LocalUser = "pim";
    private const string SyntheticOptInVariable = "PIM_RUN_ISSUE539_CONFLICT_PERF_SYNTHETIC";
    private const string DeviceId = "synthetic-issue-539-conflict";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-10-31T08:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal
        | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private readonly ITestOutputHelper _output;

    public MobileUsageIngestIssue539ConflictPerfTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task SameBatchWithDifferentPayload_IsRejectedAndOriginalRowKept()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = CreateDbContext(fixture.ConnectionString);
        var service = CreateIngest(db, Now);
        var first = Request(DeviceId, "qa-539-payload-conflict", Now, "PayloadA");

        var created = await service.IngestAsync(first, CancellationToken.None);
        Assert.True(created.AcceptedCount > 0);

        db.ChangeTracker.Clear();
        var before = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == first.BatchId);

        var conflicting = first with
        {
            Events = [Event("com.example.conflict", "MOVE_TO_FOREGROUND", Now.AddTicks(10), "PayloadB")]
        };
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.IngestAsync(conflicting, CancellationToken.None));

        Assert.Equal(MobileSyncErrorCodes.PayloadConflict, error.ErrorCode);
        db.ChangeTracker.Clear();
        var after = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == first.BatchId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ErrorJson, after.ErrorJson);
        Assert.Equal(before.FailedCount, after.FailedCount);
    }

    [SkippableFact]
    public async Task ConcurrentFirstInsertLoser_ReportsPayloadConflictWithoutChangingWinnerLease()
    {
        await using var fixture = await CreateFixtureAsync();
        var insertGate = new PauseFirstBatchInsertInterceptor();
        await using var winnerDb = CreateDbContext(fixture.ConnectionString);
        await using var loserDb = CreateDbContext(fixture.ConnectionString, insertGate);
        await using var readDb = CreateDbContext(fixture.ConnectionString);
        const string batchId = "qa-539-concurrent-payload-conflict";
        var winnerRequest = Request(DeviceId, batchId, Now, "ConcurrentPayloadA");
        var loserRequest = Request(DeviceId, batchId, Now, "ConcurrentPayloadB");
        var winnerReachedReconcile = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var loserTask = CreateIngest(loserDb, Now).IngestAsync(loserRequest, CancellationToken.None);
        await insertGate.Paused.WaitAsync(TimeSpan.FromSeconds(10));

        var winnerTask = CreateIngest(
                winnerDb,
                Now,
                async ct =>
                {
                    winnerReachedReconcile.TrySetResult();
                    await releaseWinner.Task.WaitAsync(ct);
                })
            .IngestAsync(winnerRequest, CancellationToken.None);

        try
        {
            await winnerReachedReconcile.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var before = await readDb.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .SingleAsync(row => row.BatchId == batchId);
            Assert.Equal(MobileSyncBatchStatus.Pending, before.Status);

            insertGate.Release();
            var error = await Assert.ThrowsAsync<DomainException>(
                async () => await loserTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(MobileSyncErrorCodes.PayloadConflict, error.ErrorCode);

            readDb.ChangeTracker.Clear();
            var after = await readDb.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .SingleAsync(row => row.Id == before.Id);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.ErrorJson, after.ErrorJson);
            Assert.Equal(before.CreatedAt, after.CreatedAt);
            Assert.Equal(before.CompletedAtUtc, after.CompletedAtUtc);
        }
        finally
        {
            insertGate.Release();
            releaseWinner.TrySetResult();
        }

        var winnerResult = await winnerTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(winnerResult.AcceptedCount > 0);
    }

    [SkippableFact]
    public async Task SameBatchOnDifferentDevice_CreatesSeparateRow()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = CreateDbContext(fixture.ConnectionString);
        var service = CreateIngest(db, Now);
        var first = Request(DeviceId, "qa-539-device-conflict", Now, "DeviceA");
        var second = Request("synthetic-issue-539-conflict-other", first.BatchId, Now, "DeviceB");

        await service.IngestAsync(first, CancellationToken.None);
        await service.IngestAsync(second, CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(row => row.BatchId == first.BatchId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.DeviceId).Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task SameBatchForDifferentOwner_CreatesSeparateRow()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = CreateDbContext(fixture.ConnectionString);
        var batchId = "qa-539-owner-conflict";
        var first = Request(DeviceId, batchId, Now, "OwnerA");
        var second = Request(DeviceId, batchId, Now, "OwnerB");

        await CreateIngest(db, Now).IngestAsync(first, CancellationToken.None);
        var otherOwner = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await CreateIngest(db, Now, currentUser: MobileTestHelpers.CurrentUser(otherOwner))
            .IngestAsync(second, CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(row => row.BatchId == batchId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.UserId).Distinct().Count());
    }

    [SkippableFact]
    public async Task OpenSessionRebuild_CostsByAgeAndDensity()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = CreateDbContext(fixture.ConnectionString);
        var cases = new (TimeSpan Age, int DensitySeconds)[]
        {
            (TimeSpan.FromHours(1), 60),
            (TimeSpan.FromHours(1), 300),
            (TimeSpan.FromDays(1), 60),
            (TimeSpan.FromDays(1), 300),
            (TimeSpan.FromDays(7), 60),
            (TimeSpan.FromDays(7), 300)
        };

        foreach (var (age, densitySeconds) in cases)
        {
            var deviceId = $"{DeviceId}-{age.TotalHours:0}h-{densitySeconds}s";
            var start = Now - age;
            await SeedOpenSessionAsync(db, deviceId, start, densitySeconds);

            var candidateRows = await db.Set<MobileUsageEventEntity>()
                .AsNoTracking()
                .CountAsync(e => e.UserId == MobileTestHelpers.UserId
                    && e.DeviceId == deviceId
                    && e.EventTimestampUtc >= start
                    && e.EventTimestampUtc <= Now.AddMilliseconds(1));

            var request = Request(deviceId, $"qa-539-perf-{age.TotalHours:0}h-{densitySeconds}s", Now, "Perf");
            var service = CreateIngest(db, Now);
            var stopwatch = Stopwatch.StartNew();
            var result = await service.IngestAsync(request, CancellationToken.None);
            stopwatch.Stop();

            _output.WriteLine(
                "PERF age={0}h density={1}s candidateRows={2} elapsedMs={3} accepted={4}",
                age.TotalHours,
                densitySeconds,
                candidateRows,
                stopwatch.ElapsedMilliseconds,
                result.AcceptedCount);
            Assert.True(result.AcceptedCount > 0);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
        }
    }

    private static async Task SeedOpenSessionAsync(
        PimDbContext db,
        string deviceId,
        DateTimeOffset start,
        int densitySeconds)
    {
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = "com.example.perf",
            StartUtc = start,
            EndUtc = null,
            DurationMs = null,
            QualityFlagsJson = "[\"open-ended\"]",
            CreatedAt = start
        });

        var events = new List<MobileUsageEventEntity>();
        for (var offset = TimeSpan.Zero; offset < Now - start; offset += TimeSpan.FromSeconds(densitySeconds))
        {
            var at = start + offset;
            events.Add(EntityEvent(deviceId, "MOVE_TO_FOREGROUND", at));
            events.Add(EntityEvent(deviceId, "MOVE_TO_BACKGROUND", at.AddSeconds(Math.Min(30, densitySeconds / 2.0))));
        }
        db.Set<MobileUsageEventEntity>().AddRange(events);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static MobileUsageEventsUploadRequest Request(
        string deviceId,
        string batchId,
        DateTimeOffset now,
        string key)
        => new(
            deviceId,
            batchId,
            now,
            now.AddMilliseconds(1),
            [],
            [
                Event("com.example.perf", "MOVE_TO_FOREGROUND", now.AddTicks(1), key + "1"),
                Event("com.example.perf", "MOVE_TO_BACKGROUND", now.AddTicks(2), key + "2"),
                Event("com.example.perf", "MOVE_TO_FOREGROUND", now.AddTicks(3), key + "3"),
                Event("com.example.perf", "MOVE_TO_BACKGROUND", now.AddTicks(4), key + "4"),
                Event("com.example.perf", "SCREEN_INTERACTIVE", now.AddTicks(5), key + "5")
            ],
            []);

    private static MobileUsageEventDto Event(
        string packageName,
        string eventType,
        DateTimeOffset timestamp,
        string clientItemKey)
        => new(
            packageName,
            eventType,
            timestamp,
            clientItemKey + "Activity",
            timestamp,
            "{}",
            clientItemKey);

    private static MobileUsageEventEntity EntityEvent(
        string deviceId,
        string eventType,
        DateTimeOffset timestamp)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = "com.example.perf",
            EventType = eventType,
            EventTimestampUtc = timestamp,
            ClassName = "PerfActivity",
            SourceWindowStartUtc = timestamp,
            SourceWindowEndUtc = timestamp.AddMilliseconds(1),
            CollectedAtUtc = timestamp,
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = timestamp
        };

    private static MobileUsageIngestService CreateIngest(
        PimDbContext db,
        DateTimeOffset now,
        Func<CancellationToken, Task>? beforeReconcile = null,
        Pim.Infrastructure.Auth.ICurrentUserService? currentUser = null)
    {
        var timeProvider = MobileTestHelpers.Time(now);
        var user = currentUser ?? MobileTestHelpers.CurrentUser();
        return new MobileUsageIngestService(
            db,
            user,
            new MobileSessionInterpreter(db, timeProvider)
            {
                BeforeReconcileAsync = beforeReconcile
            },
            timeProvider,
            catalogOverrideService: null,
            materializationService: CreateMaterialization(db, now, user))
        {
            AttemptDeadline = TimeSpan.FromSeconds(30)
        };
    }

    private static MobileAnalyticsMaterializationService CreateMaterialization(
        PimDbContext db,
        DateTimeOffset now,
        Pim.Infrastructure.Auth.ICurrentUserService user)
    {
        var timeProvider = MobileTestHelpers.Time(now);
        return new MobileAnalyticsMaterializationService(
            db,
            new MobileUsageAggregationService(
                db,
                user,
                new MobileAnalyticsQueryService(timeProvider),
                new MobileUsageGoalService(db, user, timeProvider),
                timeProvider,
                new MobileAppClassificationService(db, user)),
            new MobileTimelineBlockService(
                db,
                user,
                timeProvider,
                new MobileAppClassificationService(db, user)),
            timeProvider);
    }

    private static PimDbContext CreateDbContext(
        string connectionString,
        DbCommandInterceptor? interceptor = null)
    {
        MobileTestHelpers.RegisterMobileModule();
        var builder = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        var options = builder.Options;
        return new PimDbContext(options);
    }

    private sealed class PauseFirstBatchInsertInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _paused = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _triggered;

        public Task Paused => _paused.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await PauseIfBatchInsertAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await PauseIfBatchInsertAsync(command, cancellationToken);
            return result;
        }

        private async Task PauseIfBatchInsertAsync(
            DbCommand command,
            CancellationToken cancellationToken)
        {
            if (!command.CommandText.TrimStart().StartsWith(
                    "INSERT INTO mobile_sync_batches",
                    StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref _triggered, 1) != 0)
            {
                return;
            }

            _paused.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task<TempMigrationDatabase> CreateFixtureAsync()
    {
        RequireOwnedConnection();
        var fixture = await TempMigrationDatabase.CreateAsync(TempDatabasePrefix);
        try
        {
            await fixture.MigrateAsync();
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
        return fixture;
    }

    private static void RequireOwnedConnection()
    {
        var optIn = Environment.GetEnvironmentVariable(SyntheticOptInVariable);
        Skip.If(
            !string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase),
            $"Issue 539 conflict/perf tests require explicit {SyntheticOptInVariable}=true.");

        var value = RealDbTestConnection.FromEnvironment;
        Skip.If(
            string.IsNullOrWhiteSpace(value),
            "RealDb unavailable (PIM_TEST_CONN not set), skipping test.");

        var builder = new NpgsqlConnectionStringBuilder(value);
        var owned = string.Equals(builder.Host, "127.0.0.1", StringComparison.Ordinal)
            && builder.Port == LocalPort
            && string.Equals(builder.Database, LocalDatabase, StringComparison.Ordinal)
            && string.Equals(builder.Username, LocalUser, StringComparison.Ordinal);
        if (!owned)
        {
            throw new InvalidOperationException(
                $"Issue 539 conflict/perf tests require owned 127.0.0.1:{LocalPort}/{LocalDatabase} as {LocalUser}.");
        }

        // Validate the connection target before opening it so a generic or production
        // PIM_TEST_CONN can never be probed by this dedicated synthetic suite.
        RealDbTestConnection.Require();
    }
}
