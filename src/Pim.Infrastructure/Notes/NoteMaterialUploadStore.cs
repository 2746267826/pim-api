using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Storage;

namespace Pim.Infrastructure.Notes;

/// <summary>Immutable, account-isolated material versions on the server's persistent data volume.</summary>
public sealed class NoteMaterialUploadStore(IOptions<NoteMaterialUploadOptions> options, TimeProvider? clock = null, ManagedBinaryStore? binaries = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly NoteMaterialUploadOptions ReadLimits = new()
    { MaxVersionBytes = 64L * 1024 * 1024 * 1024, MaxUserReservedBytes = 64L * 1024 * 1024 * 1024, MaxFiles = 100_000 };
    private readonly NoteMaterialUploadOptions _options = options.Value;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ManagedBinaryStore _binaries = binaries ?? new(Options.Create(new ManagedBinaryOptions { Root = Path.Combine(options.Value.StorageRoot, "binary") }));

    public NoteMaterialUploadLimitsDto Limits => new(NoteMaterialUploadOptions.PartSizeBytes, NoteMaterialUploadOptions.MaxMetadataCharacters,
        _options.MaxVersionBytes, _options.MaxUserReservedBytes, _options.MaxFiles,
        _options.MaxPendingVersions, _options.MaxVersionsPerUser, _options.UploadExpiryHours);

    public async Task<NoteMaterialUploadDto> BeginAsync(Guid userId, BeginNoteMaterialUploadRequest request, CancellationToken ct = default)
    {
        var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
        var requestJson = JsonSerializer.Serialize(request, Json);
        var fingerprint = NoteMaterialManifestValidator.Hash(requestJson);
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var key = Key(request.VersionId);
        if (ledger.Versions.TryGetValue(key, out var existing))
        {
            if (existing.Fingerprint != fingerprint) throw Conflict("同一材料版本已经绑定到另一份固定输入，原版本保留");
            return await DescribeAsync(userId, request.VersionId, existing, ledger, files, ct);
        }
        var total = files.Sum(file => file.SizeBytes);
        if (total > _options.MaxVersionBytes || files.Count > _options.MaxFiles)
            throw new DomainException(41301, "本次材料超过服务器单版本容量或数量上限");
        var reserved = checked(total + Encoding.UTF8.GetByteCount(requestJson) + 1024L);
        if (ledger.Versions.Count >= _options.MaxVersionsPerUser ||
            ledger.Versions.Values.Count(item => item.Phase == "Uploading") >= _options.MaxPendingVersions ||
            reserved > _options.MaxUserReservedBytes - ledger.Versions.Values.Sum(item => item.SizeBytes))
            throw new DomainException(41301, "服务器笔记存储配额或待上传数量已达上限，请清理后重试；本机原稿保留");
        var folder = VersionFolder(userId, request.VersionId);
        Directory.CreateDirectory(folder);
        var header = Path.Combine(folder, "request.json");
        if (File.Exists(header))
        {
            // Recover a request whose header was persisted just before its ledger entry.
            var orphan = await ReadAsync<BeginNoteMaterialUploadRequest>(header, ct);
            if (NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(orphan, Json)) != fingerprint)
                throw Conflict("服务器保留了同标识的另一份材料，不能覆盖");
        }
        else await WriteAsync(header, request, ct);
        var now = _clock.GetUtcNow();
        var entry = new StoredVersion(request.NoteId, request.BaseVersionId, fingerprint, reserved,
            now, now.AddHours(_options.UploadExpiryHours), "Uploading");
        ledger.Versions.Add(key, entry);
        await SaveLedgerAsync(userId, ledger, ct);
        return await DescribeAsync(userId, request.VersionId, entry, ledger, files, ct);
    }

    public async Task<NoteMaterialVersionDto> GetAsync(Guid userId, Guid versionId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var entry = Find(ledger, versionId);
        var request = await RequestAsync(userId, versionId, ct);
        var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
        return new(await DescribeAsync(userId, versionId, entry, ledger, files, ct), request.InputJson, request.ManifestJson);
    }

    public async Task<IReadOnlyList<NoteMaterialUploadDto>> ListAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var result = new List<NoteMaterialUploadDto>();
        foreach (var (key, entry) in ledger.Versions.Where(item => item.Value.NoteId == noteId).OrderByDescending(item => item.Value.CreatedAt))
        {
            var versionId = Guid.ParseExact(key, "D");
            var request = await RequestAsync(userId, versionId, ct);
            result.Add(await DescribeAsync(userId, versionId, entry, ledger, NoteMaterialManifestValidator.Validate(request, ReadLimits), ct));
        }
        return result;
    }

    public Task<NoteAsrSourcesDto> ListAsrSourcesAsync(Guid owner, int offset, int limit, CancellationToken ct) =>
        ListAsrSourcesAsync(owner, offset, limit, ct, includeWithoutAudio: true);

    internal async Task<NoteAsrSourcesDto> ListAsrSourcesAsync(Guid owner, int offset, int limit,
        CancellationToken ct, bool includeWithoutAudio)
    {
        if (offset < 0 || limit is < 1 or > 50) throw Invalid("材料分页范围无效");
        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadLedgerAsync(owner, ct);
        var versions = ledger.Versions.Where(x => x.Value.Phase != "Uploading").OrderByDescending(x => x.Value.CreatedAt).ToArray();
        var sources = new List<(Guid VersionId, StoredVersion Entry, string Title, int Recordings, long Samples)>();
        foreach (var (key, entry) in versions)
        {
            var versionId = Guid.Parse(key); var request = await RequestAsync(owner, versionId, ct);
            using var input = JsonDocument.Parse(request.InputJson);
            var recordings = input.RootElement.GetProperty("recordings");
            if (!includeWithoutAudio && recordings.GetArrayLength() == 0) continue;
            var samples = recordings.EnumerateArray().Sum(r => r.GetProperty("chunks").EnumerateArray().Sum(c => c.GetProperty("samples").GetInt64()));
            sources.Add((versionId, entry, input.RootElement.GetProperty("note").GetProperty("title").GetString() ?? "未命名笔记",
                recordings.GetArrayLength(), samples));
        }
        var page = sources.Skip(offset).Take(limit).Select(x => new NoteAsrSourceDto(x.Entry.NoteId, x.VersionId, x.Title,
            x.Entry.CreatedAt, Current(ledger, x.Entry.NoteId) == x.VersionId, x.Entry.Phase == "Conflict",
            x.Recordings, x.Samples, NoteAsrPlan.SampleRate)).ToArray();
        return new(sources.Count, offset, page);
    }

    public async Task UploadPartAsync(Guid userId, Guid versionId, int fileIndex, int partIndex,
        string sha256, Stream body, CancellationToken ct = default)
    {
        if (!NoteMaterialManifestValidator.IsDigest(sha256)) throw Invalid("分块摘要无效");
        // Limit memory and body consumption before acquiring the account's disk transaction.
        using var content = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (content.Length + read > NoteMaterialUploadOptions.PartSizeBytes) throw new DomainException(41301, "上传分块超过上限");
            content.Write(buffer, 0, read);
        }
        var actualHash = Convert.ToHexString(SHA256.HashData(content.GetBuffer().AsSpan(0, checked((int)content.Length)))).ToLowerInvariant();
        if (actualHash != sha256) throw Invalid("上传分块摘要不匹配，请重试此分块");
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var entry = Find(ledger, versionId);
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct), ReadLimits);
        var file = FileAt(files, fileIndex);
        if (partIndex < 0 || partIndex >= PartCount(file) || content.Length != PartLength(file, partIndex))
            throw Invalid("分块位置或长度与固定清单不一致");
        var target = PartFile(userId, versionId, fileIndex, partIndex);
        if (File.Exists(target))
        {
            await using var old = File.OpenRead(target);
            if (old.Length != content.Length || await HashAsync(old, ct) != sha256)
                throw Conflict("该位置已经保存另一份分块，不能覆盖；原材料保留");
            return;
        }
        if (entry.Phase != "Uploading") throw Conflict("完整材料版本已经固定，不能继续修改");
        content.Position = 0;
        await _binaries.PutAsync(userId, ManagedBinaryStore.NoteParts, versionId, content, content.Length, ct,
            PartKey(fileIndex, partIndex), sha256);
    }

    public async Task<NoteMaterialUploadDto> CompleteAsync(Guid userId, Guid versionId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var entry = Find(ledger, versionId);
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct), ReadLimits);
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            await VerifyFileAsync(userId, versionId, fileIndex, files[fileIndex], ct);
        if (entry.Phase == "Uploading")
        {
            var current = Current(ledger, entry.NoteId);
            var conflict = current != entry.BaseVersionId;
            entry = entry with { Phase = conflict ? "Conflict" : "Uploaded", HasConflict = conflict };
            ledger.Versions[Key(versionId)] = entry;
            if (!conflict) ledger.Heads[Key(entry.NoteId)] = versionId;
            // Publication and head comparison share one atomic ledger write.
            await SaveLedgerAsync(userId, ledger, ct);
        }
        return await DescribeAsync(userId, versionId, entry, ledger, files, ct);
    }

    public async Task<NoteMaterialUploadDto> PromoteAsync(Guid userId, Guid versionId,
        Guid? expectedCurrentVersionId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        var entry = Find(ledger, versionId);
        if (entry.Phase == "Uploading") throw Conflict("材料尚未齐备，不能选为当前版本");
        var current = Current(ledger, entry.NoteId);
        if (current != versionId && current != expectedCurrentVersionId)
            throw Conflict("另一端已更新当前版本，请重新查看后再选择；双方材料仍保留");
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct), ReadLimits);
        for (var index = 0; index < files.Count; index++) await VerifyFileAsync(userId, versionId, index, files[index], ct);
        ledger.Heads[Key(entry.NoteId)] = versionId;
        entry = entry with { Phase = "Uploaded", HasConflict = false };
        ledger.Versions[Key(versionId)] = entry;
        await SaveLedgerAsync(userId, ledger, ct);
        return await DescribeAsync(userId, versionId, entry, ledger, files, ct);
    }

    public async Task ReadFileAsync(Guid userId, Guid versionId, int fileIndex, Stream output, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        if (Find(ledger, versionId).Phase == "Uploading") throw Conflict("材料尚未提交完整");
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct), ReadLimits);
        var file = FileAt(files, fileIndex);
        await VerifyFileAsync(userId, versionId, fileIndex, file, ct);
        for (var part = 0; part < PartCount(file); part++)
        {
            await using var stream = File.OpenRead(PartFile(userId, versionId, fileIndex, part));
            await stream.CopyToAsync(output, ct);
        }
    }

    internal async Task<byte[]> ReadAudioSliceAsync(Guid owner, Guid versionId, int fileIndex,
        long offsetSamples, long samples, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadLedgerAsync(owner, ct);
        if (Find(ledger, versionId).Phase == "Uploading") throw Conflict("材料尚未提交完整");
        var file = FileAt(NoteMaterialManifestValidator.Validate(await RequestAsync(owner, versionId, ct), ReadLimits), fileIndex);
        if (file.Kind != "AudioChunk" || offsetSamples < 0 || samples is <= 0 or > NoteAsrPlan.SamplesPerCall ||
            offsetSamples > file.Samples || samples > file.Samples - offsetSamples) throw Invalid("录音分片范围无效");
        await VerifyFileAsync(owner, versionId, fileIndex, file, ct);
        var bytes = new byte[checked(44 + (int)samples * 2)];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16_000); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)samples * 2);
        long position = 44 + offsetSamples * 2; var written = 44;
        while (written < bytes.Length)
        {
            var part = (int)(position / NoteMaterialUploadOptions.PartSizeBytes);
            var within = position % NoteMaterialUploadOptions.PartSizeBytes;
            await using var stream = File.OpenRead(PartFile(owner, versionId, fileIndex, part));
            stream.Position = within;
            var count = (int)Math.Min(bytes.Length - written, stream.Length - within);
            if (count <= 0) throw Invalid("录音材料分块损坏");
            await stream.ReadExactlyAsync(bytes.AsMemory(written, count), ct); written += count; position += count;
        }
        return bytes;
    }

    internal async Task<byte[]> ReadImageBytesAsync(Guid owner, Guid versionId, int fileIndex, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadLedgerAsync(owner, ct);
        var entry = Find(ledger, versionId);
        if (entry.Phase is not ("Uploaded" or "Conflict")) throw Conflict("材料尚未提交完整");
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(owner, versionId, ct), ReadLimits);
        var file = FileAt(files, fileIndex);
        if (file.Kind is not ("Photo" or "InkPreview")) throw Invalid("派生视觉任务只能读取照片或手写预览图");
        if (file.Path.EndsWith(".webp", StringComparison.Ordinal)) throw Invalid("当前视觉模型不支持WebP，请在派生计划中转换或排除");
        if (file.SizeBytes > AiVisionMaterialService.MaxImageBytes)
            throw new DomainException(41301, "图片超过单次视觉调用4MiB上限，请在派生计划中排除或压缩后重新上传");
        var expectedMime = file.Path.EndsWith(".png", StringComparison.Ordinal) ? "image/png" :
            file.Path.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : throw Invalid("图片路径类型无效");
        await VerifyFileAsync(owner, versionId, fileIndex, file, ct);
        using var output = new MemoryStream(checked((int)file.SizeBytes));
        for (var part = 0; part < PartCount(file); part++)
        {
            await using var stream = File.OpenRead(PartFile(owner, versionId, fileIndex, part));
            await stream.CopyToAsync(output, ct);
        }
        var bytes = output.ToArray();
        if (bytes.Length != file.SizeBytes ||
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != file.Sha256 ||
            ImageFileSignature.Detect(bytes) != expectedMime)
            throw Conflict("材料中的图片已改变，不能作为本次视觉输入");
        return bytes;
    }

    public async Task CancelAsync(Guid userId, Guid versionId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        var key = Key(versionId);
        if (!ledger.Versions.TryGetValue(key, out var entry)) return;
        if (entry.Phase != "Uploading") throw Conflict("已提交的材料不能作为未完成上传取消，请使用删除笔记");
        ledger.Versions.Remove(key);
        ledger.Deletions.Add(key);
        await SaveLedgerAsync(userId, ledger, ct);
        await PurgeAsync(userId, ledger, ct);
    }

    public async Task ResetPartAsync(Guid userId, Guid versionId, int fileIndex, int partIndex, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        if (Find(ledger, versionId).Phase != "Uploading") throw Conflict("完整材料版本已经固定，不能删除分块");
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct), ReadLimits);
        var file = FileAt(files, fileIndex);
        if (partIndex < 0 || partIndex >= PartCount(file)) throw Invalid("分块位置无效");
        await _binaries.DeleteAsync(userId, ManagedBinaryStore.NoteParts, versionId, PartKey(fileIndex, partIndex), ct);
        var legacy = LegacyPartFile(userId, versionId, fileIndex, partIndex);
        if (File.Exists(legacy)) File.Delete(legacy);
    }

    public async Task DeleteNoteAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        foreach (var key in ledger.Versions.Where(item => item.Value.NoteId == noteId).Select(item => item.Key).ToArray())
        {
            ledger.Versions.Remove(key);
            ledger.Deletions.Add(key);
        }
        ledger.Heads.Remove(Key(noteId));
        await SaveLedgerAsync(userId, ledger, ct);
        await PurgeAsync(userId, ledger, ct);
    }

    private async Task VerifyFileAsync(Guid userId, Guid versionId, int index, NoteUploadedMaterial file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var header = new byte[44];
        var headerLength = 0;
        for (var part = 0; part < PartCount(file); part++)
        {
            var path = PartFile(userId, versionId, index, part);
            if (!File.Exists(path)) throw Conflict("材料尚未齐备，请继续上传缺失分块");
            await using var stream = File.OpenRead(path);
            if (stream.Length != PartLength(file, part)) throw Invalid("已保存分块损坏，完整性校验失败");
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct); if (read == 0) break;
                var take = Math.Min(header.Length - headerLength, read);
                buffer.AsSpan(0, take).CopyTo(header.AsSpan(headerLength)); headerLength += take;
                hash.AppendData(buffer, 0, read);
            }
        }
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
            throw Invalid("材料文件摘要不匹配，未接受为完整版本");
        if (file.Kind is "Photo" or "InkPreview")
        {
            var mime = ImageFileSignature.Detect(header.AsSpan(0, headerLength));
            var valid = file.Path.EndsWith(".png", StringComparison.Ordinal) && mime == "image/png" ||
                file.Path.EndsWith(".jpg", StringComparison.Ordinal) && mime == "image/jpeg" ||
                file.Path.EndsWith(".webp", StringComparison.Ordinal) && mime == "image/webp";
            if (!valid) throw Invalid("照片或手写预览图的文件类型与固定材料不一致");
        }
        if (file.Kind == "AudioChunk" && (headerLength != 44 ||
            !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != file.SizeBytes - 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) != 16_000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)) != 32_000 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)) != 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 16 ||
            !header.AsSpan(36, 4).SequenceEqual("data"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)) != file.Samples * 2))
            throw Invalid("录音格式或样本数不符合本次固定材料");
    }

    private Task<NoteMaterialUploadDto> DescribeAsync(Guid userId, Guid versionId, StoredVersion entry,
        Ledger ledger, IReadOnlyList<NoteUploadedMaterial> files, CancellationToken ct)
    {
        var current = Current(ledger, entry.NoteId);
        var described = files.Select((file, index) =>
        {
            ct.ThrowIfCancellationRequested();
            var uploaded = Enumerable.Range(0, PartCount(file)).Where(part =>
            {
                var item = new FileInfo(PartFile(userId, versionId, index, part));
                return item.Exists && item.Length == PartLength(file, part);
            }).ToArray();
            return new NoteMaterialUploadFileDto(index, file.Path, file.Kind, file.Sha256, file.SizeBytes, PartCount(file), uploaded);
        }).ToArray();
        return Task.FromResult(new NoteMaterialUploadDto(versionId, entry.NoteId, entry.BaseVersionId, current,
            entry.Phase, entry.CreatedAt, entry.ExpiresAt, current == versionId, entry.HasConflict, Limits, described));
    }

    private async Task PruneAsync(Guid userId, Ledger ledger, CancellationToken ct)
    {
        // Only this account's transaction can create a temporary file; leftovers belong to interrupted writes.
        foreach (var temp in Directory.EnumerateFiles(UserFolder(userId), "*.tmp", SearchOption.AllDirectories)) File.Delete(temp);
        var expired = ledger.Versions.Where(item => item.Value.Phase == "Uploading" && item.Value.ExpiresAt <= _clock.GetUtcNow())
            .Select(item => item.Key).ToArray();
        foreach (var key in expired) { ledger.Versions.Remove(key); ledger.Deletions.Add(key); }
        if (expired.Length > 0) await SaveLedgerAsync(userId, ledger, ct);
        await PurgeAsync(userId, ledger, ct);
    }

    private async Task PurgeAsync(Guid userId, Ledger ledger, CancellationToken ct)
    {
        if (ledger.Deletions.Count == 0) return;
        foreach (var key in ledger.Deletions)
        {
            ct.ThrowIfCancellationRequested(); var version = Guid.ParseExact(key, "D");
            await _binaries.DeleteCollectionAsync(userId, ManagedBinaryStore.NoteParts, version, ct);
            DeleteFolder(VersionFolder(userId, version));
        }
        ledger.Deletions.Clear();
        await SaveLedgerAsync(userId, ledger, ct);
    }

    private async Task<FileStream> LockAsync(Guid userId, CancellationToken ct)
    {
        if (!_options.IsValid()) throw new DomainException(50301, "笔记存储配置无效，请联系管理员");
        var folder = UserFolder(userId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "transaction.lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, ct); }
            catch (IOException) { throw new DomainException(50301, "本账号的材料操作尚在进行，请稍后重试"); }
        }
    }

    private string UserFolder(Guid userId)
    {
        if (userId == Guid.Empty) throw new DomainException(40101, "请先登录");
        return Path.Combine(Path.GetFullPath(_options.StorageRoot), Key(userId));
    }
    private string VersionFolder(Guid userId, Guid versionId) => Path.Combine(UserFolder(userId), Key(versionId));
    private string LegacyPartFile(Guid userId, Guid versionId, int file, int part) => Path.Combine(VersionFolder(userId, versionId),
        "parts", file.ToString(CultureInfo.InvariantCulture), part.ToString(CultureInfo.InvariantCulture) + ".part");
    private static string PartKey(int file, int part) => NoteMaterialManifestValidator.Hash($"{file}/{part}");
    private string PartFile(Guid userId, Guid versionId, int file, int part)
    {
        var path = _binaries.ObjectPath(userId, ManagedBinaryStore.NoteParts, versionId, PartKey(file, part));
        return File.Exists(path) ? path : LegacyPartFile(userId, versionId, file, part);
    }
    private static string Key(Guid id) { if (id == Guid.Empty) throw Invalid("标识无效"); return id.ToString("D"); }
    private Task<BeginNoteMaterialUploadRequest> RequestAsync(Guid userId, Guid versionId, CancellationToken ct) =>
        ReadAsync<BeginNoteMaterialUploadRequest>(Path.Combine(VersionFolder(userId, versionId), "request.json"), ct);
    private async Task<Ledger> LoadLedgerAsync(Guid userId, CancellationToken ct)
    {
        var path = Path.Combine(UserFolder(userId), "ledger.json");
        var ledger = File.Exists(path) ? await ReadAsync<Ledger>(path, ct) : new Ledger([], [], []);
        if (ledger.SchemaVersion != 1) throw new InvalidDataException("Unsupported note storage metadata");
        await PurgeAsync(userId, ledger, ct);
        return ledger;
    }
    private Task SaveLedgerAsync(Guid userId, Ledger value, CancellationToken ct) => WriteAsync(Path.Combine(UserFolder(userId), "ledger.json"), value, ct);
    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct) ?? throw new InvalidDataException("Invalid note storage metadata");
    }
    private static Task WriteAsync<T>(string path, T value, CancellationToken ct) =>
        WriteFileAsync(path, stream => JsonSerializer.SerializeAsync(stream, value, Json, ct), ct);
    private static async Task WriteFileAsync(string path, Func<FileStream, Task> write, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            { await write(stream); await stream.FlushAsync(ct); stream.Flush(flushToDisk: true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void DeleteFolder(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    private static StoredVersion Find(Ledger ledger, Guid id) => ledger.Versions.TryGetValue(Key(id), out var entry)
        ? entry : throw new DomainException(40401, "材料版本不存在或已过期");
    private static Guid? Current(Ledger ledger, Guid noteId) => ledger.Heads.TryGetValue(Key(noteId), out var id) ? id : null;
    private static NoteUploadedMaterial FileAt(IReadOnlyList<NoteUploadedMaterial> files, int index) => index >= 0 && index < files.Count
        ? files[index] : throw new DomainException(40401, "材料文件不存在");
    private static int PartCount(NoteUploadedMaterial file) => checked((int)((file.SizeBytes + NoteMaterialUploadOptions.PartSizeBytes - 1) / NoteMaterialUploadOptions.PartSizeBytes));
    private static long PartLength(NoteUploadedMaterial file, int index) => Math.Min(NoteMaterialUploadOptions.PartSizeBytes,
        file.SizeBytes - (long)index * NoteMaterialUploadOptions.PartSizeBytes);
    private static async Task<string> HashAsync(Stream stream, CancellationToken ct) => Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Conflict(string message) => new(40901, message);
    private sealed record Ledger(Dictionary<string, StoredVersion> Versions, Dictionary<string, Guid> Heads,
        HashSet<string> Deletions, int SchemaVersion = 1);
    private sealed record StoredVersion(Guid NoteId, Guid? BaseVersionId, string Fingerprint, long SizeBytes,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Phase, bool HasConflict = false);
}
