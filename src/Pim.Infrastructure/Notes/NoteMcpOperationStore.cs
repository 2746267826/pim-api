using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Durable per-owner idempotency journal for native-note MCP mutations. A request id is bound
/// to the operation and a canonical fingerprint of the effective arguments; replaying it returns
/// the original response (with <c>Replayed=true</c>) and never runs the mutation twice. A reused
/// request id with different arguments is rejected with 409 instead of silently overwriting.
/// A pending marker is persisted before the mutation. If the process stops after the mutation
/// but before the result is written, the next call reports an unconfirmed outcome instead of
/// executing the same mutation again.
/// </summary>
public sealed class NoteMcpOperationStore(IOptions<NoteMaterialUploadOptions> options, TimeProvider? clock = null)
{
    private const int MaxRetainedRequests = 200;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string PendingState = "pending";
    private const string CompletedState = "completed";
    private const string FailedState = "failed";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string Root => Path.Combine(Path.GetFullPath(options.Value.StorageRoot), "mcp-operations");

    public async Task<NativeNoteMcpOperationResultDto<T>> RunAsync<T>(Guid owner, Guid requestId,
        string operation, object fingerprintInput,
        Func<CancellationToken, Task<NativeNoteMcpOperationResultDto<T>>> action,
        CancellationToken ct)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        if (requestId == Guid.Empty) throw new DomainException(40001, "requestId 不能为空");
        if (string.IsNullOrWhiteSpace(operation)) throw new DomainException(40001, "operation 不能为空");
        var fingerprint = Fingerprint(operation, fingerprintInput);

        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadAsync(owner, ct);
        Prune(ledger);
        if (ledger.Operations.TryGetValue(Key(requestId), out var existing))
        {
            if (!string.Equals(existing.Operation, operation, StringComparison.Ordinal) ||
                !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new DomainException(40901, "requestId 已经绑定另一组参数或另一项操作，请换一个新的 requestId；原结果保留");
            if (existing.State == FailedState)
                throw new DomainException(existing.ErrorCode ?? 40901,
                    existing.ErrorMessage ?? "该 requestId 的上一次执行没有产生可重放结果，请先重新读取最新状态");
            if (existing.State != CompletedState || existing.ResponseJson is null)
                throw new DomainException(40901,
                    "该 requestId 的执行结果尚未确认（可能已写入但未完成记账）。请先重新读取最新状态，确认后用新的 requestId 继续");
            var replayed = JsonSerializer.Deserialize<NativeNoteMcpOperationResultDto<T>>(
                existing.ResponseJson, Json)
                ?? throw new InvalidDataException("MCP operation replay payload is invalid");
            return replayed with
            {
                Receipt = replayed.Receipt with
                {
                    Replayed = true,
                    Message = replayed.Receipt.Message + "（已幂等重放，未再次执行）",
                },
            };
        }

        var operationKey = Key(requestId);
        var now = _clock.GetUtcNow();
        var pending = new StoredMcpOperation
        {
            Operation = operation,
            Fingerprint = fingerprint,
            State = PendingState,
            CreatedAt = now,
        };
        ledger.Operations[operationKey] = pending;
        // The marker must be durable before the mutation starts; otherwise a crash after the
        // mutation but before the result write would execute the same request again.
        await SaveAsync(owner, ledger, ct);

        NativeNoteMcpOperationResultDto<T> result;
        try
        {
            result = await action(ct)
                ?? throw new InvalidOperationException("MCP operation returned no result");
        }
        catch (OperationCanceledException)
        {
            // The request was cancelled with unknown outcome. Keep the pending marker.
            throw;
        }
        catch (DomainException error)
        {
            await MarkFailedAsync(owner, ledger, operationKey, pending, error.ErrorCode,
                error.Message, ct);
            throw;
        }
        catch (Exception)
        {
            await MarkFailedAsync(owner, ledger, operationKey, pending, 40901,
                "该 requestId 的上一次执行结果未确认；请先重新读取最新状态，确认后用新的 requestId 继续",
                ct);
            throw;
        }

        ledger.Operations[operationKey] = pending with
        {
            State = CompletedState,
            ResponseJson = JsonSerializer.Serialize(result, Json),
            CompletedAt = _clock.GetUtcNow(),
        };
        await SaveAsync(owner, ledger, ct);
        return result;
    }

    internal static string Fingerprint(string operation, object fingerprintInput)
        => Hash(JsonSerializer.Serialize(new { operation, request = fingerprintInput }, Json));

    private async Task MarkFailedAsync(Guid owner, Ledger ledger, string operationKey,
        StoredMcpOperation pending, int errorCode, string errorMessage, CancellationToken ct)
    {
        ledger.Operations[operationKey] = pending with
        {
            State = FailedState,
            ErrorCode = errorCode,
            ErrorMessage = Truncate(errorMessage, 500),
            CompletedAt = _clock.GetUtcNow(),
        };
        try
        {
            await SaveAsync(owner, ledger, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The pending marker is already durable. A failed completion write must not mask
            // the original action failure; the next retry will fail closed on the pending marker.
        }
    }

    private void Prune(Ledger ledger)
    {
        var cutoff = _clock.GetUtcNow() - Retention;
        foreach (var key in ledger.Operations
                     .Where(item => item.Value.CreatedAt < cutoff)
                     .Select(item => item.Key)
                     .ToArray())
            ledger.Operations.Remove(key);
        if (ledger.Operations.Count <= MaxRetainedRequests) return;
        foreach (var key in ledger.Operations
                     .OrderByDescending(item => item.Value.CreatedAt)
                     .Skip(MaxRetainedRequests)
                     .Select(item => item.Key)
                     .ToArray())
            ledger.Operations.Remove(key);
    }

    private async Task<Ledger> LoadAsync(Guid owner, CancellationToken ct)
    {
        var path = FilePath(owner);
        if (!File.Exists(path)) return new Ledger { Owner = owner };
        await using var stream = File.OpenRead(path);
        var ledger = await JsonSerializer.DeserializeAsync<Ledger>(stream, Json, ct)
            ?? throw new InvalidDataException("MCP operation journal is invalid");
        if (ledger.SchemaVersion != 1 || ledger.Owner != owner)
            throw new InvalidDataException("MCP operation journal identity is invalid");
        return ledger;
    }

    private async Task SaveAsync(Guid owner, Ledger ledger, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var path = FilePath(owner);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, ledger, Json, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<FileStream> LockAsync(Guid owner, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, Key(owner) + ".lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, ct);
            }
            catch (IOException)
            {
                throw new DomainException(50301, "该账号的 MCP 笔记操作尚在进行，请稍后重试");
            }
        }
    }

    private string FilePath(Guid owner) => Path.Combine(Root, Key(owner) + ".json");

    private static string Key(Guid id)
    {
        if (id == Guid.Empty) throw new DomainException(40001, "标识无效");
        return id.ToString("D");
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private sealed class Ledger
    {
        public int SchemaVersion { get; init; } = 1;
        public Guid Owner { get; init; }
        public Dictionary<string, StoredMcpOperation> Operations { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record StoredMcpOperation
    {
        public required string Operation { get; init; }
        public required string Fingerprint { get; init; }
        public string? ResponseJson { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string State { get; init; } = CompletedState;
        public DateTimeOffset? CompletedAt { get; init; }
        public int? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
