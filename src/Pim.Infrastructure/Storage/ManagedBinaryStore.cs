using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Storage;

public sealed class ManagedBinaryOptions
{
    public string Root { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PIM", "server", "storage")
        : "/data/pim/storage";
    public long MaxUserBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public int MaxUserObjects { get; set; } = 100_000;
}

public sealed record ManagedBinaryObject(string Key, string Sha256, long SizeBytes);

/// <summary>Bounded immutable bytes shared by file providers and managed note materials.</summary>
public sealed partial class ManagedBinaryStore(IOptions<ManagedBinaryOptions> options)
{
    public const string Files = "files";
    public const string NoteParts = "note-parts";
    public const string Uploads = "file-uploads";
    public static string UploadKey => new string('0', 64);
    private readonly ManagedBinaryOptions _options = options.Value;

    public string AccountDirectory(Guid userId)
    {
        if (userId == Guid.Empty || !Path.IsPathFullyQualified(_options.Root)) throw new DomainException(40001, "存储身份或配置无效");
        var path = Path.Combine(Path.GetFullPath(_options.Root), userId.ToString("D"));
        CheckPath(path); return path;
    }

    public string ObjectPath(Guid userId, string scope, Guid collectionId, string key)
    {
        if (scope is not (Files or NoteParts or Uploads) || collectionId == Guid.Empty || !Digest().IsMatch(key))
            throw new DomainException(40001, "存储对象标识无效");
        var path = Path.Combine(AccountDirectory(userId), scope, collectionId.ToString("D"), key + ".blob");
        CheckPath(path); return path;
    }

    public async Task<ManagedBinaryObject> PutAsync(Guid userId, string scope, Guid collectionId, Stream input,
        long maxBytes, CancellationToken ct = default, string? key = null, string? expectedSha256 = null)
    {
        if (maxBytes < 0 || maxBytes > 64L * 1024 * 1024 * 1024) throw new DomainException(40001, "存储大小上限无效");
        _ = ObjectPath(userId, scope, collectionId, key ?? new string('0', 64));
        await using var lease = await LockAsync(userId, ct);
        var folder = Path.Combine(AccountDirectory(userId), scope, collectionId.ToString("D"));
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        var objects = Objects(userId).Select(path => new FileInfo(path)).ToList();
        var used = objects.Sum(item => item.Length);
        var available = Math.Max(0, _options.MaxUserBytes - used);
        long length = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct); if (read == 0) break;
                    length = checked(length + read);
                    if (length > maxBytes) throw new DomainException(41301, "文件超过内置存储上限");
                    hash.AppendData(buffer, 0, read); await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await stream.FlushAsync(ct); stream.Flush(true);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null && expectedSha256 != digest) throw new DomainException(40001, "文件摘要不匹配");
            var objectKey = key ?? digest;
            var target = ObjectPath(userId, scope, collectionId, objectKey);
            if (File.Exists(target))
            {
                await using var old = File.OpenRead(target);
                var oldHash = Convert.ToHexString(await SHA256.HashDataAsync(old, ct)).ToLowerInvariant();
                if (old.Length != length || oldHash != digest) throw new DomainException(40901, "固定存储对象已存在，不能覆盖不同内容");
            }
            else
            {
                if (length > available || objects.Count >= _options.MaxUserObjects) throw new DomainException(41301, "内置存储容量或对象数量达到上限");
                File.Move(temp, target);
            }
            return new(objectKey, digest, length);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<long> AppendUploadAsync(Guid userId, Guid uploadId, long start, byte[] bytes, CancellationToken ct)
    {
        var path = ObjectPath(userId, Uploads, uploadId, UploadKey);
        await using var lease = await LockAsync(userId, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) && Objects(userId).Take(_options.MaxUserObjects).Count() >= _options.MaxUserObjects)
            throw new DomainException(41301, "内置存储对象数量达到上限");
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (start > stream.Length) return stream.Length;
        if (start < stream.Length)
        {
            var overlap = checked((int)Math.Min(bytes.Length, stream.Length - start));
            stream.Position = start; var old = new byte[overlap]; await stream.ReadExactlyAsync(old, ct);
            if (!old.AsSpan().SequenceEqual(bytes.AsSpan(0, overlap))) throw new DomainException(40901, "已确认分片不能替换为不同内容");
            if (overlap == bytes.Length) return stream.Length;
            bytes = bytes[overlap..];
        }
        var used = Objects(userId).Sum(file => new FileInfo(file).Length);
        if (bytes.LongLength > _options.MaxUserBytes - used) throw new DomainException(41301, "内置存储空间不足，已收分片保留");
        stream.Position = stream.Length; await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
        return stream.Length;
    }

    private IEnumerable<string> Objects(Guid userId) => Directory.EnumerateFiles(AccountDirectory(userId), "*.blob",
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });

    public void CheckPath(string path)
    {
        var root = Path.GetFullPath(_options.Root); var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new DomainException(40301, "存储路径越界");
        var current = root;
        foreach (var component in new[] { "" }.Concat(relative.Split(Path.DirectorySeparatorChar)))
        {
            if (component.Length > 0) current = Path.Combine(current, component);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new DomainException(40301, "内置存储不读取链接或重解析目录"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }

    public async Task<ManagedBinaryObject> SealUploadAsync(Guid userId, Guid uploadId, Guid providerId,
        string digest, long size, CancellationToken ct)
    {
        var source = ObjectPath(userId, Uploads, uploadId, UploadKey);
        var target = ObjectPath(userId, Files, providerId, digest);
        await using var lease = await LockAsync(userId, ct);
        async Task Verify(string path)
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length != size || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant() != digest)
                throw new DomainException(40001, "上传内容校验失败");
        }
        if (File.Exists(target)) { await Verify(target); if (File.Exists(source)) File.Delete(source); }
        else
        {
            await Verify(source); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(source, target);
        }
        return new(digest, digest, size);
    }

    public async Task DeleteAsync(Guid userId, string scope, Guid collectionId, string key, CancellationToken ct = default)
    {
        var path = ObjectPath(userId, scope, collectionId, key);
        await using var lease = await LockAsync(userId, ct); File.Delete(path);
    }

    public async Task DeleteCollectionAsync(Guid userId, string scope, Guid collectionId, CancellationToken ct = default)
    {
        var path = Path.GetDirectoryName(ObjectPath(userId, scope, collectionId, new string('0', 64)))!;
        await using var lease = await LockAsync(userId, ct);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private async Task<FileStream> LockAsync(Guid userId, CancellationToken ct)
    {
        var root = AccountDirectory(userId); Directory.CreateDirectory(root);
        CheckPath(Path.Combine(root, "binary.lock"));
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(root, "binary.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 200) { await Task.Delay(50, ct); }
            catch (IOException) { throw new DomainException(50301, "内置存储正在处理文件，请稍后重试"); }
        }
    }

    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)] private static partial Regex Digest();
}
