using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Crash, replay and retention semantics for the native-note requestId journal. The in-memory
/// ledger mirrors the PostgreSQL contract so these tests can focus on the store state machine;
/// real unique-constraint/SQL behaviour is covered by NoteMcpOperationStoreRealDbTests.
/// </summary>
public sealed class NoteMcpOperationStoreTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MemoryLedger _ledger = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _noteId = Guid.NewGuid();

    [Fact]
    public async Task ReplaysCompletedResultWithoutExecutingTheMutationAgain()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;

        var first = await RunAsync(store, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "first"));
        });
        Assert.Equal("first", first.Result);
        Assert.False(first.Receipt.Replayed);
        Assert.Equal(OperationId(requestId), first.Receipt.OperationId);

        var replay = await RunAsync(store, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "second"));
        });
        Assert.Equal("first", replay.Result);
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(1, calls);

        var changedArgs = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 2 }, _ =>
                Task.FromResult(Result(requestId, "wrong"))));
        Assert.Equal(40901, changedArgs.ErrorCode);
        Assert.Equal(OperationId(requestId), changedArgs.Receipt.OperationId);
        Assert.Equal("failed", changedArgs.Receipt.Status);
        var changedOperation = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            store.RunAsync(_owner, requestId, _noteId, "another_operation", new { value = 1 },
                _ => Task.FromResult(Result(requestId, "wrong")), default));
        Assert.Equal(40901, changedOperation.ErrorCode);
        Assert.Equal(OperationId(requestId), changedOperation.Receipt.OperationId);
        Assert.Equal("test_operation", changedOperation.Receipt.Operation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationAfterMutationKeepsPendingMarkerAndFailsClosed()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                throw new OperationCanceledException("synthetic unknown outcome");
            }));
        Assert.Equal(1, calls);
        var marker = _ledger.Require(_owner, requestId);
        Assert.Equal("pending", marker.State);
        Assert.Null(marker.ResponseJson);

        var retry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));
        Assert.Equal(40901, retry.ErrorCode);
        Assert.Contains("尚未确认", retry.Message);
        Assert.Equal("unknown", retry.Receipt.Status);
        Assert.Equal(OperationId(requestId), retry.Receipt.OperationId);
        Assert.Equal(1, calls);

        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("unknown", status.State);
        Assert.Equal("unknown", status.Receipt.Status);
        Assert.Equal(OperationId(requestId), status.OperationId);
    }

    [Fact]
    public async Task PendingMarkerIsDurableBeforeTheMutationStarts()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var observedPending = false;

        var result = await RunAsync(store, requestId, new { value = 1 }, async _ =>
        {
            var marker = _ledger.Require(_owner, requestId);
            Assert.Equal("pending", marker.State);
            Assert.Null(marker.ResponseJson);
            observedPending = true;
            await Task.Yield();
            return Result(requestId, "applied");
        });

        Assert.True(observedPending);
        Assert.Equal("applied", result.Result);
        var completed = _ledger.Require(_owner, requestId);
        Assert.Equal("completed", completed.State);
        Assert.NotNull(completed.ResponseJson);

        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("succeeded", status.State);
        Assert.Equal("applied", status.Receipt.Status);

        var crossNoteCalls = 0;
        var crossNote = await Assert.ThrowsAsync<DomainException>(() =>
            store.RunAsync(_owner, requestId, Guid.NewGuid(), "test_operation",
                new { value = 1 }, _ =>
                {
                    crossNoteCalls++;
                    return Task.FromResult(Result(requestId, "must not run"));
                }, default));
        Assert.Equal(40401, crossNote.ErrorCode);
        Assert.Equal(0, crossNoteCalls);
    }

    [Fact]
    public async Task DomainFailureIsRecordedAndDoesNotReexecuteTheRequestId()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;

        var first = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                throw new DomainException(40901, "original domain failure");
            }));
        Assert.Equal(40901, first.ErrorCode);
        Assert.Equal("original domain failure", first.Message);
        Assert.Equal("failed", first.Receipt.Status);
        Assert.Equal(OperationId(requestId), first.Receipt.OperationId);
        var marker = _ledger.Require(_owner, requestId);
        Assert.Equal("failed", marker.State);
        Assert.Equal(40901, marker.ErrorCode);

        var replay = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));
        Assert.Equal(40901, replay.ErrorCode);
        Assert.Equal("original domain failure", replay.Message);
        Assert.Equal(1, calls);

        var failedStatus = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("failed", failedStatus.State);
        Assert.Equal("failed", failedStatus.Receipt.Status);

        var fresh = await RunAsync(store, Guid.NewGuid(), new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "fresh"));
        });
        Assert.Equal("fresh", fresh.Result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DomainFailureInternalIdentifiersAreRedactedFromReceiptAndLookup()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var internalId = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
                throw new DomainException(40901,
                    $"Processing task {internalId:D} failed for attempt {internalId:N}")));

        Assert.DoesNotContain(internalId.ToString("D"), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(internalId.ToString("N"), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(internalId.ToString("D"), failure.Receipt.Message,
            StringComparison.Ordinal);
        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.DoesNotContain(internalId.ToString("D"), status.Receipt.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(internalId.ToString("N"), status.Receipt.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedFailureIsRecordedAsUnknownAndQueryable()
    {
        var store = Store();
        var requestId = Guid.NewGuid();

        var first = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 },
                _ => throw new InvalidOperationException("synthetic unknown")));

        Assert.Equal(40901, first.ErrorCode);
        Assert.Equal("unknown", first.Receipt.Status);
        var marker = _ledger.Require(_owner, requestId);
        Assert.Equal("unknown", marker.State);
        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("unknown", status.State);
        Assert.Equal("unknown", status.Receipt.Status);
        Assert.Equal(OperationId(requestId), status.Receipt.OperationId);
    }

    [Fact]
    public async Task OperationLookup_Returns404ForUnknownOrCrossNoteRequestId()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        await RunAsync(store, requestId, new { value = 1 }, _ =>
            Task.FromResult(Result(requestId, "applied")));

        await Assert.ThrowsAsync<DomainException>(() =>
            store.GetAsync(_owner, Guid.NewGuid(), requestId, default));
        await Assert.ThrowsAsync<DomainException>(() =>
            store.GetAsync(_owner, _noteId, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task PreValidationFailureIsRecordedAndRetryReturnsOriginalFailure()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;

        await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                throw new DomainException(40001, "pre-validation failed");
            }));
        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("failed", status.State);
        Assert.Equal(40001, status.Receipt.ErrorCode);

        var retry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));
        Assert.Equal("pre-validation failed", retry.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailureReceipts_DoNotExposeInternalIdentifiers()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
                throw new DomainException(40901, "safe failure")));
        var json = JsonSerializer.Serialize(failure.Receipt, Json);

        foreach (var forbidden in new[]
                 {
                     "requestId", "taskId", "attemptId", "stepId", "logId", "receiptId"
                 })
            Assert.DoesNotContain($"\"{forbidden}\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"operationId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanTwoHundredRequestsKeepIdentityAndExpiredReplayFailsClosed()
    {
        var store = Store();
        var calls = 0;
        var requestIds = Enumerable.Range(0, 205).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var requestId in requestIds)
        {
            await RunAsync(store, requestId, new { value = requestId }, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result(requestId, requestId.ToString("D")));
            });
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(205, calls);
        Assert.Equal(205, _ledger.Count(_owner));
        var first = _ledger.Require(_owner, requestIds[0]);
        Assert.Equal("completed", first.State);
        Assert.NotNull(first.Fingerprint);
        Assert.Null(first.ResponseJson);
        Assert.NotNull(first.ResponsePurgedAt);

        var expired = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestIds[0], new { value = requestIds[0] }, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result(requestIds[0], "must not run"));
            }));
        Assert.Equal(40901, expired.ErrorCode);
        Assert.Contains("保留期", expired.Message);
        Assert.Equal("failed", expired.Receipt.Status);
        Assert.Equal(40901, expired.Receipt.ErrorCode);
        Assert.Equal(205, calls);

        var status = await store.GetAsync(_owner, _noteId, requestIds[0], default);
        Assert.Equal("succeeded", status.State);
        Assert.Equal(OperationId(requestIds[0]), status.OperationId);

        var conflict = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestIds[0], new { value = "different" }, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result(requestIds[0], "must not run"));
            }));
        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(205, calls);

        var recent = await RunAsync(store, requestIds[^1], new { value = requestIds[^1] }, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Result(requestIds[^1], "must not run"));
        });
        Assert.Equal(requestIds[^1].ToString("D"), recent.Result);
        Assert.True(recent.Receipt.Replayed);
        Assert.Equal(205, calls);
    }

    [Fact]
    public async Task CompletedResponseExpiresAfterThirtyDaysAndStillFailsClosed()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;
        await RunAsync(store, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "original"));
        });

        _clock.Advance(TimeSpan.FromDays(31));
        var triggerId = Guid.NewGuid();
        await RunAsync(store, triggerId, new { value = 2 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(triggerId, "trigger"));
        });

        var old = _ledger.Require(_owner, requestId);
        Assert.Equal("completed", old.State);
        Assert.NotNull(old.Fingerprint);
        Assert.Null(old.ResponseJson);

        var replay = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));
        Assert.Equal(40901, replay.ErrorCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CompletedResponseExpiresWithoutAnotherSuccessfulMutation()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;

        await RunAsync(store, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "original"));
        });

        _clock.Advance(TimeSpan.FromDays(31));
        var replay = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));

        Assert.Equal(40901, replay.ErrorCode);
        Assert.Contains("保留期", replay.Message);
        Assert.Equal(1, calls);
        var expired = _ledger.Require(_owner, requestId);
        Assert.Equal("completed", expired.State);
        Assert.Null(expired.ResponseJson);
        Assert.NotNull(expired.ResponsePurgedAt);

        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("succeeded", status.State);
        Assert.Equal("applied", status.Receipt.Status);
        Assert.Contains("超过保留期", status.Receipt.Message);
    }

    [Fact]
    public async Task ExpiredReplayFailsClosedWhenResponsePurgeFails()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;
        await RunAsync(store, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "original"));
        });

        _clock.Advance(TimeSpan.FromDays(31));
        _ledger.FailPurge = true;
        var expired = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));

        Assert.Equal(40901, expired.ErrorCode);
        Assert.Equal(1, calls);
        Assert.NotNull(_ledger.Require(_owner, requestId).ResponseJson);
    }

    [Fact]
    public async Task OldPendingAndUnknownMarkersAreNeverPruned()
    {
        var store = Store();
        var pendingId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(store, pendingId, new { value = 1 }, _ =>
            {
                calls++;
                throw new OperationCanceledException("synthetic pending");
            }));
        await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, unknownId, new { value = 2 }, _ =>
            {
                calls++;
                throw new InvalidOperationException("synthetic unknown");
            }));
        await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, failedId, new { value = 4 }, _ =>
            {
                calls++;
                throw new DomainException(40901, "synthetic failure");
            }));
        await RunAsync(store, completedId, new { value = 3 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(completedId, "done"));
        });

        _clock.Advance(TimeSpan.FromDays(31));
        for (var index = 0; index < 205; index++)
        {
            var requestId = Guid.NewGuid();
            await RunAsync(store, requestId, new { value = index }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "new"));
            });
        }

        Assert.Equal("pending", _ledger.Require(_owner, pendingId).State);
        Assert.Equal("unknown", _ledger.Require(_owner, unknownId).State);
        Assert.Equal("failed", _ledger.Require(_owner, failedId).State);
        Assert.NotNull(_ledger.Require(_owner, pendingId).Fingerprint);
        Assert.NotNull(_ledger.Require(_owner, unknownId).Fingerprint);
        Assert.NotNull(_ledger.Require(_owner, failedId).Fingerprint);

        var pendingRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, pendingId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(pendingId, "must not run"));
            }));
        Assert.Equal("unknown", pendingRetry.Receipt.Status);
        var unknownRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, unknownId, new { value = 2 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(unknownId, "must not run"));
            }));
        Assert.Equal("unknown", unknownRetry.Receipt.Status);
        var failedRetry = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, failedId, new { value = 4 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(failedId, "must not run"));
            }));
        Assert.Equal("synthetic failure", failedRetry.Message);
        Assert.Equal(209, calls);
    }

    [Fact]
    public async Task RestartAndOwnerIsolationUseTheDurableIdentity()
    {
        var requestId = Guid.NewGuid();
        var calls = 0;
        var firstStore = Store();
        var first = await RunAsync(firstStore, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "first"));
        });
        Assert.Equal("first", first.Result);

        var restarted = Store();
        var replay = await RunAsync(restarted, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "must not run"));
        });
        Assert.Equal("first", replay.Result);
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(1, calls);

        var otherOwner = Guid.NewGuid();
        var other = await restarted.RunAsync(otherOwner, requestId, _noteId, "test_operation",
            new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "other-owner"));
            }, default);
        Assert.Equal("other-owner", other.Result);
        Assert.False(other.Receipt.Replayed);
        Assert.Equal(2, calls);
        Assert.Equal(2, _ledger.TotalCount);
    }

    [Fact]
    public async Task ConcurrentClaimsExecuteTheMutationOnlyOnce()
    {
        var requestId = Guid.NewGuid();
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = Store();
        var secondStore = Store();

        var first = RunAsync(firstStore, requestId, new { value = 1 }, async _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return Result(requestId, "first");
        });
        await started.Task;

        var second = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(secondStore, requestId, new { value = 1 }, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result(requestId, "second"));
            }));
        Assert.Equal(40901, second.ErrorCode);
        Assert.Equal("unknown", second.Receipt.Status);

        release.TrySetResult();
        var completed = await first;
        Assert.Equal("first", completed.Result);
        Assert.Equal(1, calls);

        var replay = await RunAsync(secondStore, requestId, new { value = 1 }, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Result(requestId, "must not run"));
        });
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(1, calls);
    }

    private NoteMcpOperationStore Store() => new(_ledger, _clock);

    private Task<NativeNoteMcpOperationResultDto<string>> RunAsync(
        NoteMcpOperationStore store, Guid requestId, object fingerprintInput,
        Func<CancellationToken, Task<NativeNoteMcpOperationResultDto<string>>> action)
        => store.RunAsync(_owner, requestId, _noteId, "test_operation", fingerprintInput,
            action, CancellationToken.None);

    private string OperationId(Guid requestId)
        => NoteMcpOperationStore.BuildOperationId(_owner, "test_operation", requestId);

    private NativeNoteMcpOperationResultDto<string> Result(Guid requestId, string value)
        => new(
            new NativeNoteMcpReceiptDto(OperationId(requestId), "test_operation", _noteId,
                null, "applied", false, _clock.GetUtcNow(), "ok"),
            value);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    /// <summary>
    /// Contract-compatible in-memory ledger. PostgreSQL is the production authority; this fake
    /// only proves the store never skips the claim, conflict and fail-closed branches.
    /// </summary>
    private sealed class MemoryLedger : INoteMcpOperationLedger
    {
        private readonly object _gate = new();
        private readonly Dictionary<(Guid Owner, Guid RequestId), NoteMcpOperationRow> _rows = new();

        public bool FailPurge { get; set; }

        public async Task<bool> TryInsertAsync(NoteMcpOperationRow row, CancellationToken ct)
        {
            await Task.Yield();
            lock (_gate)
            {
                if (_rows.ContainsKey((row.OwnerId, row.RequestId))) return false;
                _rows[(row.OwnerId, row.RequestId)] = row;
                return true;
            }
        }

        public async Task<NoteMcpOperationRow?> FindAsync(Guid owner, Guid requestId,
            CancellationToken ct)
        {
            await Task.Yield();
            lock (_gate)
                return _rows.GetValueOrDefault((owner, requestId));
        }

        public async Task<bool> CompleteAsync(Guid owner, Guid requestId, string responseJson,
            string receiptStatus, Guid? revision, string message, DateTimeOffset completedAt,
            CancellationToken ct)
        {
            await Task.Yield();
            lock (_gate)
            {
                var key = (owner, requestId);
                if (!_rows.TryGetValue(key, out var row) || row.State != "pending")
                    return false;
                _rows[key] = row with
                {
                    State = "completed",
                    ResponseJson = responseJson,
                    ReceiptStatus = receiptStatus,
                    Revision = revision,
                    Message = message,
                    CompletedAt = completedAt,
                    ResponsePurgedAt = null,
                };
                return true;
            }
        }

        public async Task<bool> FailAsync(Guid owner, Guid requestId, string state, int errorCode,
            string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
        {
            await Task.Yield();
            lock (_gate)
            {
                var key = (owner, requestId);
                if (!_rows.TryGetValue(key, out var row) || row.State != "pending")
                    return false;
                _rows[key] = row with
                {
                    State = state,
                    ReceiptStatus = state,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                    Message = errorMessage,
                    CompletedAt = completedAt,
                };
                return true;
            }
        }

        public async Task PurgeResponsesAsync(Guid owner, DateTimeOffset now, CancellationToken ct)
        {
            if (FailPurge) throw new InvalidOperationException("synthetic purge failure");
            await Task.Yield();
            lock (_gate)
            {
                var cutoff = now - NoteMcpOperationRetention.ResponseRetention;
                foreach (var key in _rows
                             .Where(item => item.Value.OwnerId == owner &&
                                            item.Value.State == "completed" &&
                                            item.Value.ResponseJson is not null &&
                                            item.Value.CompletedAt < cutoff)
                             .Select(item => item.Key)
                             .ToArray())
                {
                    _rows[key] = _rows[key] with
                    {
                        ResponseJson = null,
                        ResponsePurgedAt = now,
                    };
                }

                foreach (var key in _rows
                             .Where(item => item.Value.OwnerId == owner &&
                                            item.Value.State == "completed" &&
                                            item.Value.ResponseJson is not null)
                             .OrderByDescending(item => item.Value.CompletedAt)
                             .ThenByDescending(item => item.Value.CreatedAt)
                             .ThenByDescending(item => item.Value.RequestId)
                             .Skip(NoteMcpOperationRetention.MaxCompletedResponses)
                             .Select(item => item.Key)
                             .ToArray())
                {
                    _rows[key] = _rows[key] with
                    {
                        ResponseJson = null,
                        ResponsePurgedAt = now,
                    };
                }
            }
        }

        public async Task PurgeResponseAsync(Guid owner, Guid requestId, DateTimeOffset now,
            CancellationToken ct)
        {
            if (FailPurge) throw new InvalidOperationException("synthetic purge failure");
            await Task.Yield();
            lock (_gate)
            {
                var key = (owner, requestId);
                if (!_rows.TryGetValue(key, out var row) || row.State != "completed" ||
                    row.ResponseJson is null || row.CompletedAt is not { } completedAt ||
                    completedAt >= now - NoteMcpOperationRetention.ResponseRetention)
                    return;
                _rows[key] = row with { ResponseJson = null, ResponsePurgedAt = now };
            }
        }

        public NoteMcpOperationRow Require(Guid owner, Guid requestId)
        {
            lock (_gate)
                return _rows.TryGetValue((owner, requestId), out var row)
                    ? row
                    : throw new InvalidOperationException("operation row missing");
        }

        public int Count(Guid owner)
        {
            lock (_gate)
                return _rows.Keys.Count(key => key.Owner == owner);
        }

        public int TotalCount
        {
            get
            {
                lock (_gate)
                    return _rows.Count;
            }
        }
    }
}
