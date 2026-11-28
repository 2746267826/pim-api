using System.Text.Json;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Crash and replay semantics for the native-note requestId journal. These tests deliberately
/// separate "the mutation ran" from "the response was recorded": a pending marker must fail
/// closed instead of running the same mutation again, and failures must remain queryable.
/// </summary>
public sealed class NoteMcpOperationStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pim-note-mcp-operation-tests", Guid.NewGuid().ToString("N"));
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
        var marker = await ReadOperationAsync(requestId);
        Assert.Equal("pending", marker.GetProperty("state").GetString());
        Assert.False(marker.TryGetProperty("responseJson", out var responseJson) &&
            responseJson.ValueKind != JsonValueKind.Null);

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
            var marker = await ReadOperationAsync(requestId);
            Assert.Equal("pending", marker.GetProperty("state").GetString());
            Assert.False(marker.TryGetProperty("responseJson", out var responseJson) &&
                responseJson.ValueKind != JsonValueKind.Null);
            observedPending = true;
            return Result(requestId, "applied");
        });

        Assert.True(observedPending);
        Assert.Equal("applied", result.Result);
        var completed = await ReadOperationAsync(requestId);
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null,
            completed.GetProperty("responseJson").ValueKind);

        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal("succeeded", status.State);
        Assert.Equal("applied", status.Receipt.Status);
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
        var marker = await ReadOperationAsync(requestId);
        Assert.Equal("failed", marker.GetProperty("state").GetString());
        Assert.Equal(40901, marker.GetProperty("errorCode").GetInt32());

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
    public async Task UnexpectedFailureIsRecordedAsUnknownAndQueryable()
    {
        var store = Store();
        var requestId = Guid.NewGuid();

        var first = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 },
                _ => throw new InvalidOperationException("synthetic unknown")));

        Assert.Equal(40901, first.ErrorCode);
        Assert.Equal("unknown", first.Receipt.Status);
        var marker = await ReadOperationAsync(requestId);
        Assert.Equal("unknown", marker.GetProperty("state").GetString());
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
    public async Task LegacyCompletedEntryWithoutStateStillReplays()
    {
        var requestId = Guid.NewGuid();
        var operation = "legacy_operation";
        var fingerprintInput = new { value = 7 };
        var fingerprint = NoteMcpOperationStore.Fingerprint(operation, fingerprintInput);
        var responseJson = JsonSerializer.Serialize(
            new NativeNoteMcpOperationResultDto<string>(
                new NativeNoteMcpReceiptDto(OperationId(requestId), operation, _noteId, null,
                    "applied", false, DateTimeOffset.UtcNow, "legacy result"),
                "legacy"),
            Json);
        var directory = Path.Combine(_root, "mcp-operations");
        Directory.CreateDirectory(directory);
        var ledger = new
        {
            schemaVersion = 1,
            owner = _owner,
            operations = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [requestId.ToString("D")] = new
                {
                    operation,
                    fingerprint,
                    responseJson,
                    createdAt = DateTimeOffset.UtcNow,
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(directory, _owner.ToString("D") + ".json"),
            JsonSerializer.Serialize(ledger, Json));

        var calls = 0;
        var store = Store();
        var replay = await store.RunAsync(_owner, requestId, _noteId, operation,
            fingerprintInput, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }, CancellationToken.None);
        Assert.Equal("legacy", replay.Result);
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LegacyFailedEntryWithoutNoteIdIsQueryableForTheRequestedNote()
    {
        var requestId = Guid.NewGuid();
        var operation = "legacy_operation";
        var directory = Path.Combine(_root, "mcp-operations");
        Directory.CreateDirectory(directory);
        var ledger = new
        {
            schemaVersion = 1,
            owner = _owner,
            operations = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [requestId.ToString("D")] = new
                {
                    operation,
                    fingerprint = "legacy-fingerprint",
                    state = "failed",
                    errorCode = 40901,
                    errorMessage = "legacy failure",
                    createdAt = DateTimeOffset.UtcNow,
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(directory, _owner.ToString("D") + ".json"),
            JsonSerializer.Serialize(ledger, Json));

        var store = Store();
        var status = await store.GetAsync(_owner, _noteId, requestId, default);

        Assert.Equal(_noteId, status.NoteId);
        Assert.Equal("failed", status.State);
        Assert.Equal(_noteId, status.Receipt.NoteId);
        Assert.Equal(OperationId(operation, requestId), status.OperationId);
    }

    [Fact]
    public async Task PersistedFailureMessagesPassThroughFullNoteErrorSanitizer()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        const string raw =
            "派生任务 11111111-1111-4111-8111-111111111111 调用 provider " +
            "https://internal.example/v1 读取 /data/private/secret.md 失败";

        var failure = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestId, new { value = 1 },
                _ => throw new DomainException(40901, raw)));

        Assert.Equal(raw, failure.Message);
        var fallback = NoteErrorSanitizer.FallbackMessage(409);
        Assert.Equal(fallback, failure.Receipt.Message);
        Assert.DoesNotContain("internal.example", failure.Receipt.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret.md", failure.Receipt.Message, StringComparison.Ordinal);

        var status = await store.GetAsync(_owner, _noteId, requestId, default);
        Assert.Equal(fallback, status.Receipt.Message);
        var marker = await ReadOperationAsync(requestId);
        Assert.Equal(fallback, marker.GetProperty("errorMessage").GetString());
        Assert.Equal(fallback, marker.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PendingMarkersBecomeTombstonesInsteadOfReexecutingAfterPrune()
    {
        var store = Store();
        var requestIds = new List<Guid>();
        for (var index = 0; index < 202; index++)
        {
            var requestId = Guid.NewGuid();
            requestIds.Add(requestId);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunAsync(store, requestId, new { value = index },
                    _ => throw new OperationCanceledException("synthetic unknown outcome")));
        }

        var calls = 0;
        var replay = await Assert.ThrowsAsync<NativeNoteMcpOperationException>(() =>
            RunAsync(store, requestIds[0], new { value = 0 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestIds[0], "must not run"));
            }));

        Assert.Equal(40901, replay.ErrorCode);
        Assert.Equal("unknown", replay.Receipt.Status);
        Assert.Contains("超出本机核对窗口", replay.Message);
        Assert.Equal(0, calls);

        var status = await store.GetAsync(_owner, _noteId, requestIds[0], default);
        Assert.Equal("unknown", status.State);
        Assert.Equal("unknown", status.Receipt.Status);
    }

    private NoteMcpOperationStore Store()
        => new(Options.Create(new NoteMaterialUploadOptions { StorageRoot = _root }));

    private Task<NativeNoteMcpOperationResultDto<string>> RunAsync(
        NoteMcpOperationStore store, Guid requestId, object fingerprintInput,
        Func<CancellationToken, Task<NativeNoteMcpOperationResultDto<string>>> action)
        => store.RunAsync(_owner, requestId, _noteId, "test_operation", fingerprintInput,
            action, CancellationToken.None);

    private string OperationId(Guid requestId)
        => OperationId("test_operation", requestId);

    private string OperationId(string operation, Guid requestId)
        => NoteMcpOperationStore.BuildOperationId(_owner, operation, requestId);

    private NativeNoteMcpOperationResultDto<string> Result(Guid requestId, string value)
        => new(
            new NativeNoteMcpReceiptDto(OperationId(requestId), "test_operation", _noteId,
                null, "applied", false, DateTimeOffset.UtcNow, "ok"),
            value);

    private async Task<JsonElement> ReadOperationAsync(Guid requestId)
    {
        var path = Path.Combine(_root, "mcp-operations", _owner.ToString("D") + ".json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return document.RootElement.GetProperty("operations")
            .GetProperty(requestId.ToString("D")).Clone();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
