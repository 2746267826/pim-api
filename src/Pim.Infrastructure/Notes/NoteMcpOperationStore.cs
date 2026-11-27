using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Durable per-owner idempotency journal for native-note MCP mutations. A request id is bound
/// to the operation and a canonical fingerprint of the effective arguments; replaying it returns
/// the original response (with <c>Replayed=true</c>) and never runs the mutation twice. A reused
/// request id with different arguments is rejected with 409 instead of silently overwriting.
///
/// <para>
/// The binding is stored in PostgreSQL, so every API instance and every restart observes the
/// same identity. A pending marker is committed before the mutation starts. If the process stops
/// after the mutation but before the result is written, the next call reports an unconfirmed
/// outcome instead of executing the same mutation again. Completed/failed/unknown identities and
/// fingerprints are never evicted while an owner is below its hard identity capacity; retention
/// only removes the replay response body. At the capacity boundary new request ids fail closed
/// before any mutation runs, while existing request ids remain replayable/queryable.
/// </para>
/// </summary>
public sealed class NoteMcpOperationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex InternalGuidPattern = new(
        @"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}|[0-9A-Fa-f]{32})(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly INoteMcpOperationLedger _ledger;
    private readonly TimeProvider _clock;
    private readonly int _maxIdentityRowsPerOwner;
    private readonly ILogger<NoteMcpOperationStore>? _logger;

    internal NoteMcpOperationStore(INoteMcpOperationLedger ledger, TimeProvider? clock = null,
        ILogger<NoteMcpOperationStore>? logger = null, int? maxIdentityRowsPerOwner = null)
    {
        _ledger = ledger;
        _clock = clock ?? TimeProvider.System;
        _maxIdentityRowsPerOwner = maxIdentityRowsPerOwner ??
            NoteMcpOperationOptions.DefaultMaxIdentityRowsPerOwner;
        if (_maxIdentityRowsPerOwner <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIdentityRowsPerOwner));
        _logger = logger;
    }

    /// <summary>DI factory: the ledger is scoped, while the public type stays injectable.</summary>
    public static NoteMcpOperationStore Create(IServiceProvider services)
        => new(services.GetRequiredService<INoteMcpOperationLedger>(),
            services.GetService<TimeProvider>(),
            services.GetService<ILogger<NoteMcpOperationStore>>(),
            services.GetService<IOptions<NoteMcpOperationOptions>>()?
                .Value.MaxIdentityRowsPerOwner);

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
        var now = _clock.GetUtcNow();
        var pending = new NoteMcpOperationRow(owner, requestId, noteId, operation, operationId,
            fingerprint, NoteMcpOperationStates.Pending, null, now, null, null, null, null, null,
            null, null);

        // The INSERT is the cross-instance single-winner claim. A process crash after this
        // commit leaves a pending tombstone, which all later retries must fail closed on.
        var claim = await _ledger.TryInsertAsync(pending, _maxIdentityRowsPerOwner, ct);
        if (claim == NoteMcpOperationClaim.CapacityExceeded)
        {
            _logger?.LogWarning(
                "Native-note MCP idempotency identity capacity reached for owner {Owner} " +
                "(limit {Limit})", owner, _maxIdentityRowsPerOwner);
            throw new DomainException(40901,
                NativeNoteMcpOperationMessages.IdentityCapacityExceeded);
        }
        if (claim == NoteMcpOperationClaim.AlreadyExists)
        {
            var existing = await _ledger.FindAsync(owner, requestId, ct)
                ?? throw new DomainException(40901,
                    "该 requestId 的幂等记录正在建立，请稍后重试，不会重复执行变更");
            return await ReplayOrRejectAsync<T>(existing, owner, requestId, noteId, operation,
                fingerprint, operationId);
        }

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
            var safeMessage = SanitizeInternalIdentifiers(error.Message);
            await MarkFailedAsync(pending, NoteMcpOperationStates.Failed, error.ErrorCode,
                safeMessage, ct);
            var receipt = StoredReceipt(pending, noteId, operationId, "failed",
                _clock.GetUtcNow(), replayed: false, safeMessage, error.ErrorCode);
            throw new NativeNoteMcpOperationException(error.ErrorCode, safeMessage, receipt);
        }
        catch (Exception)
        {
            const string message =
                "该 requestId 的上一次执行结果未确认；请先重新读取最新状态，确认后用新的 requestId 继续";
            await MarkFailedAsync(pending, NoteMcpOperationStates.Unknown, 40901, message, ct);
            var receipt = StoredReceipt(pending, noteId, operationId, "unknown",
                _clock.GetUtcNow(), replayed: false, message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, receipt);
        }

        var completedAt = _clock.GetUtcNow();
        var responseJson = JsonSerializer.Serialize(result, Json);
        if (!await _ledger.CompleteAsync(owner, requestId, responseJson, result.Receipt.Status,
                result.Receipt.Revision, Truncate(result.Receipt.Message, 500), completedAt, ct))
        {
            const string message =
                "该 requestId 的变更已执行，但完成记账未确认；请先重新读取最新状态，确认后用新的 requestId 继续";
            var unknown = pending with
            {
                State = NoteMcpOperationStates.Unknown,
                CompletedAt = completedAt,
            };
            var receipt = StoredReceipt(unknown, noteId, operationId, "unknown", completedAt,
                replayed: false, message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, receipt);
        }

        await PurgeResponsesBestEffortAsync(owner);
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
        var stored = await _ledger.FindAsync(owner, requestId, ct)
            ?? throw new DomainException(40401, "该 requestId 没有对应的 native-note 操作记录");
        if (stored.NoteId != Guid.Empty && stored.NoteId != noteId)
            throw new DomainException(40401, "该 requestId 没有对应当前笔记的操作记录");

        if (stored.State == NoteMcpOperationStates.Completed &&
            stored.ResponseJson is not null && !HasRetainedResponse(stored, _clock.GetUtcNow()))
            await PurgeResponseBestEffortAsync(owner, requestId);

        var operationId = StoredOperationId(owner, requestId, stored);
        var state = stored.State switch
        {
            NoteMcpOperationStates.Completed => "succeeded",
            NoteMcpOperationStates.Failed => "failed",
            _ => "unknown",
        };
        NativeNoteMcpReceiptDto receipt;
        if (HasRetainedResponse(stored, _clock.GetUtcNow()))
        {
            using var document = JsonDocument.Parse(stored.ResponseJson!);
            receipt = document.RootElement.GetProperty("receipt")
                .Deserialize<NativeNoteMcpReceiptDto>(Json)
                ?? throw new InvalidDataException("MCP operation receipt is invalid");
            receipt = receipt with
            {
                OperationId = operationId,
                NoteId = noteId,
                Replayed = false,
            };
        }
        else
        {
            var status = state switch
            {
                "succeeded" => stored.ReceiptStatus ?? "succeeded",
                "failed" => stored.ReceiptStatus ?? "failed",
                _ => "unknown",
            };
            var message = state switch
            {
                "succeeded" when stored.CompletedAt is { } completedAt &&
                                  completedAt < _clock.GetUtcNow() - NoteMcpOperationRetention.ResponseRetention
                    => "该请求已执行成功；原响应已超过保留期，仅保留操作身份",
                "succeeded" => "该请求已执行成功；原响应已不再保留，仅保留操作身份",
                "failed" => SanitizeInternalIdentifiers(stored.ErrorMessage
                    ?? "该请求的执行失败"),
                _ => "该 requestId 的执行结果尚未确认",
            };
            receipt = StoredReceipt(stored, noteId, operationId, status,
                stored.CompletedAt ?? stored.CreatedAt, replayed: false, message,
                state switch
                {
                    "succeeded" => null,
                    "failed" => stored.ErrorCode ?? 40901,
                    _ => 40901,
                });
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

    private async Task<NativeNoteMcpOperationResultDto<T>> ReplayOrRejectAsync<T>(
        NoteMcpOperationRow existing, Guid owner, Guid requestId, Guid noteId, string operation,
        string fingerprint, string requestedOperationId)
    {
        if (existing.NoteId != Guid.Empty && existing.NoteId != noteId)
            throw new DomainException(40401, "该 requestId 没有对应当前笔记的操作记录");
        var storedOperationId = StoredOperationId(owner, requestId, existing);
        if (!string.Equals(existing.Operation, operation, StringComparison.Ordinal) ||
            !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            const string message =
                "requestId 已经绑定另一组参数或另一项操作，请换一个新的 requestId；原结果保留";
            var conflictReceipt = StoredReceipt(existing, noteId, storedOperationId, "failed",
                _clock.GetUtcNow(), replayed: false, message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, conflictReceipt);
        }
        if (existing.State == NoteMcpOperationStates.Failed)
        {
            var code = existing.ErrorCode ?? 40901;
            var failedMessage = SanitizeInternalIdentifiers(existing.ErrorMessage
                ?? "该 requestId 的上一次执行没有产生可重放结果，请先重新读取最新状态");
            var failedReceipt = StoredReceipt(existing, noteId, storedOperationId, "failed",
                _clock.GetUtcNow(), replayed: false, failedMessage, code);
            throw new NativeNoteMcpOperationException(code, failedMessage, failedReceipt);
        }
        if (existing.State != NoteMcpOperationStates.Completed)
        {
            const string message =
                "该 requestId 的执行结果尚未确认（可能已写入但未完成记账）。请先重新读取最新状态，确认后用新的 requestId 继续";
            var unknownReceipt = StoredReceipt(existing, noteId, storedOperationId, "unknown",
                _clock.GetUtcNow(), replayed: false, message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, unknownReceipt);
        }
        if (!HasRetainedResponse(existing, _clock.GetUtcNow()))
        {
            var expired = existing.CompletedAt is { } completedAt &&
                          completedAt < _clock.GetUtcNow() - NoteMcpOperationRetention.ResponseRetention;
            if (existing.ResponseJson is not null)
                await PurgeResponseBestEffortAsync(owner, requestId);
            var message = expired
                ? "该 requestId 已执行成功，但原响应已超过保留期；操作身份与输入指纹仍保留。请先重新读取最新状态，确认后用新的 requestId 继续"
                : "该 requestId 已执行成功，但原响应已不再保留；操作身份与输入指纹仍保留。请先重新读取最新状态，确认后用新的 requestId 继续";
            var expiredReceipt = StoredReceipt(existing, noteId, storedOperationId,
                existing.ReceiptStatus ?? "applied", _clock.GetUtcNow(), replayed: false,
                message, 40901);
            throw new NativeNoteMcpOperationException(40901, message, expiredReceipt);
        }

        var replayed = JsonSerializer.Deserialize<NativeNoteMcpOperationResultDto<T>>(
            existing.ResponseJson!, Json)
            ?? throw new InvalidDataException("MCP operation replay payload is invalid");
        return replayed with
        {
            Receipt = replayed.Receipt with
            {
                OperationId = requestedOperationId,
                Replayed = true,
                Message = replayed.Receipt.Message + "（已幂等重放，未再次执行）",
            },
        };
    }

    private static string StoredOperationId(Guid owner, Guid requestId,
        NoteMcpOperationRow stored)
        => stored.OperationId.Length > 0
            ? stored.OperationId
            : BuildOperationId(owner, stored.Operation, requestId);

    private static bool HasRetainedResponse(NoteMcpOperationRow row, DateTimeOffset now)
        => row.ResponseJson is not null &&
           row.CompletedAt is { } completedAt &&
           completedAt >= now - NoteMcpOperationRetention.ResponseRetention;

    private static string SanitizeInternalIdentifiers(string message)
        => InternalGuidPattern.Replace(message, "[id]");

    private static NativeNoteMcpReceiptDto StoredReceipt(NoteMcpOperationRow stored, Guid noteId,
        string operationId, string status, DateTimeOffset at, bool replayed, string message,
        int? errorCode)
        => new(operationId, stored.Operation, noteId == Guid.Empty ? stored.NoteId : noteId,
            stored.Revision, status, replayed, at, message, errorCode);

    private async Task MarkFailedAsync(NoteMcpOperationRow pending, string state, int errorCode,
        string errorMessage, CancellationToken ct)
    {
        var message = Truncate(errorMessage, 500);
        try
        {
            await _ledger.FailAsync(pending.OwnerId, pending.RequestId, state, errorCode,
                message, _clock.GetUtcNow(), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The pending marker is already durable. A failed completion write must not mask
            // the original action failure; the next retry will fail closed on the pending marker.
        }
    }

    private async Task PurgeResponsesBestEffortAsync(Guid owner)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _ledger.PurgeResponsesAsync(owner, _clock.GetUtcNow(), timeout.Token);
        }
        catch (Exception error)
        {
            // Bulk cleanup is opportunistic and must not delay or fail a completed mutation.
            _logger?.LogWarning(error,
                "Failed to purge old native-note MCP replay responses for owner {Owner}", owner);
        }
    }

    private async Task PurgeResponseBestEffortAsync(Guid owner, Guid requestId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _ledger.PurgeResponseAsync(owner, requestId, _clock.GetUtcNow(), timeout.Token);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error,
                "Failed to purge an expired native-note MCP replay response for owner {Owner}", owner);
        }
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
