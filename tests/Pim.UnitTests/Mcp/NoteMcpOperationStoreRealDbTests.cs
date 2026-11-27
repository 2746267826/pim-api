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
        Assert.Contains("保留期", expired.Message);
        Assert.Equal("failed", expired.Receipt.Status);
        Assert.Equal(40901, expired.Receipt.ErrorCode);
        Assert.Equal(205, calls);

        var status = await store.GetAsync(owner, noteId, requestIds[0], default);
        Assert.Equal("succeeded", status.State);
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
