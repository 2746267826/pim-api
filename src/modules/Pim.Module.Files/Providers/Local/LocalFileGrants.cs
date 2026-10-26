using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Storage;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Services;

namespace Pim.Module.Files.Providers.Local;

public sealed record LocalReadGrant(Guid UserId, Guid ProviderId, string ItemId, string Path, string Etag, bool Preview, long Expires);
public sealed record LocalUploadGrant(Guid UserId, Guid ProviderId, Guid UploadId, long Expires);
public sealed record LocalUploadReply(string? Id, IReadOnlyList<string>? NextExpectedRanges);

public sealed partial class LocalFileGrants(IDataProtectionProvider protection, LocalFileStore store,
    IHttpContextAccessor http, TimeProvider? clock = null)
{
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxPartBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _reads = protection.CreateProtector("PIM.Files.Local.Read.v1");
    private readonly IDataProtector _uploads = protection.CreateProtector("PIM.Files.Local.Upload.v1");
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private string Prefix => http.HttpContext?.Request.PathBase.Value ?? "";
    public static bool CanPreview(string? mime) => mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "application/pdf" or "text/plain";
    public string Read(Guid userId, Guid providerId, RemoteItem item, bool preview) => Prefix +
        "/api/v1/files/local/content?ticket=" + Uri.EscapeDataString(_reads.Protect(JsonSerializer.Serialize(
            new LocalReadGrant(userId, providerId, item.Id, item.Path!, item.ETag!, preview, Now.AddMinutes(5).ToUnixTimeSeconds()), Json)));
    public LocalReadGrant ReadTicket(string ticket)
    {
        var grant = Decode<LocalReadGrant>(_reads, ticket);
        if (grant.Expires <= Now.ToUnixTimeSeconds() || grant.ProviderId != LocalFileProvider.IdFor(grant.UserId)) throw InvalidGrant();
        return grant;
    }

    public async Task<RemoteUploadSession> BeginUploadAsync(Guid userId, Guid providerId, RemoteFolderRef parent, string name, CancellationToken ct)
    {
        FileNameValidator.EnsureValidName(name);
        if (name.Any(char.IsControl)) throw new DomainException(5308, "名称不能包含控制字符");
        return await store.TransactionAsync(userId, providerId, async entries =>
        {
            var resolved = LocalFileStore.ResolveParent(entries, parent.ExternalId, parent.Path, false);
            if (entries.Values.Any(item => !item.Trashed && item.ParentId == resolved.Id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new DomainException(40901, "已存在同名条目，请修改名称");
            if (await CleanupUploadsAsync(userId, ct) >= 10) throw new DomainException(41301, "未完成上传会话达到 10 个，请完成或等待过期后重试");
            var uploadId = Guid.NewGuid(); var expiry = Now.AddHours(24);
            var directory = UploadDirectory(userId, uploadId); Directory.CreateDirectory(directory);
            await SaveAsync(Path.Combine(directory, "state.json"), new UploadState(resolved.Id, parent.Path, name, expiry.ToUnixTimeSeconds()), ct);
            var token = _uploads.Protect(JsonSerializer.Serialize(new LocalUploadGrant(userId, providerId, uploadId, expiry.ToUnixTimeSeconds()), Json));
            return new RemoteUploadSession(Prefix + "/api/v1/files/local/uploads/" + uploadId.ToString("D") + "?ticket=" + Uri.EscapeDataString(token), expiry);
        }, false, ct);
    }

    public LocalUploadGrant UploadTicket(Guid id, string ticket)
    {
        var grant = Decode<LocalUploadGrant>(_uploads, ticket);
        if (grant.UploadId != id || grant.Expires <= Now.ToUnixTimeSeconds() || grant.ProviderId != LocalFileProvider.IdFor(grant.UserId)) throw InvalidGrant();
        return grant;
    }

    public async Task<LocalUploadReply> UploadAsync(LocalUploadGrant grant, string contentRange, Stream body, CancellationToken ct)
    {
        var range = RangePattern().Match(contentRange);
        if (!range.Success || !long.TryParse(range.Groups[1].Value, out var start) ||
            !long.TryParse(range.Groups[2].Value, out var end) || !long.TryParse(range.Groups[3].Value, out var total) ||
            total <= 0 || total > MaxFileBytes || start > end || end >= total || end - start + 1 > MaxPartBytes)
            throw new DomainException(40001, "上传分片范围无效或超过内置存储上限");
        using var buffer = new MemoryStream(); var bytes = new byte[64 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(bytes, ct); if (read == 0) break;
            if (buffer.Length + read > end - start + 1) throw new DomainException(41301, "分片长度超过声明范围");
            buffer.Write(bytes, 0, read);
        }
        if (buffer.Length != end - start + 1) throw new DomainException(40001, "分片长度与声明不一致");
        var directory = UploadDirectory(grant.UserId, grant.UploadId);
        await using var lease = await LockAsync(Path.Combine(directory, "session.lock"), ct);
        var statePath = Path.Combine(directory, "state.json");
        if (!File.Exists(statePath)) throw new DomainException(40401, "上传会话不存在");
        var state = JsonSerializer.Deserialize<UploadState>(await File.ReadAllTextAsync(statePath, ct), Json) ?? throw new InvalidDataException("Invalid local upload state");
        if (state.Expires <= Now.ToUnixTimeSeconds()) throw InvalidGrant();
        if (state.Total is not null && state.Total != total) throw new DomainException(40901, "上传会话不能改变文件大小");
        if (state.SealedHash is not null)
        {
            var saved = store.Binaries.ObjectPath(grant.UserId, ManagedBinaryStore.Files, grant.ProviderId, state.SealedHash);
            if (!File.Exists(saved)) saved = store.Binaries.ObjectPath(grant.UserId, ManagedBinaryStore.Uploads, grant.UploadId, ManagedBinaryStore.UploadKey);
            await using var stream = File.OpenRead(saved); stream.Position = start;
            var prior = new byte[checked((int)buffer.Length)]; await stream.ReadExactlyAsync(prior, ct);
            if (!prior.AsSpan().SequenceEqual(buffer.GetBuffer().AsSpan(0, prior.Length))) throw new DomainException(40901, "完成的上传不能替换为不同分片");
        }
        if (state.CompletedId is not null)
        {
            var completed = await store.GetAsync(grant.UserId, grant.ProviderId, state.CompletedId, ct);
            if (completed.Etag != state.SealedHash || completed.Name != state.Name || completed.ParentId != state.ParentId)
                throw new DomainException(40901, "上传结果已被修改，请刷新文件列表");
            return new(state.CompletedId, null);
        }
        state = state with { Total = total };
        if (state.SealedHash is null)
        {
            await SaveAsync(statePath, state, ct);
            var received = await store.Binaries.AppendUploadAsync(grant.UserId, grant.UploadId, start, buffer.ToArray(), ct);
            if (received < total) return new(null, [$"{received}-"]);
            var source = store.Binaries.ObjectPath(grant.UserId, ManagedBinaryStore.Uploads, grant.UploadId, ManagedBinaryStore.UploadKey);
            await using var stream = File.OpenRead(source);
            state = state with { SealedHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant() };
            await SaveAsync(statePath, state, ct);
        }
        var blob = await store.Binaries.SealUploadAsync(grant.UserId, grant.UploadId, grant.ProviderId, state.SealedHash!, total, ct);
        var mimeTypes = new FileExtensionContentTypeProvider();
        var mime = mimeTypes.TryGetContentType(state.Name, out var detected) ? detected : "application/octet-stream";
        var item = await store.PublishUploadAsync(grant.UserId, grant.ProviderId, grant.UploadId,
            state.ParentId, state.ParentPath, state.Name, mime, blob, ct);
        await SaveAsync(statePath, state with { CompletedId = item.Id }, ct);
        return new(item.Id, null);
    }

    private string UploadDirectory(Guid userId, Guid uploadId)
    {
        var path = Path.Combine(store.Binaries.AccountDirectory(userId), "uploads", uploadId.ToString("D"));
        foreach (var name in new[] { "state.json", "session.lock" }) store.Binaries.CheckPath(Path.Combine(path, name));
        return path;
    }
    private async Task<int> CleanupUploadsAsync(Guid userId, CancellationToken ct)
    {
        var root = Path.Combine(store.Binaries.AccountDirectory(userId), "uploads");
        store.Binaries.CheckPath(root);
        if (!Directory.Exists(root)) return 0;
        var pending = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var id)) continue;
            var statePath = Path.Combine(UploadDirectory(userId, id), "state.json");
            if (!File.Exists(statePath)) continue;
            var state = JsonSerializer.Deserialize<UploadState>(await File.ReadAllTextAsync(statePath, ct), Json) ?? throw new InvalidDataException("Invalid upload state");
            if (state.Expires > Now.ToUnixTimeSeconds()) { if (state.CompletedId is null) pending++; continue; }
            FileStream lease;
            try { lease = new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; }
            await using (lease) { await store.Binaries.DeleteCollectionAsync(userId, ManagedBinaryStore.Uploads, id, ct); }
            Directory.Delete(directory, recursive: true);
        }
        return pending;
    }
    private static T Decode<T>(IDataProtector protector, string token)
    {
        try
        {
            if (token.Length > 4096) throw InvalidGrant();
            return JsonSerializer.Deserialize<T>(protector.Unprotect(token), Json) ?? throw InvalidGrant();
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or ArgumentException) { throw InvalidGrant(); }
    }
    private static DomainException InvalidGrant() => new(40301, "文件授权已失效，请重新获取链接");
    private static async Task SaveAsync<T>(string path, T value, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(stream, value, Json, ct); await stream.FlushAsync(ct); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DomainException(40401, "上传会话不存在");
        for (var i = 0; ; i++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 200) { await Task.Delay(50, ct); }
            catch (IOException) { throw new DomainException(50301, "该上传会话正在处理，请稍后重试"); }
        }
    }
    private sealed record UploadState(string ParentId, string ParentPath, string Name, long Expires,
        long? Total = null, string? SealedHash = null, string? CompletedId = null);
    [GeneratedRegex("\\Abytes ([0-9]+)-([0-9]+)/([0-9]+)\\z")] private static partial Regex RangePattern();
}
