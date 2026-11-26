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
/// closed instead of running the same mutation again.
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

        var replay = await RunAsync(store, requestId, new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "second"));
        });
        Assert.Equal("first", replay.Result);
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(1, calls);

        var changedArgs = await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync(store, requestId, new { value = 2 }, _ =>
                Task.FromResult(Result(requestId, "wrong"))));
        Assert.Equal(40901, changedArgs.ErrorCode);
        var changedOperation = await Assert.ThrowsAsync<DomainException>(() =>
            store.RunAsync(_owner, requestId, "another_operation", new { value = 1 },
                _ => Task.FromResult(Result(requestId, "wrong")), default));
        Assert.Equal(40901, changedOperation.ErrorCode);
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

        var retry = await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));
        Assert.Equal(40901, retry.ErrorCode);
        Assert.Contains("尚未确认", retry.Message);
        Assert.Equal(1, calls);
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
    }

    [Fact]
    public async Task DomainFailureIsRecordedAndDoesNotReexecuteTheRequestId()
    {
        var store = Store();
        var requestId = Guid.NewGuid();
        var calls = 0;

        var first = await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                throw new DomainException(40901, "original domain failure");
            }));
        Assert.Equal(40901, first.ErrorCode);
        Assert.Equal("original domain failure", first.Message);
        var marker = await ReadOperationAsync(requestId);
        Assert.Equal("failed", marker.GetProperty("state").GetString());
        Assert.Equal(40901, marker.GetProperty("errorCode").GetInt32());

        var replay = await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync(store, requestId, new { value = 1 }, _ =>
            {
                calls++;
                return Task.FromResult(Result(requestId, "must not run"));
            }));
        Assert.Equal(40901, replay.ErrorCode);
        Assert.Equal("original domain failure", replay.Message);
        Assert.Equal(1, calls);

        var fresh = await RunAsync(store, Guid.NewGuid(), new { value = 1 }, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "fresh"));
        });
        Assert.Equal("fresh", fresh.Result);
        Assert.Equal(2, calls);
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
                new NativeNoteMcpReceiptDto(requestId, operation, _noteId, null, "applied",
                    false, DateTimeOffset.UtcNow, "legacy result"),
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
        var replay = await store.RunAsync(_owner, requestId, operation, fingerprintInput, _ =>
        {
            calls++;
            return Task.FromResult(Result(requestId, "must not run"));
        }, CancellationToken.None);
        Assert.Equal("legacy", replay.Result);
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(0, calls);
    }

    private NoteMcpOperationStore Store()
        => new(Options.Create(new NoteMaterialUploadOptions { StorageRoot = _root }));

    private Task<NativeNoteMcpOperationResultDto<string>> RunAsync(
        NoteMcpOperationStore store, Guid requestId, object fingerprintInput,
        Func<CancellationToken, Task<NativeNoteMcpOperationResultDto<string>>> action)
        => store.RunAsync(_owner, requestId, "test_operation", fingerprintInput, action,
            CancellationToken.None);

    private NativeNoteMcpOperationResultDto<string> Result(Guid requestId, string value)
        => new(
            new NativeNoteMcpReceiptDto(requestId, "test_operation", _noteId, null, "applied",
                false, DateTimeOffset.UtcNow, "ok"),
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
