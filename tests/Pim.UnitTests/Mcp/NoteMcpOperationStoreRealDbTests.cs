using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Real PostgreSQL regression for the native-note request-id ledger: migration shape, the
/// composite identity, 200-response/30-day retention, pending/unknown tombstones and
/// concurrent/restarted API instances.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class NoteMcpOperationStoreRealDbTests
{
    [SkippableFact]
    public async Task LedgerMigrationRetentionAndReplayKeepIdentity()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_retention");
        await database.MigrateAsync();
        Assert.True(await database.TableExistsAsync("note_mcp_operations"));
        Assert.True(await database.IsUniqueIndexAsync("PK_note_mcp_operations"));

        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var store = new NoteMcpOperationStore(new PostgresNoteMcpOperationLedger(options), clock);
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var calls = 0;
        var requestIds = Enumerable.Range(0, 205).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var requestId in requestIds)
        {
            await RunAsync(store, owner, noteId, requestId, new { value = requestId }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, requestId, clock, "applied"));
            });
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(205, calls);
        Assert.Equal(205, await database.Db.NoteMcpOperations
            .CountAsync(x => x.OwnerId == owner));
        var first = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == requestIds[0]);
        Assert.Equal("completed", first.State);
        Assert.False(string.IsNullOrWhiteSpace(first.Fingerprint));
        Assert.Null(first.ResponseJson);
        Assert.NotNull(first.ResponsePurgedAt);

        var expired = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, requestIds[0], new { value = requestIds[0] }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, requestIds[0], clock, "must not run"));
            }));
        Assert.Equal(40901, expired.ErrorCode);
        Assert.Contains("不再保留", expired.Message);
        Assert.Equal("applied", expired.Receipt.Status);
        Assert.Equal(40901, expired.Receipt.ErrorCode);
        Assert.Equal(205, calls);

        var status = await store.GetAsync(owner, noteId, requestIds[0], default);
        Assert.Equal("succeeded", status.State);
        Assert.Equal("applied", status.Receipt.Status);
        Assert.Contains("不再保留", status.Receipt.Message);
        Assert.Equal(NoteMcpOperationStore.BuildOperationId(owner, "test_operation",
            requestIds[0]), status.OperationId);

        var conflict = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, requestIds[0], new { value = "different" }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, requestIds[0], clock, "must not run"));
            }));
        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(205, calls);

        var recent = await RunAsync(store, owner, noteId, requestIds[^1],
            new { value = requestIds[^1] }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, requestIds[^1], clock, "must not run"));
            });
        Assert.True(recent.Receipt.Replayed);
        Assert.Equal(205, calls);

        var pendingId = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(store, owner, noteId, pendingId, new { value = 1 }, _ =>
            {
                calls++;
                throw new OperationCanceledException("synthetic pending");
            }));
        var unknownId = Guid.NewGuid();
        await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, unknownId, new { value = 2 }, _ =>
            {
                calls++;
                throw new InvalidOperationException("synthetic unknown");
            }));
        var failedId = Guid.NewGuid();
        await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, failedId, new { value = 4 }, _ =>
            {
                calls++;
                throw new DomainException(40901, "synthetic failure");
            }));

        clock.Advance(TimeSpan.FromDays(31));
        var triggerId = Guid.NewGuid();
        await RunAsync(store, owner, noteId, triggerId, new { value = 3 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(owner, noteId, triggerId, clock, "trigger"));
        });

        var pendingRow = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == pendingId);
        var unknownRow = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == unknownId);
        var failedRow = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == failedId);
        Assert.Equal("pending", pendingRow.State);
        Assert.Equal("unknown", unknownRow.State);
        Assert.Equal("failed", failedRow.State);
        Assert.False(string.IsNullOrWhiteSpace(pendingRow.Fingerprint));
        Assert.False(string.IsNullOrWhiteSpace(unknownRow.Fingerprint));
        Assert.False(string.IsNullOrWhiteSpace(failedRow.Fingerprint));

        var pendingRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, pendingId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, pendingId, clock, "must not run"));
            }));
        Assert.Equal("unknown", pendingRetry.Receipt.Status);
        var unknownRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, unknownId, new { value = 2 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, unknownId, clock, "must not run"));
            }));
        Assert.Equal("unknown", unknownRetry.Receipt.Status);
        var failedRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, failedId, new { value = 4 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, failedId, clock, "must not run"));
            }));
        Assert.Equal("synthetic failure", failedRetry.Message);
        Assert.Equal(209, calls);
    }

    [SkippableFact]
    public async Task ExpiredResponseFailsClosedWithoutAnotherSuccessfulMutation()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_expired_read");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var store = new NoteMcpOperationStore(new PostgresNoteMcpOperationLedger(options), clock);
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var calls = 0;

        await RunAsync(store, owner, noteId, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(owner, noteId, requestId, clock, "original"));
        });

        clock.Advance(TimeSpan.FromDays(31));
        var replay = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, requestId, clock, "must not run"));
            }));

        Assert.Equal(40901, replay.ErrorCode);
        Assert.Contains("保留期", replay.Message);
        Assert.Equal(1, calls);
        var expired = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == requestId);
        Assert.Equal("completed", expired.State);
        Assert.Null(expired.ResponseJson);
        Assert.NotNull(expired.ResponsePurgedAt);

        var status = await store.GetAsync(owner, noteId, requestId, default);
        Assert.Equal("succeeded", status.State);
        Assert.Equal("applied", status.Receipt.Status);
        Assert.Contains("超过保留期", status.Receipt.Message);
    }

    [SkippableFact]
    public async Task BulkPurgeClearsExpiredResponsesAndKeepsIdentityAndNewResponse()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_bulk_expiry");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var store = new NoteMcpOperationStore(new PostgresNoteMcpOperationLedger(options), clock);
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var oldRequestId = Guid.NewGuid();
        var newRequestId = Guid.NewGuid();

        await RunAsync(store, owner, noteId, oldRequestId, new { value = "old" }, _ =>
            Task.FromResult(Result(owner, noteId, oldRequestId, clock, "old-response")));
        clock.Advance(TimeSpan.FromDays(31));
        await RunAsync(store, owner, noteId, newRequestId, new { value = "new" }, _ =>
            Task.FromResult(Result(owner, noteId, newRequestId, clock, "new-response")));

        var oldRow = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == oldRequestId);
        Assert.Equal("completed", oldRow.State);
        Assert.Equal(owner, oldRow.OwnerId);
        Assert.Equal(oldRequestId, oldRow.RequestId);
        Assert.Equal(noteId, oldRow.NoteId);
        Assert.Equal("test_operation", oldRow.Operation);
        Assert.Equal(NoteMcpOperationStore.BuildOperationId(owner, "test_operation",
            oldRequestId), oldRow.OperationId);
        Assert.False(string.IsNullOrWhiteSpace(oldRow.Fingerprint));
        Assert.Null(oldRow.ResponseJson);
        Assert.NotNull(oldRow.ResponsePurgedAt);

        var newRow = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == newRequestId);
        Assert.Equal("completed", newRow.State);
        Assert.NotNull(newRow.ResponseJson);
        Assert.Null(newRow.ResponsePurgedAt);

        var replay = await RunAsync(store, owner, noteId, newRequestId, new { value = "new" }, _ =>
            throw new InvalidOperationException("must not execute a retained response"));
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal("new-response", replay.Result);

        var oldStatus = await store.GetAsync(owner, noteId, oldRequestId, default);
        Assert.Equal("succeeded", oldStatus.State);
        Assert.Equal("applied", oldStatus.Receipt.Status);
        Assert.Contains("超过保留期", oldStatus.Receipt.Message);
    }

    [SkippableFact]
    public async Task OwnerCapacityRejectsNewIdentityAndKeepsReplayAndUnknown()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_capacity");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var store = new NoteMcpOperationStore(new PostgresNoteMcpOperationLedger(options), clock,
            maxIdentityRowsPerOwner: 2);
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();
        var calls = 0;

        await RunAsync(store, owner, noteId, completedId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(owner, noteId, completedId, clock, "done"));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(store, owner, noteId, pendingId, new { value = 2 }, _ =>
            {
                calls++;
                throw new OperationCanceledException("synthetic pending");
            }));

        var rejected = await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync(store, owner, noteId, rejectedId, new { value = 3 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, rejectedId, clock, "must not run"));
            }));
        Assert.Equal(40901, rejected.ErrorCode);
        Assert.Contains("容量上限", rejected.Message);
        Assert.DoesNotContain("requestId", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, calls);
        Assert.Equal(2, await database.Db.NoteMcpOperations
            .CountAsync(x => x.OwnerId == owner));
        Assert.False(await database.Db.NoteMcpOperations.AsNoTracking()
            .AnyAsync(x => x.OwnerId == owner && x.RequestId == rejectedId));

        var replay = await RunAsync(store, owner, noteId, completedId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(owner, noteId, completedId, clock, "must not run"));
        });
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal("done", replay.Result);

        var pendingRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, owner, noteId, pendingId, new { value = 2 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(owner, noteId, pendingId, clock, "must not run"));
            }));
        Assert.Equal("unknown", pendingRetry.Receipt.Status);
        Assert.Equal(2, calls);
    }

    [SkippableFact]
    public async Task OwnerCapacityIsAtomicAcrossConcurrentInstances()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_capacity_concurrent");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var calls = 0;
        var requestIds = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        async Task<string> AttemptAsync(Guid requestId, int index)
        {
            var store = new NoteMcpOperationStore(new PostgresNoteMcpOperationLedger(options),
                clock, maxIdentityRowsPerOwner: 3);
            try
            {
                await RunAsync(store, owner, noteId, requestId, new { value = index }, _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(Result(owner, noteId, requestId, clock, "ok"));
                });
                return "inserted";
            }
            catch (DomainException error) when (error.ErrorCode == 40901)
            {
                return "capacity";
            }
        }

        var outcomes = await Task.WhenAll(requestIds.Select(AttemptAsync));
        Assert.Equal(3, outcomes.Count(item => item == "inserted"));
        Assert.Equal(5, outcomes.Count(item => item == "capacity"));
        Assert.Equal(3, calls);
        Assert.Equal(3, await database.Db.NoteMcpOperations
            .CountAsync(x => x.OwnerId == owner));
    }

    [SkippableFact]
    public async Task ExpiredGetReturnsSucceededReceiptWhenResponsePurgeFails()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_expired_get");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var writer = new NoteMcpOperationStore(new PostgresNoteMcpOperationLedger(options), clock);

        await RunAsync(writer, owner, noteId, requestId, new { value = 1 }, _ =>
            Task.FromResult(Result(owner, noteId, requestId, clock, "original")));

        var completed = await database.Db.NoteMcpOperations.SingleAsync(x =>
            x.OwnerId == owner && x.RequestId == requestId);
        completed.CompletedAt = clock.GetUtcNow() - TimeSpan.FromDays(31);
        await database.Db.SaveChangesAsync();

        var reader = new NoteMcpOperationStore(
            new PurgeFailingLedger(new PostgresNoteMcpOperationLedger(options)), clock);
        var status = await reader.GetAsync(owner, noteId, requestId, default);

        Assert.Equal("succeeded", status.State);
        Assert.Equal("applied", status.Receipt.Status);
        Assert.Contains("超过保留期", status.Receipt.Message);
        var retained = await database.Db.NoteMcpOperations.AsNoTracking()
            .SingleAsync(x => x.OwnerId == owner && x.RequestId == requestId);
        Assert.NotNull(retained.ResponseJson);
        Assert.Null(retained.ResponsePurgedAt);
    }

    [SkippableFact]
    public async Task ConcurrentInstancesRestartAndOwnerIsolationShareOneClaim()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_concurrent");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = new NoteMcpOperationStore(
            new PostgresNoteMcpOperationLedger(options), clock);
        var secondStore = new NoteMcpOperationStore(
            new PostgresNoteMcpOperationLedger(options), clock);

        var first = RunAsync(firstStore, owner, noteId, requestId, new { value = 1 }, async _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return Result(owner, noteId, requestId, clock, "first");
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(secondStore, owner, noteId, requestId, new { value = 1 }, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result(owner, noteId, requestId, clock, "second"));
            }));
        Assert.Equal(40901, second.ErrorCode);
        Assert.Equal("unknown", second.Receipt.Status);

        release.TrySetResult();
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("first", completed.Result);
        Assert.Equal(1, calls);

        var restarted = new NoteMcpOperationStore(
            new PostgresNoteMcpOperationLedger(options), clock);
        var replay = await RunAsync(restarted, owner, noteId, requestId, new { value = 1 }, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Result(owner, noteId, requestId, clock, "must not run"));
        });
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(1, calls);

        var otherOwner = Guid.NewGuid();
        var other = await RunAsync(restarted, otherOwner, noteId, requestId, new { value = 1 },
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result(otherOwner, noteId, requestId, clock, "other-owner"));
            });
        Assert.False(other.Receipt.Replayed);
        Assert.Equal(2, calls);
        Assert.Equal(2, await database.Db.NoteMcpOperations
            .CountAsync(x => x.RequestId == requestId));
    }

    [SkippableFact]
    public async Task ConcurrentInstancesClaimTheSameNewRequestIdOnlyOnce()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_mcp_idempotency_first_claim");
        await database.MigrateAsync();
        var options = Options(database.ConnectionString);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var calls = 0;
        var insertBarrier = new InsertBarrier(2);
        var firstStore = new NoteMcpOperationStore(
            new InsertBarrierLedger(new PostgresNoteMcpOperationLedger(options), insertBarrier), clock);
        var secondStore = new NoteMcpOperationStore(
            new InsertBarrierLedger(new PostgresNoteMcpOperationLedger(options), insertBarrier), clock);

        async Task<(NativeNoteMcpOperationResultDto<string>? Result,
            NativeNoteMcpOperationException? Error)> CaptureAsync(
            NoteMcpOperationStore store, string value)
        {
            try
            {
                var result = await RunAsync(store, owner, noteId, requestId,
                    new { value = 1 }, _ =>
                    {
                        Interlocked.Increment(ref calls);
                        return Task.FromResult(Result(owner, noteId, requestId, clock, value));
                    });
                return (result, null);
            }
            catch (NativeNoteMcpOperationException error)
            {
                return (null, error);
            }
        }

        var first = CaptureAsync(firstStore, "first");
        var second = CaptureAsync(secondStore, "second");
        var outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Contains(outcomes, outcome => outcome.Result is not null);
        Assert.All(outcomes, outcome =>
            Assert.True(outcome.Result is not null || outcome.Error?.ErrorCode == 40901));
        Assert.Equal(1, await database.Db.NoteMcpOperations
            .CountAsync(x => x.OwnerId == owner && x.RequestId == requestId));
    }

    private sealed class InsertBarrier(int participants)
    {
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public async Task WaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == participants)
                _released.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class InsertBarrierLedger(INoteMcpOperationLedger inner,
        InsertBarrier barrier) : INoteMcpOperationLedger
    {
        public async Task<NoteMcpOperationClaim> TryInsertAsync(NoteMcpOperationRow row,
            int maxIdentityRowsPerOwner, CancellationToken ct)
        {
            await barrier.WaitAsync(ct);
            return await inner.TryInsertAsync(row, maxIdentityRowsPerOwner, ct);
        }

        public Task<NoteMcpOperationRow?> FindAsync(Guid owner, Guid requestId,
            CancellationToken ct) => inner.FindAsync(owner, requestId, ct);

        public Task<bool> CompleteAsync(Guid owner, Guid requestId, string responseJson,
            string receiptStatus, Guid? revision, string message, DateTimeOffset completedAt,
            CancellationToken ct) => inner.CompleteAsync(owner, requestId, responseJson,
            receiptStatus, revision, message, completedAt, ct);

        public Task<bool> FailAsync(Guid owner, Guid requestId, string state, int errorCode,
            string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
            => inner.FailAsync(owner, requestId, state, errorCode, errorMessage, completedAt, ct);

        public Task PurgeResponsesAsync(Guid owner, DateTimeOffset now, CancellationToken ct)
            => inner.PurgeResponsesAsync(owner, now, ct);

        public Task PurgeResponseAsync(Guid owner, Guid requestId, DateTimeOffset now,
            CancellationToken ct) => inner.PurgeResponseAsync(owner, requestId, now, ct);
    }

    private sealed class PurgeFailingLedger(INoteMcpOperationLedger inner)
        : INoteMcpOperationLedger
    {
        public Task<NoteMcpOperationClaim> TryInsertAsync(NoteMcpOperationRow row,
            int maxIdentityRowsPerOwner, CancellationToken ct)
            => inner.TryInsertAsync(row, maxIdentityRowsPerOwner, ct);

        public Task<NoteMcpOperationRow?> FindAsync(Guid owner, Guid requestId,
            CancellationToken ct) => inner.FindAsync(owner, requestId, ct);

        public Task<bool> CompleteAsync(Guid owner, Guid requestId, string responseJson,
            string receiptStatus, Guid? revision, string message, DateTimeOffset completedAt,
            CancellationToken ct) => inner.CompleteAsync(owner, requestId, responseJson,
            receiptStatus, revision, message, completedAt, ct);

        public Task<bool> FailAsync(Guid owner, Guid requestId, string state, int errorCode,
            string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
            => inner.FailAsync(owner, requestId, state, errorCode, errorMessage, completedAt, ct);

        public Task PurgeResponsesAsync(Guid owner, DateTimeOffset now, CancellationToken ct)
            => throw new InvalidOperationException("synthetic purge failure");

        public Task PurgeResponseAsync(Guid owner, Guid requestId, DateTimeOffset now,
            CancellationToken ct) => throw new InvalidOperationException("synthetic purge failure");
    }

    private static DbContextOptions<PimDbContext> Options(string connectionString)
        => new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(connectionString).Options;

    private static Task<NativeNoteMcpOperationResultDto<string>> RunAsync(
        NoteMcpOperationStore store, Guid owner, Guid noteId, Guid requestId,
        object fingerprintInput,
        Func<CancellationToken, Task<NativeNoteMcpOperationResultDto<string>>> action)
        => store.RunAsync(owner, requestId, noteId, "test_operation", fingerprintInput, action,
            CancellationToken.None);

    private static NativeNoteMcpOperationResultDto<string> Result(Guid owner, Guid noteId,
        Guid requestId, TimeProvider clock, string value)
        => new(
            new NativeNoteMcpReceiptDto(
                NoteMcpOperationStore.BuildOperationId(owner, "test_operation", requestId),
                "test_operation", noteId, null, "applied", false, clock.GetUtcNow(), "ok"),
            value);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
