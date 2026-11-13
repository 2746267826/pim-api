using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// #568 真库回归：回收器与中断写回都落在 SQL 上（jsonb 条件写 + 时间窗比较），
/// InMemory 既不翻译这些语句也不执行 pg_advisory_xact_lock，必须在 Postgres 上验。
///
/// 无 <c>PIM_TEST_CONN</c> 时用 <c>Skip.If</c> 显式跳过（报告里显示 Skipped），不静默通过。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class MobileSyncIssue568RealDbTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T09:00:00Z");
    private const string DeviceId = "android-issue-568-real";

    [SkippableFact]
    public async Task Reaper_ReleasesOnlyExpiredLeases_AndArchivesOnlyUnrecoverableWindows()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;

        var recent = Now.AddHours(-6);
        var coveredWindow = Now.AddHours(-10);
        var outOfHorizon = Now.AddDays(-20);

        var activeLease = await SeedAsync(db, "reaper-active", recent, Now.AddMinutes(5), null);
        var expiredLease = await SeedAsync(db, "reaper-expired", recent, Now.AddMinutes(-40), null);
        var retryCooldown = await SeedAsync(db, "reaper-cooldown", recent, Now.AddMinutes(-40), Now.AddMinutes(10));
        var archivedByHorizon = await SeedAsync(db, "reaper-horizon", outOfHorizon, leaseUntilUtc: null, retryAfterUtc: null);
        var archivedByCoverage = await SeedAsync(db, "reaper-covered", coveredWindow, leaseUntilUtc: null, retryAfterUtc: null);
        await SeedCompletedAsync(db, "reaper-covered-winner", coveredWindow);

        var reaper = BuildReaper(database);
        var summary = await reaper.SweepOnceAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Set<MobileSyncBatchEntity>().AsNoTracking().ToListAsync();
        MobileSyncBatchEntity Row(string batchId) => rows.Single(row => row.BatchId == batchId);

        // 活动租约：一个字节都不许动（处理者还在跑）。
        var active = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(Row(activeLease).ErrorJson);
        Assert.Equal(MobileSyncBatchStatus.Pending, Row(activeLease).Status);
        Assert.Equal(Now.AddMinutes(5), active.LeaseUntilUtc);

        // 过期租约：只擦租约，不判死——判死会让客户端重投撞 409 并永久卡住该窗口。
        var expired = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(Row(expiredLease).ErrorJson);
        Assert.Equal(MobileSyncBatchStatus.Pending, Row(expiredLease).Status);
        Assert.Null(expired.LeaseUntilUtc);
        Assert.NotNull(expired.RetryAfterUtc);

        // 重试冷却未到：同样不许动。
        var cooldown = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(Row(retryCooldown).ErrorJson);
        Assert.Equal(MobileSyncBatchStatus.Pending, Row(retryCooldown).Status);
        Assert.Equal(Now.AddMinutes(10), cooldown.RetryAfterUtc);

        // 超出补传期 / 同窗口已被成功批次覆盖：归档。
        foreach (var archivedId in new[] { archivedByHorizon, archivedByCoverage })
        {
            var row = Row(archivedId);
            var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(row.ErrorJson);
            Assert.Equal(MobileSyncBatchStatus.Abandoned, row.Status);
            Assert.Equal(0, row.FailedCount);
            Assert.NotNull(row.CompletedAtUtc);
            Assert.Equal("abandoned-lease", envelope.FailureCode);
        }

        Assert.Equal(1, summary.ReleasedLeases);
        Assert.Equal(2, summary.ArchivedBatches);

        // 归档成 abandoned（而不是 failed）必须仍然满足 INV-M21 / S11。
        var invariant = DataReliabilityInvariants.CheckS11_StatusSemantics(
            rows.Select(row => new BatchSyncStatusRecord
            {
                BatchId = row.BatchId,
                Status = row.Status,
                AcceptedCount = row.AcceptedCount,
                FailedCount = row.FailedCount,
                RejectedCount = row.RejectedCount,
                SkippedCount = row.SkippedCount,
                WindowStartUtc = row.WindowStartUtc.UtcDateTime,
                TotalCount = row.AcceptedCount + row.FailedCount + row.RejectedCount + row.SkippedCount
            }),
            referenceTimeUtc: Now.UtcDateTime);
        Assert.True(invariant.Pass, invariant.Detail);
    }

    [SkippableFact]
    public async Task Reaper_DoesNotArchiveABatchThatIsStillProcessing()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;

        // 在途批次：持有未来租约，且窗口既超出补传期、又同窗口已有成功批次——
        // 这正是"补偿重投"的常见形状，回收器绝不能把它归档掉（否则跑完的事务会被 LeaseLost 回滚）。
        var outOfHorizon = Now.AddDays(-20);
        await SeedAsync(db, "reaper-inflight-horizon", outOfHorizon, Now.AddMinutes(10), null);
        await SeedCompletedAsync(db, "reaper-inflight-winner", outOfHorizon);
        await SeedAsync(db, "reaper-inflight-covered", outOfHorizon, Now.AddMinutes(10), null);

        var summary = await BuildReaper(database).SweepOnceAsync(CancellationToken.None);

        Assert.Equal(0, summary.ReleasedLeases);
        Assert.Equal(0, summary.ArchivedBatches);

        db.ChangeTracker.Clear();
        var rows = await db.Set<MobileSyncBatchEntity>().AsNoTracking().ToListAsync();
        foreach (var row in rows.Where(row => row.Status == MobileSyncBatchStatus.Pending))
        {
            var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(row.ErrorJson);
            Assert.Equal(Now.AddMinutes(10), envelope.LeaseUntilUtc);
        }
    }

    [SkippableFact]
    public async Task Reaper_IsIdempotentAcrossRepeatedSweeps()
    {
        await using var database = await TempDatabase.CreateAsync();
        await using var db = database.Db;

        await SeedAsync(db, "reaper-idempotent", Now.AddHours(-6), Now.AddMinutes(-40), null);

        var reaper = BuildReaper(database);
        var first = await reaper.SweepOnceAsync(CancellationToken.None);
        var second = await reaper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.ReleasedLeases);
        Assert.Equal(0, second.ReleasedLeases);
        Assert.Equal(0, second.ArchivedBatches);
    }

    [SkippableFact]
    public async Task ClientCancellation_OnPostgres_ReleasesTheLeaseImmediately()
    {
        await using var database = await TempDatabase.CreateAsync();
        var request = Request("android-pg-abort", Now.AddHours(-18));

        using (var cts = new CancellationTokenSource())
        {
            await using var aborting = new AbortAfterFirstSaveDbContext(database.Options, cts);
            var service = CreateIngest(aborting);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.IngestAsync(request, cts.Token));
        }

        await using var verify = new PimDbContext(database.Options);
        var batch = await verify.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == request.BatchId);
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);

        Assert.Equal(MobileSyncBatchStatus.Pending, batch.Status);
        Assert.Null(envelope.LeaseUntilUtc);
        Assert.Equal(0, envelope.AttemptCount);
        Assert.Equal("processing-interrupted", envelope.FailureCode);
        Assert.False(MobileSyncBatchEnvelopeCodec.HasActiveLease(batch.CreatedAt, batch.ErrorJson, Now));
    }

    [SkippableFact]
    public async Task TimingWriter_PersistsTheBreakdownAndPrunesByRetention()
    {
        await using var database = await TempDatabase.CreateAsync();
        var writer = database.TimingWriter;

        await writer.TryRecordAsync(new MobileSyncIngestTimingRecord(
            MobileTestHelpers.UserId,
            DeviceId,
            "android-timing",
            Now.AddHours(-2),
            Now.AddHours(-1),
            Attempt: 2,
            Outcome: "completed",
            ConnectionWaitMs: 12,
            AdvisoryLockWaitMs: 2500,
            SessionRebuildMs: 400,
            MaterializeMs: 900,
            ComputeMs: 1200,
            CommitMs: 30,
            TotalMs: 4100));

        await using var db = new PimDbContext(database.Options);
        var row = await db.Set<MobileSyncIngestTimingEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(2500, row.AdvisoryLockWaitMs);
        Assert.Equal("completed", row.Outcome);
        Assert.Equal(4100, row.TotalMs);

        // 超出保留期的行会被清掉。
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE mobile_sync_ingest_timings SET created_at = {Now.AddDays(-31)}");

        var pruned = await writer.PruneAsync(TimeSpan.FromDays(30), CancellationToken.None);
        Assert.Equal(1, pruned);
        Assert.Equal(0, await db.Set<MobileSyncIngestTimingEntity>().CountAsync());
    }

    private static MobileSyncBatchReaper BuildReaper(TempDatabase database)
        => new(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            database.TimingWriter,
            MobileTestHelpers.Time(Now),
            NullLogger<MobileSyncBatchReaper>.Instance);

    private static MobileUsageIngestService CreateIngest(PimDbContext db) => new(
        db,
        MobileTestHelpers.CurrentUser(),
        new MobileSessionInterpreter(db, MobileTestHelpers.Time(Now)),
        MobileTestHelpers.Time(Now));

    private static MobileUsageEventsUploadRequest Request(string batchId, DateTimeOffset windowStart) => new(
        DeviceId,
        batchId,
        windowStart,
        windowStart.AddMinutes(15),
        [],
        [
            new MobileUsageEventDto(
                "com.example.messages",
                "MOVE_TO_FOREGROUND",
                windowStart.AddMinutes(1),
                "MainActivity",
                windowStart.AddMinutes(1),
                "{}",
                "item-1")
        ],
        []);

    private static async Task<string> SeedAsync(
        PimDbContext db,
        string batchId,
        DateTimeOffset windowStart,
        DateTimeOffset? leaseUntilUtc,
        DateTimeOffset? retryAfterUtc)
    {
        var envelope = MobileSyncBatchEnvelopeCodec.Serialize(
            [],
            [],
            payloadSha256: null,
            leaseUntilUtc,
            attemptCount: 1,
            failureCode: null,
            leaseId: Guid.NewGuid().ToString("N"),
            retryAfterUtc);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = batchId,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowStart.AddMinutes(15),
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = envelope,
            CreatedAt = windowStart.AddHours(1)
        });
        await db.SaveChangesAsync();
        return batchId;
    }

    private static async Task SeedCompletedAsync(PimDbContext db, string batchId, DateTimeOffset windowStart)
    {
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = batchId,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowStart.AddMinutes(15),
            Status = MobileSyncBatchStatus.Completed,
            AcceptedCount = 3,
            FailedCount = 0,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize([], []),
            CreatedAt = windowStart.AddHours(2),
            CompletedAtUtc = windowStart.AddHours(2)
        });
        await db.SaveChangesAsync();
    }

    private sealed class AbortAfterFirstSaveDbContext(
        DbContextOptions<PimDbContext> options,
        CancellationTokenSource abort) : PimDbContext(options)
    {
        private int _saves;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var written = await base.SaveChangesAsync(cancellationToken);
            if (Interlocked.Increment(ref _saves) == 1)
                abort.Cancel();
            return written;
        }
    }

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _database;
        private readonly NpgsqlConnection _admin;

        private TempDatabase(
            string database,
            NpgsqlConnection admin,
            DbContextOptions<PimDbContext> options,
            ServiceProvider services)
        {
            _database = database;
            _admin = admin;
            Options = options;
            Db = new PimDbContext(options);
            Services = services;
            TimingWriter = new MobileSyncIngestTimingWriter(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MobileSyncIngestTimingWriter>.Instance);
        }

        public PimDbContext Db { get; }

        public DbContextOptions<PimDbContext> Options { get; }

        public ServiceProvider Services { get; }

        public MobileSyncIngestTimingWriter TimingWriter { get; }

        public static async Task<TempDatabase> CreateAsync()
        {
            var connStr = RealDbTestConnection.Require();
            MobileTestHelpers.RegisterMobileModule();
            var admin = new NpgsqlConnection(connStr);
            await admin.OpenAsync();

            var database = $"pim_test_qa_issue568_{Guid.NewGuid():N}";
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
                await create.ExecuteNonQueryAsync();

            var connectionString = new NpgsqlConnectionStringBuilder(connStr) { Database = database }.ConnectionString;
            var options = new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(connectionString).Options;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<PimDbContext>(builder => builder.UseNpgsql(connectionString));

            var temp = new TempDatabase(database, admin, options, services.BuildServiceProvider());
            await temp.Db.Database.EnsureCreatedAsync();
            return temp;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Services.DisposeAsync();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", _admin))
                await drop.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }
    }
}
