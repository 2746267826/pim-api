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

/// <summary>
/// Synthetic PostgreSQL regressions for the #539/#551 sync convergence path.
/// The suite only targets the explicitly owned localhost synthetic cluster and
/// creates/drops its own database through TempMigrationDatabase.
/// </summary>
[Collection("MobileSyncConvergenceSyntheticPg")]
[Trait("DataSource", "RealDb")]
public sealed class MobileSyncConvergenceRealDbTests
{
    private const string OptInVariable = "PIM_RUN_SYNC_CONVERGENCE_SYNTHETIC_PG";
    private const string TempDatabasePrefix = "sync_convergence";
    private const string OwnedDatabase = "postgres";
    private const int OwnedPort = 35439;
    private const string OwnedUser = "pim_roadmap_test";
    private const string DeviceId = "synthetic-sync-convergence";
    private const int HistoricalEventCount = 5_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-11-01T06:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal
        | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private readonly ITestOutputHelper _output;

    public MobileSyncConvergenceRealDbTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task GapQuery_OnPostgres_ReturnsOnlyTheRealShortHoles()
    {
        var fixture = await CreateFixtureAsync();
        await using var _ = fixture;
        var db = fixture.Db;
        var rangeStart = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        var rangeEnd = DateTimeOffset.Parse("2026-11-01T06:00:00Z");
        var now = rangeEnd;

        AddCoverageEvent(
            db,
            "coverage-head-a",
            rangeStart,
            rangeStart.AddHours(3));
        AddCoverageEvent(
            db,
            "coverage-head-b",
            rangeStart,
            rangeStart.AddHours(3));
        AddCoverageEvent(
            db,
            "coverage-middle-a",
            rangeStart.AddHours(3).AddMinutes(12),
            DateTimeOffset.Parse("2026-10-31T04:00:00Z"));
        AddCoverageEvent(
            db,
            "coverage-middle-b",
            DateTimeOffset.Parse("2026-10-31T04:20:00Z"),
            DateTimeOffset.Parse("2026-11-01T05:00:00Z"));
        AddCoverageEvent(
            db,
            "coverage-tail",
            DateTimeOffset.Parse("2026-11-01T05:30:00Z"),
            rangeEnd);
        await db.SaveChangesAsync();

        var started = DateTimeOffset.UtcNow;
        var service = new MobileGapService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(now));
        var response = await service.GetGapsAsync(
            new MobileGapRequest(
                DeviceId,
                rangeStart,
                rangeEnd,
                "{\"usageEvents\":true}"),
            CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(3, response.Windows.Count);
        Assert.Equal(
            [
                DateTimeOffset.Parse("2026-10-30T03:00:00Z"),
                DateTimeOffset.Parse("2026-10-31T04:00:00Z"),
                DateTimeOffset.Parse("2026-11-01T05:00:00Z")
            ],
            response.Windows.Select(window => window.WindowStartUtc));
        Assert.Equal(
            [
                rangeStart.AddHours(3).AddMinutes(12),
                DateTimeOffset.Parse("2026-10-31T04:20:00Z"),
                DateTimeOffset.Parse("2026-11-01T05:30:00Z")
            ],
            response.Windows.Select(window => window.WindowEndUtc));
        Assert.Equal(
            62,
            response.Windows.Sum(window =>
                (window.WindowEndUtc - window.WindowStartUtc).TotalMinutes));

        _output.WriteLine(
            "PG_GAP sourceRows=5 duplicateWindows=1 gaps={0} gapMinutes=62 elapsedMs={1:F1}",
            response.Windows.Count,
            elapsed.TotalMilliseconds);
    }

    [SkippableFact]
    public async Task WidenedDuplicate_OnPostgres_ExtendsOnlyTheOpenSessionAndDoesNotRereadHistory()
    {
        var fixture = await CreateFixtureAsync();
        await using var _ = fixture;
        var observer = new EventQueryObserver();
        await using var db = CreateDbContext(fixture.ConnectionString, observer);
        var start = Now.AddHours(-5);
        var openStart = start.AddMinutes(10);
        var oldEnd = start.AddMinutes(15);

        var closedSession = new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = "com.example.messages",
            StartUtc = start,
            EndUtc = openStart,
            DurationMs = (long)(openStart - start).TotalMilliseconds,
            QualityFlagsJson = "[\"closed-by-app-switch\"]",
            CreatedAt = start
        };
        var openSession = new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = "com.example.chat",
            StartUtc = openStart,
            EndUtc = oldEnd,
            DurationMs = (long)(oldEnd - openStart).TotalMilliseconds,
            QualityFlagsJson = "[\"open-ended\"]",
            CreatedAt = openStart
        };
        db.Set<MobileUsageSessionEntity>().AddRange(closedSession, openSession);
        db.Set<MobileUsageEventEntity>().Add(
            Event(
                "com.example.chat",
                "MOVE_TO_FOREGROUND",
                openStart,
                "ChatActivity",
                start,
                oldEnd));
        var historical = new List<MobileUsageEventEntity>(HistoricalEventCount);
        var spacing = Math.Max(1, (oldEnd - openStart).TotalMilliseconds / (HistoricalEventCount + 1));
        for (var index = 0; index < HistoricalEventCount; index++)
        {
            historical.Add(Event(
                "com.example.system",
                "SCREEN_INTERACTIVE",
                openStart.AddMilliseconds(spacing * (index + 1)),
                $"Synthetic{index:D5}",
                start,
                oldEnd));
        }
        db.Set<MobileUsageEventEntity>().AddRange(historical);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var staleService = new MobileAppCatalogOverrideService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));
        var materialization = CreateMaterialization(db);
        await materialization.MaterializeAsync(
            MobileTestHelpers.UserId,
            DeviceId,
            start,
            oldEnd,
            CancellationToken.None);
        db.ChangeTracker.Clear();
        var baselineAggregates = await db.Set<MobileUsageAggregateEntity>()
            .AsNoTracking()
            .ToListAsync();
        var baselineBlocks = await db.Set<MobileTimelineBlockEntity>()
            .AsNoTracking()
            .ToListAsync();
        Assert.NotEmpty(baselineAggregates);
        Assert.NotEmpty(baselineBlocks);
        var baselineAggregateSeconds = baselineAggregates.Sum(row => row.ForegroundSeconds);
        var baselineBlockSeconds = baselineBlocks.Sum(row => row.ForegroundSeconds);

        var request = new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-sync-convergence-widened",
            start,
            oldEnd,
            [],
            [
                new MobileUsageEventDto(
                    "com.example.chat",
                    "MOVE_TO_FOREGROUND",
                    openStart,
                    "ChatActivity",
                    openStart.AddMinutes(1),
                    "{}",
                    "duplicate-open-event")
            ],
            []);
        var reconcileCount = 0;
        var service = CreateIngest(
            db,
            staleService,
            materialization,
            _ =>
            {
                reconcileCount++;
                return Task.CompletedTask;
            });
        var unchanged = await service.IngestAsync(request, CancellationToken.None);
        Assert.Equal(0, unchanged.AcceptedCount);
        Assert.Equal(0, reconcileCount);
        db.ChangeTracker.Clear();
        Assert.Equal(
            baselineAggregateSeconds,
            await db.Set<MobileUsageAggregateEntity>()
                .AsNoTracking()
                .SumAsync(row => row.ForegroundSeconds));
        Assert.Equal(
            baselineBlockSeconds,
            await db.Set<MobileTimelineBlockEntity>()
                .AsNoTracking()
                .SumAsync(row => row.ForegroundSeconds));

        var widenedRequest = request with
        {
            ClientBatchId = "batch-sync-convergence-widened-2",
            SourceWindowEndUtc = start.AddHours(4)
        };
        var started = DateTimeOffset.UtcNow;
        var result = await service.IngestAsync(widenedRequest, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, reconcileCount);
        Assert.Equal(1, result.SkippedCount);
        db.ChangeTracker.Clear();
        var sessions = await db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .OrderBy(session => session.StartUtc)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(closedSession.Id, sessions[0].Id);
        Assert.Equal(openSession.Id, sessions[1].Id);
        Assert.Equal(openStart, sessions[1].StartUtc);
        Assert.Equal(start.AddHours(4), sessions[1].EndUtc);

        var refreshedAggregates = await db.Set<MobileUsageAggregateEntity>()
            .AsNoTracking()
            .ToListAsync();
        var refreshedBlocks = await db.Set<MobileTimelineBlockEntity>()
            .AsNoTracking()
            .ToListAsync();
        Assert.NotEmpty(refreshedAggregates);
        Assert.NotEmpty(refreshedBlocks);
        Assert.True(refreshedAggregates.Sum(row => row.ForegroundSeconds) > baselineAggregateSeconds);
        Assert.True(refreshedBlocks.Sum(row => row.ForegroundSeconds) > baselineBlockSeconds);
        Assert.All(refreshedAggregates, row => Assert.False(row.IsStale));
        Assert.All(refreshedBlocks, row => Assert.False(row.IsStale));
        var refreshedAggregateIds = refreshedAggregates.Select(row => row.Id).ToArray();
        var refreshedAggregateSeconds = refreshedAggregates.Sum(row => row.ForegroundSeconds);

        var repeated = widenedRequest with { ClientBatchId = "batch-sync-convergence-widened-3" };
        var repeatedResult = await service.IngestAsync(repeated, CancellationToken.None);
        Assert.Equal(0, repeatedResult.AcceptedCount);
        db.ChangeTracker.Clear();
        Assert.Equal(
            refreshedAggregateIds,
            await db.Set<MobileUsageAggregateEntity>()
                .AsNoTracking()
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .ToArrayAsync());
        Assert.Equal(
            refreshedAggregateSeconds,
            await db.Set<MobileUsageAggregateEntity>()
                .AsNoTracking()
                .SumAsync(row => row.ForegroundSeconds));

        var batch = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == request.BatchId);
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
        Assert.Equal(0, batch.AcceptedCount);

        var eventObservations = observer.Observations.Where(item => item.RowsObserved > 0).ToList();
        var maxRowsObserved = eventObservations
            .Select(item => item.RowsObserved)
            .DefaultIfEmpty(0)
            .Max();
        _output.WriteLine(
            "PG_DUPLICATE_WIDEN sessions=2 openId={0} end={1:O} accepted=0 skipped=1 eventSelectMaxRows={2} elapsedMs={3:F1} aggregateSeconds={4}->{5} blockSeconds={6}->{7}",
            sessions[1].Id,
            sessions[1].EndUtc,
            maxRowsObserved,
            elapsed.TotalMilliseconds,
            baselineAggregateSeconds,
            refreshedAggregates.Sum(row => row.ForegroundSeconds),
            baselineBlockSeconds,
            refreshedBlocks.Sum(row => row.ForegroundSeconds));
        Assert.True(
            maxRowsObserved <= 1,
            $"Widened duplicate read {maxRowsObserved} event rows; expected only the duplicate lookup, not history.");

        var gapService = new MobileGapService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));
        var gaps = await gapService.GetGapsAsync(
            new MobileGapRequest(DeviceId, start, start.AddHours(4), "{}"),
            CancellationToken.None);
        Assert.Empty(gaps.Windows);
    }

    private static async Task<TempMigrationDatabase> CreateFixtureAsync()
    {
        RequireOptedInSyntheticCluster();
        MobileTestHelpers.RegisterMobileModule();
        var fixture = await TempMigrationDatabase.CreateAsync(TempDatabasePrefix);
        try
        {
            await fixture.MigrateAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static void RequireOptedInSyntheticCluster()
    {
        var optedIn = Environment.GetEnvironmentVariable(OptInVariable);
        Skip.If(
            !string.Equals(optedIn, "true", StringComparison.OrdinalIgnoreCase),
            $"Synthetic sync convergence tests require {OptInVariable}=true.");

        var value = RealDbTestConnection.FromEnvironment;
        Skip.If(
            string.IsNullOrWhiteSpace(value),
            "RealDb unavailable (PIM_TEST_CONN not set), skipping test.");

        var builder = new NpgsqlConnectionStringBuilder(value);
        var loopback = string.Equals(builder.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        var owned = loopback
            && builder.Port == OwnedPort
            && string.Equals(builder.Database, OwnedDatabase, StringComparison.Ordinal)
            && string.Equals(builder.Username, OwnedUser, StringComparison.Ordinal);
        if (!owned)
        {
            throw new InvalidOperationException(
                $"Synthetic sync convergence tests require owned localhost:{OwnedPort}/{OwnedDatabase} as {OwnedUser}.");
        }

        builder.MaxPoolSize = 5;
        Environment.SetEnvironmentVariable(
            "PIM_TEST_CONN",
            builder.ConnectionString,
            EnvironmentVariableTarget.Process);
        RealDbTestConnection.Require();
    }

    private static PimDbContext CreateDbContext(
        string connectionString,
        EventQueryObserver observer)
    {
        MobileTestHelpers.RegisterMobileModule();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .AddInterceptors(observer)
            .Options;
        return new PimDbContext(options);
    }

    private static MobileUsageIngestService CreateIngest(PimDbContext db)
        => CreateIngest(db, null, null);

    private static MobileUsageIngestService CreateIngest(
        PimDbContext db,
        MobileAppCatalogOverrideService? staleService,
        MobileAnalyticsMaterializationService? materializationService,
        Func<CancellationToken, Task>? beforeReconcile = null)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        return new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db, timeProvider)
            {
                BeforeReconcileAsync = beforeReconcile
            },
            timeProvider,
            staleService,
            materializationService)
        {
            AttemptDeadline = TimeSpan.FromSeconds(10)
        };
    }

    private static MobileAnalyticsMaterializationService CreateMaterialization(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
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

    private static void AddCoverageEvent(
        PimDbContext db,
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        db.Set<MobileUsageEventEntity>().Add(
            Event(
                packageName,
                "SCREEN_INTERACTIVE",
                start.AddMinutes(1),
                $"{packageName}-activity",
                start,
                end));
    }

    private static MobileUsageEventEntity Event(
        string packageName,
        string eventType,
        DateTimeOffset timestamp,
        string className,
        DateTimeOffset sourceStart,
        DateTimeOffset sourceEnd)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            PackageName = packageName,
            EventType = eventType,
            EventTimestampUtc = timestamp,
            ClassName = className,
            SourceWindowStartUtc = sourceStart,
            SourceWindowEndUtc = sourceEnd,
            CollectedAtUtc = timestamp,
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = timestamp
        };

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

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Wrap(command, result));

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
            return new CountingDbDataReader(
                reader,
                () => Interlocked.Increment(ref observation.RowsObserved));
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
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length)
            => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length)
            => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(
            int ordinal,
            CancellationToken cancellationToken)
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
        public override Task<bool> IsDBNullAsync(
            int ordinal,
            CancellationToken cancellationToken)
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

[CollectionDefinition("MobileSyncConvergenceSyntheticPg", DisableParallelization = true)]
public sealed class MobileSyncConvergenceSyntheticPgCollection;
