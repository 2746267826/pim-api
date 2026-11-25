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
/// </summary>
public sealed class NoteMcpOperationStore(IOptions<NoteMaterialUploadOptions> options, TimeProvider? clock = null)
{
    private const int MaxRetainedRequests = 200;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
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
        var fingerprint = Hash(JsonSerializer.Serialize(new { operation, request = fingerprintInput }, Json));

        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadAsync(owner, ct);
        Prune(ledger);
        if (ledger.Operations.TryGetValue(Key(requestId), out var existing))
        {
            if (!string.Equals(existing.Operation, operation, StringComparison.Ordinal) ||
                !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new DomainException(40901, "requestId 已经绑定另一组参数或另一项操作，请换一个新的 requestId；原结果保留");
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

        var result = await action(ct)
            ?? throw new InvalidOperationException("MCP operation returned no result");
        ledger.Operations[Key(requestId)] = new StoredMcpOperation(
            operation,
            fingerprint,
            JsonSerializer.Serialize(result, Json),
            _clock.GetUtcNow());
        await SaveAsync(owner, ledger, ct);
        return result;
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

    private sealed class Ledger
    {
        public int SchemaVersion { get; init; } = 1;
        public Guid Owner { get; init; }
        public Dictionary<string, StoredMcpOperation> Operations { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record StoredMcpOperation(
        string Operation,
        string Fingerprint,
        string ResponseJson,
        DateTimeOffset CreatedAt);
}
