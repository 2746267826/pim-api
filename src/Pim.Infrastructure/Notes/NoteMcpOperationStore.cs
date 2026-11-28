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
/// executing the same mutation again. Failures and unknown outcomes are returned as structured
/// receipts keyed by a stable external operation id.
/// <para>
/// Scope: this journal is a per-owner JSON file guarded by a process-local file lock. It gives
/// exactly-once replay only on a single API instance/host; it is not a multi-instance
/// idempotency ledger. Cross-instance replay protection depends on the PostgreSQL ledger tracked
/// by #621. Pending/unknown markers are deliberately not age-pruned, and when capacity forces
/// eviction they become durable tombstones so a later replay fails closed instead of executing
/// as a brand-new operation.
/// </para>
/// </summary>
public sealed class NoteMcpOperationStore(IOptions<NoteMaterialUploadOptions> options, TimeProvider? clock = null)
{
    private const int MaxRetainedRequests = 200;
    private const int MaxRetainedUnconfirmed = 200;
    private const int MaxRetainedTombstones = 2000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string PendingState = "pending";
    private const string CompletedState = "completed";
    private const string FailedState = "failed";
    private const string UnknownState = "unknown";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string Root => Path.Combine(Path.GetFullPath(options.Value.StorageRoot), "mcp-operations");

    public async Task<NativeNoteMcpOperationResultDto<T>> RunAsync<T>(Guid owner, Guid requestId,
        Guid noteId, string operation, object fingerprintInput,
        Func<CancellationToken, Task<NativeNoteMcpOperationResultDto<T>>> action,
        CancellationToken ct)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        if (noteId == Guid.Empty) throw new DomainException(40001, "笔记标识无效");
        if (requestId == Guid.Empty) throw new DomainException(40001, "requestId 不能为空");
        if (string.IsNullOrWhiteSpace(operation)) throw new DomainException(40001, "operation 不能为空");
        var operationId = BuildOperationId(owner, operation, requestId);
        var fingerprint = Fingerprint(operation, fingerprintInput);

        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadAsync(owner, ct);
        if (Prune(ledger)) await SaveAsync(owner, ledger, ct);
        if (ledger.Operations.TryGetValue(Key(requestId), out var existing))
        {
            if (existing.NoteId != Guid.Empty && existing.NoteId != noteId)
                throw new DomainException(40401,
                    "该 requestId 没有对应当前笔记的操作记录");
            var storedOperationId = existing.OperationId.Length > 0
                ? existing.OperationId
                : BuildOperationId(owner, existing.Operation, requestId);
            if (!string.Equals(existing.Operation, operation, StringComparison.Ordinal) ||
                !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                var conflictReceipt = StoredReceipt(existing, noteId, storedOperationId, "failed",
                    _clock.GetUtcNow(), replayed: false,
                    "requestId 已经绑定另一组参数或另一项操作，请换一个新的 requestId；原结果保留",
                    40901);
                throw new NativeNoteMcpOperationException(40901, conflictReceipt.Message,
                    conflictReceipt);
            }
            if (existing.State == FailedState)
            {
                var code = existing.ErrorCode ?? 40901;
                var failedMessage = NoteErrorSanitizer.SanitizeOperationMessage(
                    existing.ErrorMessage
                    ?? "该 requestId 的上一次执行没有产生可重放结果，请先重新读取最新状态",
                    code);
                var failedReceipt = StoredReceipt(existing, noteId, storedOperationId, "failed",
                    _clock.GetUtcNow(), replayed: false, failedMessage, code);
                throw new NativeNoteMcpOperationException(code, failedMessage, failedReceipt);
            }
            if (existing.State != CompletedState || existing.ResponseJson is null)
            {
                const string message =
                    "该 requestId 的执行结果尚未确认（可能已写入但未完成记账）。请先重新读取最新状态，确认后用新的 requestId 继续";
                var unknownReceipt = StoredReceipt(existing, noteId, storedOperationId, "unknown",
                    _clock.GetUtcNow(), replayed: false, message, 40901);
                throw new NativeNoteMcpOperationException(40901, message, unknownReceipt);
            }
            var replayed = JsonSerializer.Deserialize<NativeNoteMcpOperationResultDto<T>>(
                existing.ResponseJson, Json)
                ?? throw new InvalidDataException("MCP operation replay payload is invalid");
            return replayed with
            {
                Receipt = replayed.Receipt with
                {
                    OperationId = operationId,
                    Replayed = true,
                    Message = NoteErrorSanitizer.SanitizeDomainMessage(
                        replayed.Receipt.Message, 400) + "（已幂等重放，未再次执行）",
                },
            };
        }

        if (ledger.RetiredRequests.TryGetValue(Key(requestId), out var retired))
        {
            if (retired.NoteId != Guid.Empty && retired.NoteId != noteId)
                throw new DomainException(40401, "该 requestId 没有对应当前笔记的操作记录");
            const string message =
                "该 requestId 的执行结果曾未确认，且已超出本机核对窗口；为避免重复写入，不能再次执行。请重新读取最新状态并换用新的 requestId";
            var retiredReceipt = new NativeNoteMcpReceiptDto(
                BuildOperationId(owner, retired.Operation, requestId), retired.Operation, noteId,
                null, "unknown", false, _clock.GetUtcNow(), message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, retiredReceipt);
        }

        var operationKey = Key(requestId);
        var now = _clock.GetUtcNow();
        var pending = new StoredMcpOperation
        {
            Operation = operation,
            OperationId = operationId,
            NoteId = noteId,
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
            result = result with
            {
                Receipt = result.Receipt with { OperationId = operationId },
            };
        }
        catch (OperationCanceledException)
        {
            // The request was cancelled with unknown outcome. Keep the pending marker.
            throw;
        }
        catch (DomainException error)
        {
            var safeMessage = NoteErrorSanitizer.SanitizeOperationMessage(error.Message,
                error.ErrorCode);
            await MarkFailedAsync(owner, ledger, operationKey, pending, FailedState,
                error.ErrorCode, safeMessage, ct);
            var receipt = StoredReceipt(pending, noteId, operationId, "failed",
                _clock.GetUtcNow(), replayed: false, safeMessage, error.ErrorCode);
            throw new NativeNoteMcpOperationException(error.ErrorCode, error.Message, receipt);
        }
        catch (Exception)
        {
            const string message =
                "该 requestId 的上一次执行结果未确认；请先重新读取最新状态，确认后用新的 requestId 继续";
            await MarkFailedAsync(owner, ledger, operationKey, pending, UnknownState,
                40901, message, ct);
            var receipt = StoredReceipt(pending, noteId, operationId, "unknown",
                _clock.GetUtcNow(), replayed: false, message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, receipt);
        }

        ledger.Operations[operationKey] = pending with
        {
            State = CompletedState,
            ResponseJson = JsonSerializer.Serialize(result, Json),
            CompletedAt = _clock.GetUtcNow(),
            ReceiptStatus = result.Receipt.Status,
            Revision = result.Receipt.Revision,
            Message = result.Receipt.Message,
        };
        await SaveAsync(owner, ledger, ct);
        return result;
    }

    /// <summary>
    /// Looks up the current journal state for a caller-supplied requestId. This is the only
    /// supported path for reconciling a failed/unknown mutation; it does not execute anything.
    /// </summary>
    public async Task<NativeNoteMcpOperationStatusDto> GetAsync(Guid owner, Guid noteId,
        Guid requestId, CancellationToken ct)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        if (noteId == Guid.Empty) throw new DomainException(40001, "笔记标识无效");
        if (requestId == Guid.Empty) throw new DomainException(40001, "requestId 不能为空");
        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadAsync(owner, ct);
        if (Prune(ledger)) await SaveAsync(owner, ledger, ct);
        if (!ledger.Operations.TryGetValue(Key(requestId), out var stored))
        {
            if (ledger.RetiredRequests.TryGetValue(Key(requestId), out var retired))
            {
                if (retired.NoteId != Guid.Empty && retired.NoteId != noteId)
                    throw new DomainException(40401, "该 requestId 没有对应当前笔记的操作记录");
                const string retiredMessage =
                    "该 requestId 的执行结果曾未确认，且已超出本机核对窗口；为避免重复写入，不能再次执行。请重新读取最新状态并换用新的 requestId";
                var retiredReceipt = new NativeNoteMcpReceiptDto(
                    BuildOperationId(owner, retired.Operation, requestId), retired.Operation, noteId,
                    null, "unknown", false, _clock.GetUtcNow(), retiredMessage, 40901);
                return new NativeNoteMcpOperationStatusDto(retiredReceipt.OperationId,
                    retired.Operation, noteId, "unknown", retiredReceipt);
            }
            throw new DomainException(40401, "该 requestId 没有对应的 native-note 操作记录");
        }
        if (stored.NoteId != Guid.Empty && stored.NoteId != noteId)
            throw new DomainException(40401, "该 requestId 没有对应当前笔记的操作记录");

        var operationId = stored.OperationId.Length > 0
            ? stored.OperationId
            : BuildOperationId(owner, stored.Operation, requestId);
        var state = stored.State switch
        {
            CompletedState => "succeeded",
            FailedState => "failed",
            _ => "unknown",
        };
        NativeNoteMcpReceiptDto receipt;
        if (stored.ResponseJson is not null)
        {
            using var document = JsonDocument.Parse(stored.ResponseJson);
            receipt = document.RootElement.GetProperty("receipt")
                .Deserialize<NativeNoteMcpReceiptDto>(Json)
                ?? throw new InvalidDataException("MCP operation receipt is invalid");
            receipt = receipt with
            {
                OperationId = operationId,
                NoteId = noteId,
                Replayed = false,
                Message = NoteErrorSanitizer.SanitizeDomainMessage(receipt.Message, 400),
            };
        }
        else
        {
            var status = state == "failed" ? "failed" : "unknown";
            var message = state == "failed"
                ? NoteErrorSanitizer.SanitizeOperationMessage(
                    stored.ErrorMessage ?? "该 requestId 的执行失败", stored.ErrorCode ?? 40901)
                : "该 requestId 的执行结果尚未确认";
            receipt = StoredReceipt(stored, noteId, operationId, status,
                stored.CompletedAt ?? stored.CreatedAt, replayed: false, message,
                state == "failed" ? stored.ErrorCode ?? 40901 : 40901);
        }
        return new NativeNoteMcpOperationStatusDto(operationId, stored.Operation, noteId,
            state, receipt);
    }

    public static string BuildOperationId(Guid owner, string operation, Guid requestId)
    {
        if (owner == Guid.Empty || requestId == Guid.Empty)
            throw new DomainException(40001, "操作标识无效");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{owner:D}:{operation}:{requestId:D}:native-note-operation"));
        return "nop_" + Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    public static string BuildReadOperationId(Guid owner, string operation, Guid noteId,
        Guid? revision)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        if (noteId == Guid.Empty) throw new DomainException(40001, "笔记标识无效");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{owner:D}:{operation}:{noteId:D}:{revision?.ToString("D") ?? "-"}:native-note-read"));
        return "rop_" + Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static string Fingerprint(string operation, object fingerprintInput)
        => Hash(JsonSerializer.Serialize(new { operation, request = fingerprintInput }, Json));

    private static NativeNoteMcpReceiptDto StoredReceipt(StoredMcpOperation stored, Guid noteId,
        string operationId, string status, DateTimeOffset at, bool replayed, string message,
        int? errorCode)
        => new(operationId, stored.Operation, noteId == Guid.Empty ? stored.NoteId : noteId,
            stored.Revision, status, replayed, at, message, errorCode);

    private async Task MarkFailedAsync(Guid owner, Ledger ledger, string operationKey,
        StoredMcpOperation pending, string state, int errorCode, string errorMessage,
        CancellationToken ct)
    {
        var safeMessage = Truncate(NoteErrorSanitizer.SanitizeOperationMessage(errorMessage,
            errorCode), 500);
        ledger.Operations[operationKey] = pending with
        {
            State = state,
            ReceiptStatus = state,
            ErrorCode = errorCode,
            ErrorMessage = safeMessage,
            Message = safeMessage,
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

    private bool Prune(Ledger ledger)
    {
        var changed = false;
        var cutoff = _clock.GetUtcNow() - Retention;
        foreach (var key in ledger.Operations
                     .Where(item => item.Value.State is CompletedState or FailedState &&
                                    item.Value.CreatedAt < cutoff)
                     .Select(item => item.Key)
                     .ToArray())
        {
            ledger.Operations.Remove(key);
            changed = true;
        }

        foreach (var key in ledger.Operations
                     .Where(item => item.Value.State is CompletedState or FailedState)
                     .OrderByDescending(item => item.Value.CreatedAt)
                     .Skip(MaxRetainedRequests)
                     .Select(item => item.Key)
                     .ToArray())
        {
            ledger.Operations.Remove(key);
            changed = true;
        }

        // A pending/unknown marker represents a mutation whose outcome was never confirmed.
        // Evicting it silently would let the same requestId execute the mutation again, so the
        // eviction leaves a tombstone that keeps failing closed. This is a single-host
        // mitigation; #621's PostgreSQL ledger is required for durable cross-instance replay
        // protection.
        foreach (var (key, entry) in ledger.Operations
                     .Where(item => item.Value.State is PendingState or UnknownState)
                     .OrderByDescending(item => item.Value.CreatedAt)
                     .Skip(MaxRetainedUnconfirmed)
                     .ToArray())
        {
            ledger.Operations.Remove(key);
            ledger.RetiredRequests[key] = new RetiredOperation
            {
                Operation = entry.Operation,
                NoteId = entry.NoteId,
                RetiredAt = _clock.GetUtcNow(),
            };
            changed = true;
        }

        foreach (var key in ledger.RetiredRequests
                     .OrderByDescending(item => item.Value.RetiredAt)
                     .Skip(MaxRetainedTombstones)
                     .Select(item => item.Key)
                     .ToArray())
        {
            ledger.RetiredRequests.Remove(key);
            changed = true;
        }
        return changed;
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
        public Dictionary<string, RetiredOperation> RetiredRequests { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record RetiredOperation
    {
        public required string Operation { get; init; }
        public Guid NoteId { get; init; }
        public DateTimeOffset RetiredAt { get; init; }
    }

    private sealed record StoredMcpOperation
    {
        public required string Operation { get; init; }
        public string OperationId { get; init; } = string.Empty;
        public Guid NoteId { get; init; }
        public required string Fingerprint { get; init; }
        public string? ResponseJson { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string State { get; init; } = CompletedState;
        public DateTimeOffset? CompletedAt { get; init; }
        public string? ReceiptStatus { get; init; }
        public Guid? Revision { get; init; }
        public string? Message { get; init; }
        public int? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
