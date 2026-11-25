using System.Security.Cryptography;
using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Storage;
using Pim.Module.Files.Services;

namespace Pim.Module.Files.Providers.Local;

public sealed record LocalFileEntry(string Id, string? ParentId, string Name, bool IsFolder,
    string? BlobKey, string? ContentType, long Size, string Etag, DateTimeOffset ModifiedAt, bool Trashed = false, string? UploadId = null, string? TrashGroup = null);

public sealed class LocalFileStore(ManagedBinaryStore binaries)
{
    public const string RootId = "root";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ManagedBinaryStore Binaries => binaries;

    public async Task<T> TransactionAsync<T>(Guid userId, Guid providerId,
        Func<Dictionary<string, LocalFileEntry>, Task<T>> action, bool write, CancellationToken ct)
    {
        if (providerId != LocalFileProvider.IdFor(userId)) throw new DomainException(40301, "内置存储身份不匹配");
        var root = binaries.AccountDirectory(userId); Directory.CreateDirectory(root);
        binaries.CheckPath(Path.Combine(root, "files.lock"));
        await using var lease = await AcquireAsync(Path.Combine(root, "files.lock"), ct);
        var path = Path.Combine(root, "files-catalog.json");
        binaries.CheckPath(path);
        var entries = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, LocalFileEntry>>(await File.ReadAllTextAsync(path, ct), Json)
                ?? throw new InvalidDataException("Invalid local file catalog")
            : new Dictionary<string, LocalFileEntry>(StringComparer.Ordinal)
            { [RootId] = new(RootId, null, "内置存储", true, null, null, 0, "root", DateTimeOffset.UtcNow) };
        var result = await action(entries);
        if (write)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await JsonSerializer.SerializeAsync(stream, entries, Json, ct); await stream.FlushAsync(ct); stream.Flush(true); }
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return result;
    }

    public Task<LocalFileEntry> GetAsync(Guid userId, Guid providerId, string id, CancellationToken ct = default) =>
        TransactionAsync(userId, providerId, entries => Task.FromResult(Live(entries, id)), false, ct);

    public Task<IReadOnlyList<LocalFileEntry>> ListAsync(Guid userId, Guid providerId, CancellationToken ct = default) =>
        TransactionAsync<IReadOnlyList<LocalFileEntry>>(userId, providerId,
            entries => Task.FromResult<IReadOnlyList<LocalFileEntry>>(entries.Values.Where(entry => !entry.Trashed).ToArray()), false, ct);

    public Task<LocalFileEntry> CreateAsync(Guid userId, Guid providerId, string? parentId, string parentPath,
        string name, bool folder, Stream? content, string? mime, long maxBytes, CancellationToken ct = default) =>
        TransactionAsync(userId, providerId, async entries =>
        {
            FileNameValidator.EnsureValidName(name);
            if (name.Any(char.IsControl)) throw new DomainException(5308, "名称不能包含控制字符");
            var parent = ResolveParent(entries, parentId, parentPath, create: parentId is null);
            EnsureAvailable(entries, parent.Id, name, null);
            if (entries.Count >= 100_000) throw new DomainException(41301, "内置存储条目数量达到上限");
            var blob = folder ? null : await binaries.PutAsync(userId, ManagedBinaryStore.Files, providerId,
                content ?? throw new DomainException(40001, "缺少文件内容"), maxBytes, ct);
            var item = new LocalFileEntry(Guid.NewGuid().ToString("N"), parent.Id, name, folder,
                blob?.Key, mime, blob?.SizeBytes ?? 0, blob?.Sha256 ?? Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            entries.Add(item.Id, item); return item;
        }, true, ct);

    public Task<LocalFileEntry> ReplaceAsync(Guid userId, Guid providerId, string id, Stream content, string mime, long maxBytes,
        CancellationToken ct = default) => TransactionAsync(userId, providerId, async entries =>
        {
            var item = Live(entries, id); if (item.IsFolder) throw new DomainException(40001, "文件夹没有文件内容");
            var blob = await binaries.PutAsync(userId, ManagedBinaryStore.Files, providerId, content, maxBytes, ct);
            var updated = item with { BlobKey = blob.Key, Etag = blob.Sha256, Size = blob.SizeBytes, ContentType = mime, ModifiedAt = DateTimeOffset.UtcNow };
            entries[id] = updated; return updated;
        }, true, ct);

    public Task<LocalFileEntry> PublishUploadAsync(Guid userId, Guid providerId, Guid uploadId, string? parentId, string parentPath,
        string name, string mime, ManagedBinaryObject blob, CancellationToken ct) =>
        TransactionAsync(userId, providerId, entries =>
        {
            var prior = entries.Values.SingleOrDefault(item => item.UploadId == uploadId.ToString("N"));
            if (prior is not null) return Task.FromResult(Live(entries, prior.Id));
            var parent = ResolveParent(entries, parentId, parentPath, false); EnsureAvailable(entries, parent.Id, name, null);
            if (entries.Count >= 100_000) throw new DomainException(41301, "内置存储条目数量达到上限");
            var item = new LocalFileEntry(Guid.NewGuid().ToString("N"), parent.Id, name, false, blob.Key, mime,
                blob.SizeBytes, blob.Sha256, DateTimeOffset.UtcNow, UploadId: uploadId.ToString("N"));
            entries.Add(item.Id, item); return Task.FromResult(item);
        }, true, ct);

    public Task<string> MoveAsync(Guid userId, Guid providerId, string id, string? name, string? parentId, CancellationToken ct = default) =>
        TransactionAsync(userId, providerId, entries =>
        {
            var item = Live(entries, id); if (id == RootId) throw new DomainException(5337, "不能修改内置存储根目录");
            var target = Live(entries, parentId ?? item.ParentId!);
            if (!target.IsFolder) throw new DomainException(5304, "目标不是文件夹");
            for (var cursor = target; cursor.ParentId is not null; cursor = Live(entries, cursor.ParentId))
                if (cursor.Id == id) throw new DomainException(5337, "不能移动到自身或子目录");
            var newName = name ?? item.Name; FileNameValidator.EnsureValidName(newName);
            if (newName.Any(char.IsControl)) throw new DomainException(5308, "名称不能包含控制字符");
            EnsureAvailable(entries, target.Id, newName, id);
            entries[id] = item with { Name = newName, ParentId = target.Id, ModifiedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(id);
        }, true, ct);

    public Task SetTrashedAsync(Guid userId, Guid providerId, string id, bool trashed, CancellationToken ct = default) =>
        TransactionAsync(userId, providerId, entries =>
        {
            if (id == RootId) throw new DomainException(5337, "不能删除内置存储根目录");
            if (!entries.TryGetValue(id, out var item)) return Task.FromResult(false);
            if (!trashed)
            {
                _ = Live(entries, item.ParentId!); EnsureAvailable(entries, item.ParentId!, item.Name, id);
            }
            var descendants = new HashSet<string>(StringComparer.Ordinal) { id };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var candidate in entries.Values)
                    if (candidate.ParentId is not null && descendants.Contains(candidate.ParentId) && descendants.Add(candidate.Id)) changed = true;
            }
            var group = trashed ? Guid.NewGuid().ToString("N") : item.TrashGroup;
            foreach (var child in descendants)
            {
                var candidate = entries[child];
                if (trashed && candidate.Trashed || !trashed && candidate.TrashGroup != group) continue;
                entries[child] = candidate with { Trashed = trashed, TrashGroup = trashed ? group : null, ModifiedAt = DateTimeOffset.UtcNow };
            }
            return Task.FromResult(true);
        }, true, ct);

    /// <summary>
    /// Permanently removes a trashed local entry and its trashed descendants from the
    /// catalog. Returns blob keys that no remaining catalog entry references, so the
    /// caller only deletes content-addressed bytes that are genuinely unreachable.
    /// </summary>
    public Task<IReadOnlyList<string>> PurgeTrashedAsync(Guid userId, Guid providerId, string id,
        CancellationToken ct = default) =>
        PurgeTrashedAsync(userId, providerId, id, static (_, _) => Task.CompletedTask, ct);

    /// <summary>
    /// Purges the catalog and invokes <paramref name="deleteBlobs"/> while the catalog
    /// lock is still held. A restore cannot race between planning and deletion, and a
    /// blob failure leaves the catalog untouched so the same purge can be retried.
    /// </summary>
    public Task<IReadOnlyList<string>> PurgeTrashedAsync(Guid userId, Guid providerId, string id,
        Func<IReadOnlyList<string>, CancellationToken, Task> deleteBlobs,
        CancellationToken ct = default) =>
        TransactionAsync<IReadOnlyList<string>>(userId, providerId, async entries =>
        {
            var (purgeIds, removed) = PurgePlan(entries, id);
            if (purgeIds.Count == 0) return [];
            var unreferenced = UnreferencedBlobKeys(entries, purgeIds, removed);
            await deleteBlobs(unreferenced, ct);
            foreach (var key in purgeIds) entries.Remove(key);
            return unreferenced;
        }, true, ct);

    private static (HashSet<string> Ids, IReadOnlyList<LocalFileEntry> Removed) PurgePlan(
        IReadOnlyDictionary<string, LocalFileEntry> entries, string id)
    {
        if (id == RootId) throw new DomainException(5337, "不能永久删除内置存储根目录");
        if (!entries.TryGetValue(id, out var item))
            return (new HashSet<string>(StringComparer.Ordinal), Array.Empty<LocalFileEntry>());
        if (!item.Trashed) throw new DomainException(40901, "文件不在回收站，不能永久删除");

        var purgeIds = new HashSet<string>(StringComparer.Ordinal) { id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in entries.Values)
            {
                if (candidate.ParentId is not null && purgeIds.Contains(candidate.ParentId) &&
                    purgeIds.Add(candidate.Id))
                {
                    changed = true;
                }
            }
        }

        // SetTrashedAsync always marks the complete subtree. A live descendant therefore
        // means the catalog is inconsistent; deleting it here would destroy user data.
        if (purgeIds.Any(key => !entries[key].Trashed))
            throw new DomainException(40901, "目录内仍有未删除条目，请刷新文件树后重试");

        return (purgeIds, purgeIds.Select(key => entries[key]).ToArray());
    }

    private static IReadOnlyList<string> UnreferencedBlobKeys(
        IReadOnlyDictionary<string, LocalFileEntry> entries,
        IReadOnlySet<string> purgeIds,
        IReadOnlyList<LocalFileEntry> removed)
    {
        var stillReferenced = entries
            .Where(pair => !purgeIds.Contains(pair.Key) && pair.Value.BlobKey is not null)
            .Select(pair => pair.Value.BlobKey!)
            .ToHashSet(StringComparer.Ordinal);
        return removed
            .Where(entry => entry.BlobKey is not null && !stillReferenced.Contains(entry.BlobKey))
            .Select(entry => entry.BlobKey!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string PathOf(IReadOnlyDictionary<string, LocalFileEntry> entries, LocalFileEntry item)
    {
        var parts = new List<string>(); var seen = new HashSet<string>();
        while (item.ParentId is not null)
        {
            if (!seen.Add(item.Id)) throw new InvalidDataException("Invalid local file hierarchy");
            parts.Add(item.Name); item = entries[item.ParentId];
        }
        parts.Reverse(); return "/" + string.Join('/', parts);
    }

    public static LocalFileEntry Live(IReadOnlyDictionary<string, LocalFileEntry> entries, string id) =>
        entries.TryGetValue(id, out var item) && !item.Trashed ? item : throw new Providers.Abstractions.ProviderItemNotFoundException("内置存储条目不存在");

    public static LocalFileEntry ResolveParent(Dictionary<string, LocalFileEntry> entries, string? id, string path, bool create)
    {
        if (!path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(part => part is "." or ".."))
            throw new DomainException(5308, "文件路径无效");
        if (id is not null)
        {
            var parent = Live(entries, id);
            if (!parent.IsFolder || !string.Equals(PathOf(entries, parent), path.TrimEnd('/').Length == 0 ? "/" : path.TrimEnd('/'), StringComparison.Ordinal))
                throw new DomainException(5304, "目标目录与文件来源不一致");
            return parent;
        }
        var current = entries[RootId];
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            FileNameValidator.EnsureValidName(segment);
            if (segment.Any(char.IsControl)) throw new DomainException(5308, "目录名称不能包含控制字符");
            var next = entries.Values.SingleOrDefault(item => !item.Trashed && item.ParentId == current.Id && string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null && create)
            {
                if (entries.Count >= 100_000) throw new DomainException(41301, "内置存储条目数量达到上限");
                next = new(Guid.NewGuid().ToString("N"), current.Id, segment, true, null, null, 0, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
                entries.Add(next.Id, next);
            }
            current = next is { IsFolder: true } ? next : throw new DomainException(5304, "目标目录不存在");
        }
        return current;
    }

    private static void EnsureAvailable(IEnumerable<KeyValuePair<string, LocalFileEntry>> entries, string parent, string name, string? ownId)
    {
        if (entries.Any(pair => !pair.Value.Trashed && pair.Value.ParentId == parent && pair.Value.Id != ownId &&
            string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))) throw new DomainException(40901, "已存在同名条目，请修改名称");
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 200) { await Task.Delay(50, ct); }
            catch (IOException) { throw new DomainException(50301, "内置存储正在处理目录，请稍后重试"); }
        }
    }
}
