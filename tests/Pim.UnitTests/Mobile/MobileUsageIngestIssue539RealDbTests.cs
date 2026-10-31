using System.Collections;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Mobile;

[Collection("Mobile539SyntheticPg")]
[Trait("DataSource", "RealDb")]
public sealed class MobileUsageIngestIssue539RealDbTests
{
    private const string TempDatabasePrefix = "mobile539_ingest";
    private const string LocalDatabase = "pim_mobile539_synthetic";
    private const int LocalPort = 55441;
    private const string LocalUser = "pim";
    private const string CiDatabase = "pim_ci";
    private const int CiPort = 5432;
    private const string CiPostgresUser = "pim_ci";
    private const string DeviceId = "synthetic-issue-539";
    private const int HistoricalEventCount = 5_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-10-30T04:00:01Z",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal
        | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private readonly ITestOutputHelper _output;

    public MobileUsageIngestIssue539RealDbTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Fixture_CreatesOnlyItsOwnTemporaryDatabase()
    {
        await using var fixture = await CreateFixtureAsync();
        var source = new NpgsqlConnectionStringBuilder(ResolveBaseConnectionString());
        var isolated = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);

        Assert.Equal(source.Host, isolated.Host);
        Assert.Equal(source.Port, isolated.Port);
        Assert.NotEqual(source.Database, isolated.Database);
        Assert.StartsWith($"{TempDatabasePrefix}_", isolated.Database, StringComparison.Ordinal);
        Assert.Matches("^[a-f0-9]{32}$", isolated.Database[(TempDatabasePrefix.Length + 1)..]);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT current_database(), count(*) FROM information_schema.tables "
            + "WHERE table_schema = 'public' AND table_name = 'mobile_usage_events'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(isolated.Database, reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));

        _output.WriteLine(
            "ISOLATED_FIXTURE database={0} host={1} port={2}",
            isolated.Database,
            isolated.Host,
            isolated.Port);
    }

    [SkippableFact]
    public async Task OneMillisecondTail_WithCrossDayOpenSession_DoesNotRescanHistoryOrReplaceSessionId()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        var observer = new EventQueryObserver();
        await using var db = CreateDbContext(connectionString, observer);

        var tailStart = Now.AddHours(-1);
        var sessionStart = tailStart.AddHours(-20);
        var seededSessionId = await SeedCrossDayOpenSessionAsync(db, sessionStart, tailStart);
        db.ChangeTracker.Clear();

        var request = TailRequest(tailStart);
        var service = CreateIngest(db, Now, TimeSpan.FromSeconds(5));
        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(5, result.AcceptedCount);
        Assert.Equal(0, result.FailedCount);

        db.ChangeTracker.Clear();
        var sessions = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(row => row.DeviceId == DeviceId)
            .OrderBy(row => row.StartUtc)
            .ToListAsync();

        foreach (var observation in observer.Observations.Where(item => item.RowsObserved > 0))
        {
            _output.WriteLine(
                "EVENT_SELECT rows={0} sql={1}",
                observation.RowsObserved,
                Collapse(observation.CommandText));
        }

        var maxRowsObserved = observer.Observations
            .Select(item => item.RowsObserved)
            .DefaultIfEmpty(0)
            .Max();
        _output.WriteLine("EVENT_SELECT_MAX rows={0}", maxRowsObserved);
        Assert.Contains(sessions, row => row.Id == seededSessionId);
        Assert.True(maxRowsObserved <= 20, $"Tail ingest read {maxRowsObserved} event rows.");
    }

    [SkippableFact]
    public async Task BoundaryEvent_InsideOpenSession_IsNotSwallowedByIncrementalTailPath()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        await using var db = CreateDbContext(connectionString, new EventQueryObserver());

        var boundary = Now.AddHours(-1);
        var seededSessionId = await SeedCrossDayOpenSessionAsync(db, boundary.AddHours(-20), boundary);
        db.ChangeTracker.Clear();

        var request = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-boundary-event",
            boundary,
            boundary.AddMinutes(1),
            [],
            [
                ClientEvent("com.example.messages", "MOVE_TO_BACKGROUND", boundary, "Boundary1")
            ],
            []);
        var result = await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(request, CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var session = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == seededSessionId);
        Assert.Equal(boundary, session.EndUtc);
        Assert.DoesNotContain("open-ended", session.QualityFlagsJson);
    }

    [SkippableFact]
    public async Task MaterializationLockWait_IsCanceledByAttemptDeadline_AndSchedulesBoundedRetry()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        var observer = new EventQueryObserver();
        await using var db = CreateDbContext(connectionString, observer);

        var tailStart = Now.AddHours(-1);
        await SeedCrossDayOpenSessionAsync(db, tailStart.AddHours(-4), tailStart);
        db.ChangeTracker.Clear();

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        var lockKey = MobileAnalyticsMaterializationLock.KeyFor(MobileTestHelpers.UserId, DeviceId);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", lockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var request = TailRequest(tailStart) with { ClientBatchId = "batch-issue-539-deadline" };
        var service = CreateIngest(db, Now, TimeSpan.FromMilliseconds(600));
        var startedAt = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => service.IngestAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        _output.WriteLine("DEADLINE_CANCELED elapsedMs={0:F0} message={1}", elapsed.TotalMilliseconds, error.Message);

        await lockTransaction.RollbackAsync();
        db.ChangeTracker.Clear();
        var batch = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == request.BatchId);
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);

        Assert.True(elapsed < TimeSpan.FromSeconds(8));
        Assert.Equal(MobileSyncBatchStatus.Pending, batch.Status);
        Assert.Equal("processing-timeout", envelope.FailureCode);
        Assert.NotNull(envelope.RetryAfterUtc);
        Assert.Equal(0, batch.FailedCount);
    }

    [SkippableFact]
    public async Task ActiveLease_OnRealPipeline_IsReportedAsInFlightAndLeavesPendingRowUnchanged()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        await using var db = CreateDbContext(connectionString, new EventQueryObserver());

        var request = TailRequest(Now.AddHours(-1)) with { ClientBatchId = "batch-issue-539-in-flight" };
        var leaseId = Guid.NewGuid();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                [],
                MobileUsageIngestService.ComputePayloadSha256(request),
                Now.AddMinutes(10),
                attemptCount: 1,
                leaseId: leaseId.ToString("N")),
            CreatedAt = Now.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == request.BatchId);

        var result = await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(request, CancellationToken.None);

        db.ChangeTracker.Clear();
        var after = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == request.BatchId);

        _output.WriteLine(
            "IN_FLIGHT_RESULT accepted={0} skipped={1} rejected={2} failed={3} items={4} state={5} retryAfterSeconds={6}",
            result.AcceptedCount,
            result.SkippedCount,
            result.RejectedCount,
            result.FailedCount,
            result.ItemResults.Count,
            result.ProcessingState,
            result.RetryAfterSeconds);
        Assert.Equal("in-flight", result.ProcessingState);
        Assert.Equal(600, result.RetryAfterSeconds);
        Assert.Equal(Now.AddMinutes(10), result.LeaseUntilUtc);
        Assert.Null(result.RetryAtUtc);
        Assert.Equal(5, result.FailedCount);
        Assert.Empty(result.ItemResults);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ErrorJson, after.ErrorJson);
        Assert.Equal(before.FailedCount, after.FailedCount);
        Assert.Equal(before.CompletedAtUtc, after.CompletedAtUtc);
    }

    [SkippableFact]
    public async Task LateEvent_InsidePriorWindow_RecomputesSessionsWithoutReplacingStableSessionId()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        await using var db = CreateDbContext(connectionString, new EventQueryObserver());

        var start = Now.AddHours(-2);
        var firstRequest = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-late-base",
            start,
            start.AddMinutes(40),
            [],
            [
                ClientEvent("com.example.messages", "MOVE_TO_FOREGROUND", start, "Base1"),
                ClientEvent("com.example.messages", "MOVE_TO_BACKGROUND", start.AddMinutes(10), "Base2"),
                ClientEvent("com.example.chat", "MOVE_TO_FOREGROUND", start.AddMinutes(20), "Base3"),
                ClientEvent("com.example.chat", "MOVE_TO_BACKGROUND", start.AddMinutes(30), "Base4")
            ],
            []);
        await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(firstRequest, CancellationToken.None);

        db.ChangeTracker.Clear();
        var chatBefore = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.PackageName == "com.example.chat");
        var messagesBefore = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.PackageName == "com.example.messages");

        var lateRequest = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-late-change",
            start.AddMinutes(20),
            start.AddMinutes(35),
            [],
            [
                ClientEvent("com.example.mail", "MOVE_TO_FOREGROUND", start.AddMinutes(25), "Late1")
            ],
            []);
        await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(lateRequest, CancellationToken.None);

        db.ChangeTracker.Clear();
        var sessions = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(row => row.DeviceId == DeviceId)
            .OrderBy(row => row.StartUtc)
            .ToListAsync();

        Assert.Contains(sessions, row => row.Id == messagesBefore.Id
            && row.StartUtc == messagesBefore.StartUtc
            && row.EndUtc == messagesBefore.EndUtc);
        var chatAfter = Assert.Single(sessions, row => row.PackageName == "com.example.chat");
        Assert.Equal(chatBefore.Id, chatAfter.Id);
        Assert.Equal(chatBefore.CreatedAt, chatAfter.CreatedAt);
        Assert.Equal(start.AddMinutes(25), chatAfter.EndUtc);
        Assert.Contains("closed-by-app-switch", chatAfter.QualityFlagsJson);
        Assert.Contains(sessions, row => row.PackageName == "com.example.mail"
            && row.StartUtc == start.AddMinutes(25)
            && row.EndUtc == start.AddMinutes(35));
    }

    [SkippableFact]
    public async Task LateEvent_BeforeEarliestOverlappingSession_IsReconciledFromRequestWindow()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        await using var db = CreateDbContext(connectionString, new EventQueryObserver());

        var start = Now.AddHours(-2);
        var initialRequest = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-early-late-base",
            start.AddMinutes(20),
            start.AddMinutes(41),
            [],
            [
                ClientEvent("com.example.chat", "MOVE_TO_FOREGROUND", start.AddMinutes(20), "EarlyBase1"),
                ClientEvent("com.example.chat", "MOVE_TO_BACKGROUND", start.AddMinutes(40), "EarlyBase2")
            ],
            []);
        await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(initialRequest, CancellationToken.None);

        db.ChangeTracker.Clear();
        var chatBefore = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.PackageName == "com.example.chat");

        var lateRequest = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-early-late-change",
            start,
            start.AddHours(1),
            [],
            [
                ClientEvent("com.example.mail", "MOVE_TO_FOREGROUND", start.AddMinutes(5), "EarlyLate1")
            ],
            []);
        var result = await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(lateRequest, CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var sessions = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(row => row.DeviceId == DeviceId)
            .OrderBy(row => row.StartUtc)
            .ToListAsync();

        var chatAfter = Assert.Single(sessions, row => row.PackageName == "com.example.chat");
        Assert.Equal(chatBefore.Id, chatAfter.Id);
        Assert.Equal(chatBefore.StartUtc, chatAfter.StartUtc);
        var mail = Assert.Single(sessions, row => row.PackageName == "com.example.mail");
        Assert.Equal(start.AddMinutes(5), mail.StartUtc);
        Assert.Equal(start.AddMinutes(20), mail.EndUtc);
        Assert.Contains("closed-by-app-switch", mail.QualityFlagsJson);
    }

    [SkippableFact]
    public async Task CorrectedDayOverflowSession_ClearsDerivedFlagAndIsIncludedInMaterialization()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        await using var db = CreateDbContext(connectionString, new EventQueryObserver());

        var start = Now.AddHours(-2);
        var sessionId = await SeedDayOverflowSessionAsync(db, start, start.AddHours(7));
        db.ChangeTracker.Clear();

        var request = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-day-overflow",
            start,
            start.AddHours(2),
            [],
            [
                ClientEvent("com.example.legacy", "MOVE_TO_BACKGROUND", start.AddHours(1), "OverflowFix1")
            ],
            []);
        var result = await CreateIngest(db, Now, TimeSpan.FromSeconds(5))
            .IngestAsync(request, CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var session = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == sessionId);
        Assert.Equal(start.AddHours(1), session.EndUtc);
        Assert.DoesNotContain("day_overflow", session.QualityFlagsJson);
        Assert.DoesNotContain("anomalous_duration", session.QualityFlagsJson);
        Assert.DoesNotContain("open-ended", session.QualityFlagsJson);

        var foregroundSeconds = await db.Set<MobileUsageAggregateEntity>()
            .Where(row => row.UserId == MobileTestHelpers.UserId
                && row.DeviceId == DeviceId
                && row.PackageName == "com.example.legacy")
            .SumAsync(row => row.ForegroundSeconds);
        Assert.Equal(3600, foregroundSeconds);
    }

    [SkippableFact]
    public async Task ConcurrentBatches_ForSameDevice_SerializeBeforeSessionReconcile()
    {
        await using var fixture = await CreateFixtureAsync();
        var connectionString = fixture.ConnectionString;
        await using var dbA = CreateDbContext(connectionString, new EventQueryObserver());
        await using var dbB = CreateDbContext(connectionString, new EventQueryObserver());

        var start = Now.AddHours(-2);
        var reachedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestA = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-concurrent-a",
            start,
            start.AddMinutes(1),
            [],
            [
                ClientEvent("com.example.alpha", "MOVE_TO_FOREGROUND", start.AddSeconds(1), "ConcurrentA")
            ],
            []);
        var requestB = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-issue-539-concurrent-b",
            start,
            start.AddMinutes(1),
            [],
            [
                ClientEvent("com.example.beta", "MOVE_TO_FOREGROUND", start.AddSeconds(2), "ConcurrentB")
            ],
            []);

        var taskA = CreateIngest(
                dbA,
                Now,
                TimeSpan.FromSeconds(10),
                async ct =>
                {
                    reachedA.TrySetResult();
                    await releaseA.Task.WaitAsync(ct);
                })
            .IngestAsync(requestA, CancellationToken.None);
        await reachedA.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var taskB = CreateIngest(
                dbB,
                Now,
                TimeSpan.FromSeconds(10),
                _ =>
                {
                    reachedB.TrySetResult();
                    return Task.CompletedTask;
                })
            .IngestAsync(requestB, CancellationToken.None);

        var reachedBBeforeRelease = await Task.WhenAny(
            reachedB.Task,
            Task.Delay(TimeSpan.FromSeconds(2))) == reachedB.Task;
        releaseA.TrySetResult();
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(reachedBBeforeRelease, "Batch B entered session reconciliation while batch A owned the device lock.");
        dbA.ChangeTracker.Clear();
        var sessions = await dbA.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(row => row.DeviceId == DeviceId)
            .ToListAsync();
        Assert.Single(sessions, row => row.PackageName == "com.example.alpha");
        Assert.Single(sessions, row => row.PackageName == "com.example.beta");
    }

    private static async Task<Guid> SeedCrossDayOpenSessionAsync(
        PimDbContext db,
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd)
    {
        var session = new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = "com.example.messages",
            StartUtc = sessionStart,
            EndUtc = sessionEnd,
            DurationMs = (long)(sessionEnd - sessionStart).TotalMilliseconds,
            QualityFlagsJson = "[\"open-ended\",\"anomalous_duration\"]",
            CreatedAt = sessionStart
        };

        var events = new List<MobileUsageEventEntity>(HistoricalEventCount + 1)
        {
            EntityEvent(
                "com.example.messages",
                "MOVE_TO_FOREGROUND",
                sessionStart,
                "MainActivity")
        };

        var availableSeconds = Math.Max(1, (sessionEnd - sessionStart).TotalSeconds - 1);
        var spacingSeconds = Math.Max(0.001, availableSeconds / (HistoricalEventCount + 1));
        for (var index = 0; index < HistoricalEventCount; index++)
        {
            var timestamp = sessionStart.AddSeconds(spacingSeconds * (index + 1));
            events.Add(EntityEvent(
                "com.example.system",
                "SCREEN_INTERACTIVE",
                timestamp,
                $"Synthetic{index:D5}"));
        }

        db.Set<MobileUsageSessionEntity>().Add(session);
        db.Set<MobileUsageEventEntity>().AddRange(events);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task<Guid> SeedDayOverflowSessionAsync(
        PimDbContext db,
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd)
    {
        var session = new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = "com.example.legacy",
            StartUtc = sessionStart,
            EndUtc = sessionEnd,
            DurationMs = (long)(sessionEnd - sessionStart).TotalMilliseconds,
            QualityFlagsJson = "[\"day_overflow\",\"open-ended\",\"anomalous_duration\"]",
            CreatedAt = sessionStart
        };
        db.Set<MobileUsageSessionEntity>().Add(session);
        db.Set<MobileUsageEventEntity>().Add(EntityEvent(
            "com.example.legacy",
            "MOVE_TO_FOREGROUND",
            sessionStart,
            "LegacyActivity"));
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static MobileUsageEventsUploadRequest TailRequest(DateTimeOffset start)
        => new(
            DeviceId,
            "batch-issue-539-tail",
            start,
            start.AddMilliseconds(1),
            [],
            [
                ClientEvent("com.example.messages", "MOVE_TO_BACKGROUND", start.AddTicks(1_000), "Tail1"),
                ClientEvent("com.example.chat", "MOVE_TO_FOREGROUND", start.AddTicks(2_000), "Tail2"),
                ClientEvent("com.example.chat", "MOVE_TO_BACKGROUND", start.AddTicks(3_000), "Tail3"),
                ClientEvent("com.example.messages", "MOVE_TO_FOREGROUND", start.AddTicks(4_000), "Tail4"),
                ClientEvent("com.example.messages", "SCREEN_INTERACTIVE", start.AddTicks(5_000), "Tail5")
            ],
            []);

    private static MobileUsageEventDto ClientEvent(
        string packageName,
        string eventType,
        DateTimeOffset timestamp,
        string clientItemKey)
        => new(
            packageName,
            eventType,
            timestamp,
            $"{clientItemKey}Activity",
            timestamp,
            "{}",
            clientItemKey);

    private static MobileUsageEventEntity EntityEvent(
        string packageName,
        string eventType,
        DateTimeOffset timestamp,
        string className)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = packageName,
            EventType = eventType,
            EventTimestampUtc = timestamp,
            ClassName = className,
            SourceWindowStartUtc = new DateTimeOffset(timestamp.UtcDateTime.Date, TimeSpan.Zero),
            SourceWindowEndUtc = new DateTimeOffset(timestamp.UtcDateTime.Date.AddDays(1), TimeSpan.Zero),
            CollectedAtUtc = timestamp,
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = timestamp
        };

    private static MobileUsageIngestService CreateIngest(
        PimDbContext db,
        DateTimeOffset now,
        TimeSpan attemptDeadline,
        Func<CancellationToken, Task>? beforeReconcile = null)
    {
        var timeProvider = MobileTestHelpers.Time(now);
        return new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db, timeProvider)
            {
                BeforeReconcileAsync = beforeReconcile
            },
            timeProvider,
            catalogOverrideService: null,
            materializationService: CreateMaterialization(db, now))
        {
            AttemptDeadline = attemptDeadline
        };
    }

    private static MobileAnalyticsMaterializationService CreateMaterialization(
        PimDbContext db,
        DateTimeOffset now)
    {
        var timeProvider = MobileTestHelpers.Time(now);
        var currentUser = MobileTestHelpers.CurrentUser();
        return new MobileAnalyticsMaterializationService(
            db,
            new MobileUsageAggregationService(
                db,
                currentUser,
                new MobileAnalyticsQueryService(timeProvider),
                new MobileUsageGoalService(db, currentUser, timeProvider),
                timeProvider,
                new MobileAppClassificationService(db, currentUser)),
            new MobileTimelineBlockService(
                db,
                currentUser,
                timeProvider,
                new MobileAppClassificationService(db, currentUser)),
            timeProvider);
    }

    private static async Task<TempMigrationDatabase> CreateFixtureAsync()
    {
        var baseConnectionString = ResolveBaseConnectionString();
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

        var source = new NpgsqlConnectionStringBuilder(baseConnectionString);
        var isolated = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        if (!string.Equals(source.Host, isolated.Host, StringComparison.OrdinalIgnoreCase)
            || source.Port != isolated.Port
            || !isolated.Database.StartsWith($"{TempDatabasePrefix}_", StringComparison.Ordinal))
        {
            await fixture.DisposeAsync();
            throw new InvalidOperationException(
                "Issue 539 fixture did not create a database on the configured PostgreSQL server.");
        }

        return fixture;
    }

    private static string ResolveBaseConnectionString()
    {
        var value = RealDbTestConnection.Require();
        var builder = new NpgsqlConnectionStringBuilder(value);
        var loopback = string.Equals(builder.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        var localOwned = loopback && builder.Port == LocalPort
            && string.Equals(builder.Database, LocalDatabase, StringComparison.Ordinal)
            && string.Equals(builder.Username, LocalUser, StringComparison.Ordinal);
        var ciEphemeral = loopback && builder.Port == CiPort
            && string.Equals(builder.Database, CiDatabase, StringComparison.Ordinal)
            && string.Equals(builder.Username, CiPostgresUser, StringComparison.Ordinal)
            && string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        if (!localOwned && !ciEphemeral)
        {
            throw new InvalidOperationException(
                $"Issue 539 requires local owned {LocalPort}/{LocalDatabase}, or the GitHub Actions "
                + $"ephemeral {CiPort}/{CiDatabase} as {CiPostgresUser} (GITHUB_ACTIONS=true).");
        }

        return builder.ConnectionString;
    }

    private static PimDbContext CreateDbContext(string connectionString, EventQueryObserver observer)
    {
        MobileTestHelpers.RegisterMobileModule();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .AddInterceptors(observer)
            .Options;
        return new PimDbContext(options);
    }

    private static string Collapse(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record EventQueryObservation(string CommandText)
    {
        public int RowsObserved;
    }

    private sealed class EventQueryObserver : DbCommandInterceptor
    {
        private readonly List<EventQueryObservation> _observations = [];

        public IReadOnlyList<EventQueryObservation> Observations
        {
            get
            {
                lock (_observations)
                    return _observations.ToList();
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(result);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Wrap(command, result));

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
            => result;

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
            => Wrap(command, result);

        private DbDataReader Wrap(DbCommand command, DbDataReader reader)
        {
            var commandText = command.CommandText.TrimStart();
            if (!commandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || !commandText.Contains("FROM mobile_usage_events", StringComparison.OrdinalIgnoreCase))
                return reader;

            var observation = new EventQueryObservation(command.CommandText);
            lock (_observations)
                _observations.Add(observation);
            return new CountingDbDataReader(reader, () => Interlocked.Increment(ref observation.RowsObserved));
        }
    }

    private sealed class CountingDbDataReader(DbDataReader inner, Action rowRead) : DbDataReader
    {
        public override int FieldCount => inner.FieldCount;
        public override int VisibleFieldCount => inner.VisibleFieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override int Depth => inner.Depth;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
            => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
            => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
            => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override Stream GetStream(int ordinal) => inner.GetStream(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override TextReader GetTextReader(int ordinal) => inner.GetTextReader(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
            => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
            => inner.NextResultAsync(cancellationToken);

        public override bool Read()
        {
            var read = inner.Read();
            if (read)
                rowRead();
            return read;
        }

        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(cancellationToken);
            if (read)
                rowRead();
            return read;
        }

        public override void Close() => inner.Close();

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

[CollectionDefinition("Mobile539SyntheticPg", DisableParallelization = true)]
public sealed class Mobile539SyntheticPgCollection;
