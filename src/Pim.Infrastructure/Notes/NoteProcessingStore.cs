using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

/// <summary>Stable identities for a fixed owner/version processing scope and its child tasks.</summary>
public static class NoteProcessingIdentity
{
    public static Guid Processing(Guid owner, Guid versionId) =>
        Stable(owner, versionId, "processing");

    public static Guid Task(Guid owner, Guid versionId, NoteProcessingTaskKind kind) =>
        Stable(owner, versionId, kind.ToString());

    private static Guid Stable(Guid owner, Guid versionId, string kind)
    {
        if (owner == Guid.Empty || versionId == Guid.Empty)
            throw new DomainException(40001, "账号或材料版本标识无效");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{owner:D}:{versionId:D}:{kind}"));
        var identity = bytes.AsSpan(0, 16).ToArray();
        identity[6] = (byte)((identity[6] & 0x0f) | 0x50);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new Guid(identity);
    }
}

internal sealed class NoteProcessingRecord
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Owner { get; set; }
    public Guid ProcessingId { get; set; }
    public Guid NoteId { get; set; }
    public Guid VersionId { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public bool AutoProcessingEnabled { get; set; }
    public bool AutoRequested { get; set; }
    public bool AutoStarted { get; set; }
    public bool ManualStarted { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? PendingReason { get; set; }
    public List<NoteProcessingIssueDto> Issues { get; set; } = [];
    public StartNoteAsrRequest? AsrRequest { get; set; }
    public StartNoteDerivedTaskRequest? DerivedRequest { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Durable high-level orchestration record. Child task files remain authoritative for execution;
/// this record only freezes the start decision, pending explanation and exact request snapshots.
/// </summary>
public sealed class NoteProcessingStore(IOptions<NoteMaterialUploadOptions> options, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string Root => Path.Combine(Path.GetFullPath(options.Value.StorageRoot), "processing");

    internal async Task<NoteProcessingRecord?> TryReadAsync(Guid owner, Guid processingId,
        CancellationToken ct)
    {
        if (processingId == Guid.Empty) throw Invalid("处理编排标识无效");
        var path = FilePath(owner, processingId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var state = await JsonSerializer.DeserializeAsync<NoteProcessingRecord>(stream, Json, ct)
            ?? throw new InvalidDataException("处理编排记录无效");
        if (state.SchemaVersion != 1 || state.Owner != owner ||
            state.ProcessingId != processingId)
            throw new InvalidDataException("处理编排记录身份不一致");
        return state;
    }

    internal async Task<NoteProcessingRecord> ReadAsync(Guid owner, Guid processingId,
        CancellationToken ct) =>
        await TryReadAsync(owner, processingId, ct)
        ?? throw new DomainException(40401, "处理编排记录不存在");

    internal async Task DeleteForNoteAsync(Guid owner, Guid noteId, CancellationToken ct)
    {
        if (noteId == Guid.Empty) throw Invalid("笔记标识无效");
        await using var lease = await LockAsync(owner, ct);
        var folder = Folder(owner);
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id))
                continue;
            var state = await TryReadAsync(owner, id, ct);
            if (state is null || state.NoteId != noteId) continue;
            File.Delete(path);
        }
    }

    internal async Task<T> WithLockAsync<T>(Guid owner,
        Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        return await action(ct);
    }

    internal async Task<NoteProcessingRecord> SaveAsync(NoteProcessingRecord state,
        CancellationToken ct)
    {
        await using var lease = await LockAsync(state.Owner, ct);
        var existing = await TryReadAsync(state.Owner, state.ProcessingId, ct);
        if (existing is not null && existing.Revision != state.Revision)
            throw new DomainException(40901, "处理编排记录已变化，请刷新后重试");
        state.Revision = Guid.NewGuid();
        state.UpdatedAt = _clock.GetUtcNow();
        await WriteAsync(FilePath(state.Owner, state.ProcessingId), state, ct);
        return state;
    }

    /// <summary>
    /// Caller already holds the owner orchestration lock. An unchanged snapshot is a no-op so
    /// periodic reconciliation does not rotate <see cref="NoteProcessingRecord.Revision"/> or
    /// <see cref="NoteProcessingRecord.UpdatedAt"/> for the same blocked input.
    /// </summary>
    internal async Task<NoteProcessingRecord> SaveLockedAsync(NoteProcessingRecord state,
        CancellationToken ct)
    {
        var existing = await TryReadAsync(state.Owner, state.ProcessingId, ct);
        if (existing is not null && HasSameContent(existing, state))
        {
            state.Revision = existing.Revision;
            state.UpdatedAt = existing.UpdatedAt;
            return state;
        }
        state.Revision = Guid.NewGuid();
        state.UpdatedAt = _clock.GetUtcNow();
        await WriteAsync(FilePath(state.Owner, state.ProcessingId), state, ct);
        return state;
    }

    private static bool HasSameContent(NoteProcessingRecord left, NoteProcessingRecord right) =>
        JsonNode.DeepEquals(ComparableJson(left), ComparableJson(right));

    private static JsonNode ComparableJson(NoteProcessingRecord state)
    {
        var node = JsonSerializer.SerializeToNode(state, Json)!.AsObject();
        node.Remove(JsonNamingPolicy.CamelCase.ConvertName(nameof(NoteProcessingRecord.Revision)));
        node.Remove(JsonNamingPolicy.CamelCase.ConvertName(nameof(NoteProcessingRecord.UpdatedAt)));
        return node;
    }

    internal IEnumerable<(Guid Owner, Guid Id)> Enumerate(Guid? owner = null)
    {
        if (!Directory.Exists(Root)) yield break;
        var folders = owner.HasValue ? [Folder(owner.Value)] : Directory.EnumerateDirectories(Root);
        foreach (var folder in folders)
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "D", out var account) ||
                !Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id))
                    yield return (account, id);
        }
    }

    private string Folder(Guid owner)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        return Path.Combine(Root, owner.ToString("D"));
    }

    private string FilePath(Guid owner, Guid id) => Path.Combine(Folder(owner), id.ToString("D") + ".json");

    private async Task<FileStream> LockAsync(Guid owner, CancellationToken ct)
    {
        var folder = Folder(owner);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "processing.lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, ct);
            }
            catch (IOException)
            {
                throw new DomainException(50301, "本账号的处理编排尚在进行，请稍后重试");
            }
        }
    }

    private static async Task WriteAsync(string path, NoteProcessingRecord state,
        CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, state, Json, ct);
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

    private static DomainException Invalid(string message) => new(40001, message);
}
