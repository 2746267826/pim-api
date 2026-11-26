using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
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
    private readonly NoteAudioDecoder _audioDecoder = new(options.Value);
    // Fixed stripes keep the process-local gate set bounded; a collision only serializes two
    // unrelated decodes, which is safe because the decoder already has a process-wide cap.
    private const int AudioDecodeGateSlots = 64;
    private readonly SemaphoreSlim[] _audioDecodeGates = Enumerable.Range(0, AudioDecodeGateSlots)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly BoundedLruCache<string, string> _verifiedFiles =
        new(Math.Max(1, options.Value.VerifiedFileCacheMaxEntries), StringComparer.Ordinal);
    private readonly object _audioPruneLock = new();
    private DateTimeOffset _nextAudioPruneAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan AudioPruneInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AudioWorkMaxAge = TimeSpan.FromHours(1);

    public NoteMaterialUploadLimitsDto Limits => new(NoteMaterialUploadOptions.PartSizeBytes, NoteMaterialUploadOptions.MaxMetadataCharacters,
        _options.MaxVersionBytes, _options.MaxUserReservedBytes, _options.MaxFiles,
        _options.MaxPendingVersions, _options.MaxVersionsPerUser, _options.UploadExpiryHours);
    public static IReadOnlyList<string> DownloadContentTypes { get; } =
    [
        "audio/wav", "audio/ogg", "image/png", "image/jpeg", "image/webp",
        "application/json", "text/markdown; charset=utf-8", "application/octet-stream"
    ];
    public static string ContentType(string path) => NoteAudioFormats.ContentType(path);

    internal int VerifiedFileCacheCount => _verifiedFiles.Count;
    internal int AudioDecodeGateSlotCount => _audioDecodeGates.Length;

    public async Task<NoteMaterialUploadDto> BeginAsync(Guid userId, BeginNoteMaterialUploadRequest request, CancellationToken ct = default)
    {
        var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
        var requestJson = JsonSerializer.Serialize(request, Json);
        var fingerprint = NoteMaterialManifestValidator.Hash(requestJson);
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireActive(ledger, request.NoteId, "创建材料版本");
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

    public Task<NoteMaterialVersionDto> GetAsync(Guid userId, Guid versionId,
        CancellationToken ct = default) =>
        GetCoreAsync(userId, versionId, allowRecycled: false, ct);

    /// <summary>MCP reads may inspect a recycled note; REST material reads must not.</summary>
    public Task<NoteMaterialVersionDto> GetForMcpAsync(Guid userId, Guid versionId,
        CancellationToken ct = default) =>
        GetCoreAsync(userId, versionId, allowRecycled: true, ct);

    private async Task<NoteMaterialVersionDto> GetCoreAsync(Guid userId, Guid versionId,
        bool allowRecycled, CancellationToken ct)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var entry = Find(ledger, versionId);
        if (!allowRecycled) RequireActive(ledger, entry.NoteId, "读取材料版本");
        var request = await RequestAsync(userId, versionId, ct);
        var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
        return new(await DescribeAsync(userId, versionId, entry, ledger, files, ct), request.InputJson, request.ManifestJson);
    }

    public async Task<NativeNotePageDto> ListNativeNotesAsync(Guid userId, int offset, int limit, CancellationToken ct = default)
    {
        if (offset < 0 || offset > _options.MaxVersionsPerUser || limit is < 1 or > 50)
            throw Invalid("笔记分页范围无效，单页最多50篇且起始位置不得超过账号版本上限");
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var conflicts = ledger.Versions.Values.Where(item => item.Phase == "Conflict" || item.HasConflict)
            .Select(item => item.NoteId).ToHashSet();
        var candidates = ledger.Heads.Select(head =>
            (Head: head, Parsed: Guid.TryParseExact(head.Key, "D", out var noteId) ? noteId : Guid.Empty))
            .Where(item => item.Parsed != Guid.Empty &&
                ledger.Versions.TryGetValue(Key(item.Head.Value), out var version) &&
                version.NoteId == item.Parsed && version.Phase == "Uploaded" &&
                !IsRecycled(ledger, item.Parsed))
            .Select(item => (NoteId: item.Parsed, VersionId: item.Head.Value, Entry: ledger.Versions[Key(item.Head.Value)],
                HasConflict: conflicts.Contains(item.Parsed)))
            .OrderByDescending(item => item.Entry.CreatedAt).ThenBy(item => item.NoteId).ToArray();
        var page = new List<NativeNoteSummaryDto>();
        foreach (var item in candidates.Skip(offset).Take(limit))
        {
            var request = await RequestAsync(userId, item.VersionId, ct);
            _ = NoteMaterialManifestValidator.Validate(request, ReadLimits);
            page.Add(new(item.NoteId, ReadTitle(request.InputJson), item.VersionId, item.HasConflict));
        }
        return new(candidates.Length, offset, page);
    }

    internal async Task<IReadOnlyList<NativeNoteSearchSource>> ListSearchSourcesAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var result = new List<NativeNoteSearchSource>();
        foreach (var head in ledger.Heads)
        {
            if (!Guid.TryParseExact(head.Key, "D", out var noteId)) continue;
            if (IsRecycled(ledger, noteId)) continue;
            if (!ledger.Versions.TryGetValue(Key(head.Value), out var version) ||
                version.NoteId != noteId || version.Phase != "Uploaded")
                continue;
            var request = await RequestAsync(userId, head.Value, ct);
            result.Add(new NativeNoteSearchSource(noteId, head.Value, request.InputJson,
                version.CreatedAt, version.HasConflict));
        }
        return result;
    }

    public async Task<NoteMaterialVersionDto> GetCurrentAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireActive(ledger, noteId, "读取当前材料版本");
        var versionId = Current(ledger, noteId) ?? throw new DomainException(40401, "当前完整笔记不存在");
        var entry = Find(ledger, versionId);
        if (entry.NoteId != noteId || entry.Phase != "Uploaded")
            throw new DomainException(40401, "当前完整笔记不存在");
        var request = await RequestAsync(userId, versionId, ct);
        var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
        _ = ReadTitle(request.InputJson);
        return new(await DescribeAsync(userId, versionId, entry, ledger, files, ct), request.InputJson, request.ManifestJson);
    }

    public async Task<NoteMaterialUploadDto> CloneAsync(Guid userId, Guid sourceVersionId,
        CloneNoteMaterialVersionRequest clone, CancellationToken ct = default)
    {
        var request = clone.Material;
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireActive(ledger, request.NoteId, "复制材料版本");
        var sourceEntry = Find(ledger, sourceVersionId);
        if (sourceEntry.NoteId != request.NoteId) throw new DomainException(40401, "材料版本不存在或已过期");
        var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
        if (sourceVersionId == request.VersionId) throw Invalid("新材料版本必须使用新的版本标识");
        var fingerprint = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, Json));
        if (sourceEntry.NoteId != request.NoteId || sourceEntry.Phase != "Uploaded" ||
            Current(ledger, request.NoteId) != sourceVersionId || clone.ExpectedCurrentVersionId != sourceVersionId ||
            request.BaseVersionId != sourceVersionId)
            throw Conflict("当前材料版本已变化或来源不是当前完整版本，请重新读取后再编辑");
        var source = await RequestAsync(userId, sourceVersionId, ct);
        var sourceFiles = NoteMaterialManifestValidator.Validate(source, ReadLimits);
        var sourceByPath = sourceFiles.Select((file, index) => (File: file, Index: index))
            .ToDictionary(item => item.File.Path, StringComparer.Ordinal);
        foreach (var sourceFile in sourceFiles.Where(file => file.Kind is "AudioChunk" or "RecordingManifest" or "Photo" or "InkPreview"))
        {
            if (!files.Any(file => file.Path == sourceFile.Path && file.Kind == sourceFile.Kind &&
                    file.Sha256 == sourceFile.Sha256 && file.SizeBytes == sourceFile.SizeBytes &&
                    file.RecordingId == sourceFile.RecordingId && file.Segment == sourceFile.Segment &&
                    file.ChunkIndex == sourceFile.ChunkIndex && file.Samples == sourceFile.Samples &&
                    file.BlockId == sourceFile.BlockId && file.AudioFormat == sourceFile.AudioFormat))
                throw Invalid("续写材料必须完整保留原版本的音频、照片与手写预览；请从当前版本重新载入原稿");
        }
        var key = Key(request.VersionId);
        if (!ledger.Versions.TryGetValue(key, out var targetEntry))
        {
            var total = files.Sum(file => file.SizeBytes);
            var reserved = checked(total + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, Json)) + 1024L);
            if (total > _options.MaxVersionBytes || files.Count > _options.MaxFiles ||
                ledger.Versions.Count >= _options.MaxVersionsPerUser ||
                ledger.Versions.Values.Count(item => item.Phase == "Uploading") >= _options.MaxPendingVersions ||
                reserved > _options.MaxUserReservedBytes - ledger.Versions.Values.Sum(item => item.SizeBytes))
                throw new DomainException(41301, "服务器笔记存储配额或待上传数量已达上限；本机原稿保留");
            var now = _clock.GetUtcNow();
            targetEntry = new StoredVersion(request.NoteId, request.BaseVersionId, fingerprint, reserved, now,
                now.AddHours(_options.UploadExpiryHours), "Uploading");
            Directory.CreateDirectory(VersionFolder(userId, request.VersionId));
            await WriteAsync(Path.Combine(VersionFolder(userId, request.VersionId), "request.json"), request, ct);
            ledger.Versions.Add(key, targetEntry);
            await SaveLedgerAsync(userId, ledger, ct);
        }
        else if (targetEntry.Fingerprint != fingerprint)
            throw Conflict("同一材料版本已经绑定到另一份固定输入，原版本保留");

        for (var targetIndex = 0; targetIndex < files.Count; targetIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var targetFile = files[targetIndex];
            if (!sourceByPath.TryGetValue(targetFile.Path, out var sourceFile) ||
                sourceFile.File.Kind != targetFile.Kind || sourceFile.File.Sha256 != targetFile.Sha256 ||
                sourceFile.File.SizeBytes != targetFile.SizeBytes || sourceFile.File.RecordingId != targetFile.RecordingId ||
                sourceFile.File.Segment != targetFile.Segment || sourceFile.File.ChunkIndex != targetFile.ChunkIndex ||
                sourceFile.File.Samples != targetFile.Samples || sourceFile.File.BlockId != targetFile.BlockId ||
                sourceFile.File.AudioFormat != targetFile.AudioFormat)
                continue;
            await VerifyFileAsync(userId, sourceVersionId, sourceFile.Index, sourceFile.File, ct);
            for (var partIndex = 0; partIndex < PartCount(targetFile); partIndex++)
            {
                await using var part = File.OpenRead(PartFile(userId, sourceVersionId, sourceFile.Index, partIndex));
                var expectedLength = PartLength(targetFile, partIndex);
                if (part.Length != expectedLength) throw Conflict("来源材料分块缺失或损坏，未能保全原稿");
                var partHash = await HashAsync(part, ct);
                var targetPath = PartFile(userId, request.VersionId, targetIndex, partIndex);
                if (File.Exists(targetPath))
                {
                    await using var existingPart = File.OpenRead(targetPath);
                    if (existingPart.Length != expectedLength || await HashAsync(existingPart, ct) != partHash)
                        throw Conflict("续写版本中已保存的材料分块与来源不一致，不能覆盖");
                    continue;
                }
                part.Position = 0;
                await _binaries.PutAsync(userId, ManagedBinaryStore.NoteParts, request.VersionId, part,
                    expectedLength, ct, PartKey(targetIndex, partIndex), partHash);
            }
        }
        return await DescribeAsync(userId, request.VersionId, targetEntry, ledger, files, ct);
    }

    public async Task WriteBundleAsync(Guid userId, Guid versionId, Stream output, CancellationToken ct = default)
    {
        var version = await GetAsync(userId, versionId, ct);
        if (version.Upload.Phase is not ("Uploaded" or "Conflict"))
            throw Conflict("未完成的材料版本不能导出");

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        await WriteMetadataAsync(archive, "material-version.json", version.InputJson, ct);
        await WriteMetadataAsync(archive, "materials.json", version.ManifestJson, ct);
        foreach (var file in version.Upload.Files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await ReadFileAsync(userId, versionId, file.Index, entryStream, ct);
        }
    }

    private static async Task WriteMetadataAsync(ZipArchive archive, string path, string value,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(bytes, ct);
    }

    public async Task<IReadOnlyList<NoteMaterialUploadDto>> ListAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireActive(ledger, noteId, "列出材料版本");
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
        var versions = ledger.Versions
            .Where(x => x.Value.Phase != "Uploading" && !IsRecycled(ledger, x.Value.NoteId))
            .OrderByDescending(x => x.Value.CreatedAt)
            .ToArray();
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
        RequireActive(ledger, entry.NoteId, "上传材料分块");
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

    public Task<NoteMaterialUploadDto> CompleteAsync(Guid userId, Guid versionId,
        CancellationToken ct = default)
        => CompleteAsync(userId, versionId, makeCurrent: true, ct);

    /// <summary>
    /// Verifies and publishes a version. <paramref name="makeCurrent"/> lets the MCP package
    /// upload keep a verified version off the head (promote=false) without inventing a conflict.
    /// A real concurrent head change is still recorded as a conflict candidate.
    /// </summary>
    public async Task<NoteMaterialUploadDto> CompleteAsync(Guid userId, Guid versionId,
        bool makeCurrent, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var entry = Find(ledger, versionId);
        RequireActive(ledger, entry.NoteId, "完成材料上传");
        var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct), ReadLimits);
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            await VerifyFileAsync(userId, versionId, fileIndex, files[fileIndex], ct);
        if (entry.Phase == "Uploading")
        {
            var current = Current(ledger, entry.NoteId);
            var conflict = current != entry.BaseVersionId;
            entry = entry with { Phase = conflict ? "Conflict" : "Uploaded", HasConflict = conflict };
            ledger.Versions[Key(versionId)] = entry;
            if (!conflict && makeCurrent) ledger.Heads[Key(entry.NoteId)] = versionId;
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
        RequireActive(ledger, entry.NoteId, "选择当前材料版本");
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
        var entry = Find(ledger, versionId);
        RequireActive(ledger, entry.NoteId, "读取材料文件");
        if (entry.Phase == "Uploading") throw Conflict("材料尚未提交完整");
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
        long offsetSamples, long samples, CancellationToken ct, long? maxSamples = null)
    {
        // Callers pass their persisted task budget (multimodal routes may use up to ten minutes).
        // Omitting it keeps the legacy one-minute bound instead of widening it implicitly.
        var budget = maxSamples ?? NoteAsrPlan.SamplesPerCall;
        byte[]? wav = null;
        NoteUploadedMaterial? opus = null;
        string? cachePath = null;
        if (budget is <= 0 or > NoteAsrPlan.MaxSamplesPerSlice)
            throw Invalid("录音分片预算超出单次读取上限");
        await using (var lease = await LockAsync(owner, ct))
        {
            var ledger = await LoadLedgerAsync(owner, ct);
            var entry = Find(ledger, versionId);
            RequireActive(ledger, entry.NoteId, "读取录音分片");
            if (entry.Phase == "Uploading") throw Conflict("材料尚未提交完整");
            var file = FileAt(NoteMaterialManifestValidator.Validate(
                await RequestAsync(owner, versionId, ct), ReadLimits), fileIndex);
            if (file.Kind != "AudioChunk" || offsetSamples < 0 ||
                samples <= 0 || samples > budget ||
                file.Samples is not { } sourceSamples || offsetSamples > sourceSamples ||
                samples > sourceSamples - offsetSamples)
                throw Invalid("录音分片范围无效");
            await VerifyFileAsync(owner, versionId, fileIndex, file, ct);
            if ((file.AudioFormat ?? NoteAudioFormat.Wav) == NoteAudioFormat.Wav)
                wav = await ReadWavSliceAsync(owner, versionId, fileIndex, offsetSamples, samples, ct);
            else
            {
                opus = file;
                cachePath = AudioCachePath(file);
            }
        }
        if (wav is not null) return wav;
        // A concurrent decode may prune an older cache entry while this request is between the
        // decode-cache check and opening the read handle. Re-decode once instead of surfacing a
        // filesystem race; the cache budget keeps the second pass bounded.
        for (var attempt = 0; ; attempt++)
        {
            await EnsureOpusDecodedAsync(owner, versionId, fileIndex, opus!, cachePath!, ct);
            try
            {
                return await ReadPcmSliceAsync(cachePath!, offsetSamples, samples, ct);
            }
            catch (Exception error) when (attempt == 0 &&
                error is FileNotFoundException or DirectoryNotFoundException)
            {
                // Retry the ensure/read pair once.
            }
        }
    }

    internal async Task VerifyAudioFileAsync(Guid owner, Guid versionId, int fileIndex,
        CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadLedgerAsync(owner, ct);
        var entry = Find(ledger, versionId);
        RequireActive(ledger, entry.NoteId, "读取录音分片");
        if (entry.Phase == "Uploading") throw Conflict("材料尚未提交完整");
        var file = FileAt(NoteMaterialManifestValidator.Validate(
            await RequestAsync(owner, versionId, ct), ReadLimits), fileIndex);
        if (file.Kind != "AudioChunk") throw Invalid("录音材料索引无效");
        await VerifyFileAsync(owner, versionId, fileIndex, file, ct);
    }

    internal async Task<byte[]> ReadImageBytesAsync(Guid owner, Guid versionId, int fileIndex, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var ledger = await LoadLedgerAsync(owner, ct);
        var entry = Find(ledger, versionId);
        RequireActive(ledger, entry.NoteId, "读取图片材料");
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
        RequireActive(ledger, entry.NoteId, "取消未完成上传");
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
        var entry = Find(ledger, versionId);
        RequireActive(ledger, entry.NoteId, "清除材料分块");
        if (entry.Phase != "Uploading") throw Conflict("完整材料版本已经固定，不能删除分块");
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
        var keys = ledger.Versions.Where(item => item.Value.NoteId == noteId)
            .Select(item => item.Key).ToArray();
        foreach (var key in keys)
        {
            ledger.Versions.Remove(key);
            ledger.Deletions.Add(key);
            await DeleteAudioCacheAsync(userId, key, ct);
        }
        ledger.Heads.Remove(Key(noteId));
        ledger.Notes.Remove(Key(noteId));
        await SaveLedgerAsync(userId, ledger, ct);
        await PurgeAsync(userId, ledger, ct);
    }

    /// <summary>
    /// PCM cache entries are derived, content-addressed data, so dropping them cannot lose source
    /// audio; a later read simply decodes again. Only this note's own audio keys are removed.
    /// </summary>
    private async Task DeleteAudioCacheAsync(Guid userId, string versionKey, CancellationToken ct)
    {
        try
        {
            var versionId = Guid.ParseExact(versionKey, "D");
            var request = await RequestAsync(userId, versionId, ct);
            var files = NoteMaterialManifestValidator.Validate(request, ReadLimits);
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (file.Kind != "AudioChunk" ||
                    (file.AudioFormat ?? NoteAudioFormat.Wav) != NoteAudioFormat.Opus) continue;
                TryDelete(AudioCachePath(file));
                _verifiedFiles.RemoveWhere(key => key == VerifyKey(userId, versionId, index));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            JsonException or DomainException)
        {
            // The version may already be partially gone; its cache stays bounded by TTL/byte quota.
        }
    }

    /// <summary>
    /// Owner-scoped note list for the MCP aggregate read. Existing REST callers keep using
    /// <see cref="ListNativeNotesAsync"/>; this overload adds recycle-bin status filtering and a
    /// deterministic per-note revision used for optimistic concurrency checks.
    /// </summary>
    public async Task<NativeNoteMcpPageDto> ListMcpNotesAsync(Guid userId, string? status, int offset,
        int limit, CancellationToken ct = default)
    {
        var normalizedStatus = NormalizeStatus(status);
        if (offset < 0 || offset > _options.MaxVersionsPerUser || limit is < 1 or > 50)
            throw Invalid("笔记分页范围无效，单页最多50篇且起始位置不得超过账号版本上限");
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);

        // A note can be visible through versions without a head (for example an explicit
        // promote=false package upload or a restored conflict candidate). Union both sources so
        // every owner-visible material version is discoverable by the MCP list tool.
        var noteIds = ledger.Versions.Values
            .Select(entry => entry.NoteId)
            .Concat(ledger.Notes.Keys
                .Select(key => Guid.TryParseExact(key, "D", out var id) ? id : Guid.Empty))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var items = new List<NativeNoteMcpNoteSummaryDto>();
        foreach (var noteId in noteIds)
        {
            var noteStatus = NoteStatus(ledger, noteId);
            if (!StatusMatches(noteStatus, normalizedStatus))
                continue;
            var versions = ledger.Versions
                .Where(item => item.Value.NoteId == noteId)
                .OrderByDescending(item => item.Value.CreatedAt)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            if (versions.Length == 0)
                continue;
            var headVersionId = Current(ledger, noteId);
            if (headVersionId is { } candidate &&
                (!ledger.Versions.TryGetValue(Key(candidate), out var headEntry) ||
                 headEntry.NoteId != noteId))
                headVersionId = null;
            var titleEntry = headVersionId is { } headId
                ? versions.First(item => item.Key == Key(headId))
                : versions.FirstOrDefault(item => item.Value.Phase == "Uploaded");
            if (string.IsNullOrEmpty(titleEntry.Key))
                titleEntry = versions[0];
            var title = ReadTitle((await RequestAsync(userId,
                Guid.ParseExact(titleEntry.Key, "D"), ct)).InputJson);
            var conflicts = versions.Count(item =>
                item.Value.HasConflict || item.Value.Phase == "Conflict");
            items.Add(new NativeNoteMcpNoteSummaryDto(
                noteId,
                title,
                headVersionId,
                RevisionOf(ledger, noteId),
                noteStatus,
                conflicts > 0,
                versions.Length,
                conflicts,
                UpdatedAtOf(ledger, noteId)));
        }

        var ordered = items
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.NoteId)
            .ToArray();
        return new NativeNoteMcpPageDto(ordered.Length, offset, limit,
            ordered.Skip(offset).Take(limit).ToArray());
    }

    /// <summary>Owner-scoped aggregate material snapshot (current version + conflict candidates).</summary>
    public async Task<NativeNoteMcpSnapshotDto> GetMcpSnapshotAsync(Guid userId, Guid noteId,
        int versionLimit, CancellationToken ct = default)
    {
        if (versionLimit is < 1 or > 100) throw Invalid("版本分页范围无效，单页最多100个版本");
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        var entries = ledger.Versions
            .Where(item => item.Value.NoteId == noteId)
            .OrderByDescending(item => item.Value.CreatedAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0) throw new DomainException(40401, "笔记不存在");

        var currentVersionId = Current(ledger, noteId);
        if (currentVersionId is { } currentId &&
            (!ledger.Versions.TryGetValue(Key(currentId), out var currentEntry) ||
             currentEntry.NoteId != noteId))
            currentVersionId = null;

        KeyValuePair<string, StoredVersion> titleEntry;
        if (currentVersionId is { } currentForTitle)
        {
            titleEntry = entries.First(item => item.Key == Key(currentForTitle));
        }
        else
        {
            titleEntry = entries.FirstOrDefault(item => item.Value.Phase == "Uploaded");
            if (string.IsNullOrEmpty(titleEntry.Key)) titleEntry = entries[0];
        }
        var titleVersionId = Guid.ParseExact(titleEntry.Key, "D");
        var titleRequest = await RequestAsync(userId, titleVersionId, ct);
        var title = ReadTitle(titleRequest.InputJson);
        var versionEntries = entries.Take(versionLimit).ToList();
        if (currentVersionId is { } currentVersion)
        {
            var selectedCurrentEntry = entries.First(item => item.Key == Key(currentVersion));
            if (versionEntries.All(item => item.Key != selectedCurrentEntry.Key))
            {
                if (versionEntries.Count == versionLimit)
                    versionEntries.RemoveAt(versionEntries.Count - 1);
                versionEntries.Add(selectedCurrentEntry);
                versionEntries.Sort((left, right) =>
                {
                    var created = right.Value.CreatedAt.CompareTo(left.Value.CreatedAt);
                    return created != 0 ? created : string.CompareOrdinal(left.Key, right.Key);
                });
            }
        }
        var allConflictEntries = entries
            .Where(item => item.Value.HasConflict || item.Value.Phase == "Conflict")
            .ToArray();
        var conflictEntries = allConflictEntries.Take(100).ToArray();
        var versions = await DescribeMcpVersionsAsync(userId, versionEntries, ledger, ct);
        var conflicts = await DescribeMcpVersionsAsync(userId, conflictEntries, ledger, ct);
        return new NativeNoteMcpSnapshotDto(
            noteId,
            title,
            NoteStatus(ledger, noteId),
            RevisionOf(ledger, noteId),
            currentVersionId,
            allConflictEntries.Length > 0,
            entries.Length,
            allConflictEntries.Length,
            UpdatedAtOf(ledger, noteId),
            versions,
            conflicts);
    }

    /// <summary>Current recycle-bin status with an existence check (404 when the note is unknown).</summary>
    public async Task<string> GetMcpNoteStatusAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireNote(ledger, noteId);
        return NoteStatus(ledger, noteId);
    }

    /// <summary>
    /// Moves a note to the recycle bin. Versions and files stay on disk so a restore is lossless;
    /// only the note status changes. Native notes have no separate archive state: folders are the
    /// normal archive location and the recycle bin is the deleted state (R05 A05/A07 rules).
    /// The optional expected revision is a strict CAS.
    /// </summary>
    public async Task<NativeNoteMcpRecycleDto> RecycleAsync(Guid userId, Guid noteId,
        Guid? expectedRevision, CancellationToken ct = default)
        => await SetRecycledAsync(userId, noteId, recycled: true, expectedRevision, ct);

    /// <summary>Restores a recycled note without resuming any processing task.</summary>
    public async Task<NativeNoteMcpRecycleDto> RestoreAsync(Guid userId, Guid noteId,
        Guid? expectedRevision, CancellationToken ct = default)
        => await SetRecycledAsync(userId, noteId, recycled: false, expectedRevision, ct);

    public async Task<IReadOnlyList<Guid>> ListNoteVersionIdsAsync(Guid userId, Guid noteId,
        CancellationToken ct = default)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireNote(ledger, noteId);
        return ledger.Versions
            .Where(item => item.Value.NoteId == noteId)
            .Select(item => Guid.ParseExact(item.Key, "D"))
            .ToArray();
    }

    private async Task<NativeNoteMcpRecycleDto> SetRecycledAsync(Guid userId, Guid noteId,
        bool recycled, Guid? expectedRevision, CancellationToken ct)
    {
        await using var lease = await LockAsync(userId, ct);
        var ledger = await LoadLedgerAsync(userId, ct);
        await PruneAsync(userId, ledger, ct);
        RequireNote(ledger, noteId);
        var currentStatus = NoteStatus(ledger, noteId);
        var revision = RevisionOf(ledger, noteId);
        if (expectedRevision is { } expected && expected != revision)
            throw Conflict("笔记版本已变化，请先重新读取 get_native_note_versions 后再试");
        var targetStatus = recycled ? "Recycled" : "Active";
        if (currentStatus == targetStatus)
            return new NativeNoteMcpRecycleDto(currentStatus, revision, UpdatedAtOf(ledger, noteId));

        var now = _clock.GetUtcNow();
        var state = ledger.Notes.TryGetValue(Key(noteId), out var existing)
            ? existing
            : new StoredNoteState(currentStatus, now);
        var updated = state with
        {
            Status = targetStatus,
            UpdatedAt = now,
            RecycledAt = recycled ? now : null,
        };
        ledger.Notes[Key(noteId)] = updated;
        await SaveLedgerAsync(userId, ledger, ct);
        return new NativeNoteMcpRecycleDto(targetStatus, RevisionOf(ledger, noteId), now);
    }

    private async Task<IReadOnlyList<NoteMaterialUploadDto>> DescribeMcpVersionsAsync(Guid userId,
        IEnumerable<KeyValuePair<string, StoredVersion>> entries, Ledger ledger, CancellationToken ct)
    {
        var result = new List<NoteMaterialUploadDto>();
        foreach (var (key, entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var versionId = Guid.ParseExact(key, "D");
            var files = NoteMaterialManifestValidator.Validate(await RequestAsync(userId, versionId, ct),
                ReadLimits);
            result.Add(await DescribeAsync(userId, versionId, entry, ledger, files, ct));
        }
        return result;
    }

    private static string NormalizeStatus(string? status)
    {
        var value = string.IsNullOrWhiteSpace(status) ? "Active" : status.Trim();
        if (value.Equals("Active", StringComparison.OrdinalIgnoreCase)) return "Active";
        if (value.Equals("Recycled", StringComparison.OrdinalIgnoreCase)) return "Recycled";
        if (value.Equals("All", StringComparison.OrdinalIgnoreCase)) return "All";
        throw Invalid("status 必须是 Active、Recycled 或 All");
    }

    private static bool StatusMatches(string status, string filter)
        => filter == "All" || string.Equals(status, filter, StringComparison.Ordinal);

    private static string NoteStatus(Ledger ledger, Guid noteId)
        => ledger.Notes.TryGetValue(Key(noteId), out var state) ? state.Status : "Active";

    private static DateTimeOffset UpdatedAtOf(Ledger ledger, Guid noteId)
    {
        var latest = ledger.Versions
            .Where(item => item.Value.NoteId == noteId)
            .Select(item => item.Value.CreatedAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (ledger.Notes.TryGetValue(Key(noteId), out var state) && state.UpdatedAt > latest)
            latest = state.UpdatedAt;
        return latest;
    }

    private static Guid RevisionOf(Ledger ledger, Guid noteId)
    {
        var builder = new StringBuilder(256);
        builder.Append(Key(noteId)).Append('|').Append(NoteStatus(ledger, noteId))
            .Append("|head:").Append(Current(ledger, noteId)?.ToString("D") ?? "-");
        foreach (var (key, entry) in ledger.Versions
                     .Where(item => item.Value.NoteId == noteId)
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Append('|').Append(key).Append(':').Append(entry.Phase).Append(':')
                .Append(entry.HasConflict ? '1' : '0').Append(':')
                .Append(entry.CreatedAt.UtcTicks);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static void RequireNote(Ledger ledger, Guid noteId)
    {
        if (!ledger.Versions.Values.Any(item => item.NoteId == noteId) &&
            !ledger.Notes.ContainsKey(Key(noteId)))
            throw new DomainException(40401, "笔记不存在");
    }

    private static bool IsRecycled(Ledger ledger, Guid noteId)
        => ledger.Notes.TryGetValue(Key(noteId), out var state) &&
           string.Equals(state.Status, "Recycled", StringComparison.Ordinal);

    private static void RequireActive(Ledger ledger, Guid noteId, string action)
    {
        if (IsRecycled(ledger, noteId))
            throw new DomainException(40901, $"笔记在回收站，请先恢复后再{action}");
    }

    private async Task VerifyFileAsync(Guid userId, Guid versionId, int index, NoteUploadedMaterial file, CancellationToken ct)
    {
        var cacheKey = VerifyKey(userId, versionId, index);
        var signature = FileSignature(userId, versionId, index, file);
        if (_verifiedFiles.TryGet(cacheKey, out var verified) && verified == signature) return;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var header = new byte[44];
        var headerLength = 0;
        NoteOggOpus.Validator? opus = null;
        long? audioSamples = null;
        if (file.Kind == "AudioChunk")
        {
            if (file.Samples is not { } samples || samples < 0)
                throw Invalid("录音格式或样本数不符合本次固定材料");
            audioSamples = samples;
            if ((file.AudioFormat ?? NoteAudioFormat.Wav) == NoteAudioFormat.Opus)
                opus = new NoteOggOpus.Validator();
        }
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
                opus?.Write(buffer.AsSpan(0, read));
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
        if (file.Kind == "AudioChunk")
        {
            var format = file.AudioFormat ?? NoteAudioFormat.Wav;
            var samples = audioSamples!.Value;
            if (format == NoteAudioFormat.Wav)
            {
                if (file.SizeBytes < 44 || headerLength < 44 ||
                    !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                    !header.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != file.SizeBytes - 8 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != 16 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) != NoteAsrPlan.SampleRate ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)) != NoteAsrPlan.SampleRate * 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 16 ||
                    !header.AsSpan(36, 4).SequenceEqual("data"u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)) != samples * 2)
                    throw Invalid("录音格式或样本数不符合本次固定材料");
            }
            else
            {
                opus!.Complete(samples);
            }
        }
        _verifiedFiles.Set(cacheKey, signature);
    }

    private async Task<byte[]> ReadWavSliceAsync(Guid owner, Guid versionId, int fileIndex,
        long offsetSamples, long samples, CancellationToken ct)
    {
        var bytes = new byte[checked(44 + (int)samples * 2)];
        NoteAudioFormats.WriteWavHeader(bytes, checked((int)samples * 2));
        long position = 44 + offsetSamples * 2;
        var written = 44;
        while (written < bytes.Length)
        {
            var part = (int)(position / NoteMaterialUploadOptions.PartSizeBytes);
            var within = position % NoteMaterialUploadOptions.PartSizeBytes;
            await using var stream = File.OpenRead(PartFile(owner, versionId, fileIndex, part));
            stream.Position = within;
            var count = (int)Math.Min(bytes.Length - written, stream.Length - within);
            if (count <= 0) throw Invalid("录音材料分块损坏");
            await stream.ReadExactlyAsync(bytes.AsMemory(written, count), ct);
            written += count;
            position += count;
        }
        return bytes;
    }

    private async Task EnsureOpusDecodedAsync(Guid owner, Guid versionId, int fileIndex,
        NoteUploadedMaterial file, string cachePath, CancellationToken ct)
    {
        var expectedBytes = checked(file.Samples!.Value * 2);
        var gate = AudioDecodeGate(
            file.Sha256 + ":" + file.Samples.Value.ToString(CultureInfo.InvariantCulture));
        await gate.WaitAsync(ct);
        try
        {
            if (IsValidPcmCache(cachePath, expectedBytes)) return;
            TryDelete(cachePath);
            string? source = null;
            string? pcmTemp = null;
            try
            {
                // The account lock protects the immutable part files while they are copied once to
                // the private work folder. Decoding and the expensive hash verification happen
                // outside that per-user transaction.
                await using (var lease = await LockAsync(owner, ct))
                    source = await WriteAudioWorkFileAsync(owner, versionId, fileIndex, file, ct);
                pcmTemp = Path.Combine(AudioWorkFolder,
                    Guid.NewGuid().ToString("N") + "-" + file.Sha256 + ".pcm.tmp");
                await using (var output = OpenPrivateWrite(pcmTemp))
                {
                    await _audioDecoder.DecodeOpusFileAsync(source, output, file.Samples.Value, ct);
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                }
                if (new FileInfo(pcmTemp).Length != expectedBytes)
                    throw Invalid("Ogg Opus 录音解码后的样本数与固定材料不一致，未发送至转写服务");
                EnsurePrivateDirectory(AudioCacheFolder);
                // Content-addressed entries are identical by construction, so replacing a file
                // created by another process is safe and avoids a non-deterministic IOException.
                File.Move(pcmTemp, cachePath, overwrite: true);
                if (!IsValidPcmCache(cachePath, expectedBytes))
                    throw Invalid("Ogg Opus 解码缓存写入失败，未发送至转写服务");
                SetPrivateFile(cachePath);
            }
            finally
            {
                TryDelete(source);
                TryDelete(pcmTemp);
            }
            PruneAudioStorage(force: true, keepPath: cachePath);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> WriteAudioWorkFileAsync(Guid owner, Guid versionId, int fileIndex,
        NoteUploadedMaterial file, CancellationToken ct)
    {
        EnsurePrivateDirectory(AudioWorkFolder);
        var path = Path.Combine(AudioWorkFolder,
            $"pim-audio-{Guid.NewGuid():N}{NoteAudioFormats.Extension(NoteAudioFormat.Opus)}");
        try
        {
            await using (var output = OpenPrivateWrite(path))
            {
                for (var part = 0; part < PartCount(file); part++)
                {
                    await using var source = File.OpenRead(PartFile(owner, versionId, fileIndex, part));
                    await source.CopyToAsync(output, ct);
                }
            }
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static async Task<byte[]> ReadPcmSliceAsync(string cachePath, long offsetSamples,
        long samples, CancellationToken ct)
    {
        var pcmBytes = checked((int)samples * 2);
        var bytes = new byte[checked(44 + pcmBytes)];
        NoteAudioFormats.WriteWavHeader(bytes, pcmBytes);
        await using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = checked(offsetSamples * 2);
        await stream.ReadExactlyAsync(bytes.AsMemory(44, pcmBytes), ct);
        return bytes;
    }

    private string AudioCachePath(NoteUploadedMaterial file) => Path.Combine(AudioCacheFolder,
        file.Sha256 + "-" + file.Samples!.Value.ToString(CultureInfo.InvariantCulture) + ".pcm");
    private string AudioCacheFolder => Path.Combine(Path.GetFullPath(_options.StorageRoot), "audio-cache");
    private string AudioWorkFolder => Path.Combine(Path.GetFullPath(_options.StorageRoot), "audio-work");

    private SemaphoreSlim AudioDecodeGate(string key)
    {
        var hash = 2166136261u;
        foreach (var character in key)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return _audioDecodeGates[(int)(hash % (uint)AudioDecodeGateSlots)];
    }

    private static bool IsValidPcmCache(string path, long expectedBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == expectedBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Removes decoded PCM older than the configured retention, enforces the cache byte budget
    /// oldest-first, and clears work files abandoned by a crashed decode. Active work files are
    /// recent, so the one-hour floor never races the bounded FFmpeg timeout.
    /// </summary>
    internal void PruneAudioStorage(bool force = false, string? keepPath = null)
    {
        var now = _clock.GetUtcNow();
        lock (_audioPruneLock)
        {
            if (!force && now < _nextAudioPruneAt) return;
            _nextAudioPruneAt = now.Add(AudioPruneInterval);
        }
        try
        {
            var cache = new DirectoryInfo(AudioCacheFolder);
            if (cache.Exists)
            {
                var expired = now.AddHours(-_options.AudioCacheMaxAgeHours);
                var entries = cache.EnumerateFiles("*.pcm").OrderBy(file => file.LastWriteTimeUtc).ToArray();
                long total = 0;
                foreach (var entry in entries)
                {
                    if (entry.LastWriteTimeUtc < expired.UtcDateTime)
                    {
                        TryDelete(entry.FullName);
                        continue;
                    }
                    total += entry.Length;
                }
                foreach (var entry in entries)
                {
                    if (total <= _options.AudioCacheMaxBytes) break;
                    if (!entry.Exists) continue;
                    if (keepPath is not null &&
                        string.Equals(entry.FullName, keepPath, StringComparison.Ordinal)) continue;
                    if (TryDelete(entry.FullName)) total -= entry.Length;
                }
            }
            var work = new DirectoryInfo(AudioWorkFolder);
            if (work.Exists)
            {
                var stale = now.Subtract(AudioWorkMaxAge).UtcDateTime;
                foreach (var entry in work.EnumerateFiles())
                    if (entry.LastWriteTimeUtc < stale) TryDelete(entry.FullName);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryDelete(string? path)
    {
        if (path is null) return false;
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string VerifyKey(Guid userId, Guid versionId, int index) =>
        userId.ToString("D") + "/" + versionId.ToString("D") + "/" +
        index.ToString(CultureInfo.InvariantCulture);

    private string FileSignature(Guid userId, Guid versionId, int index,
        NoteUploadedMaterial file)
    {
        var signature = new StringBuilder(160);
        signature.Append(file.Sha256).Append(':').Append(file.SizeBytes).Append(':')
            .Append(PartCount(file));
        for (var part = 0; part < PartCount(file); part++)
        {
            var info = new FileInfo(PartFile(userId, versionId, index, part));
            signature.Append('|').Append(part).Append(':')
                .Append(info.Exists ? info.Length : -1).Append(':')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }
        return signature.ToString();
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static FileStream OpenPrivateWrite(string path)
    {
        EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, streamOptions);
    }

    private static void SetPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        PruneAudioStorage();
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
            var prefix = userId.ToString("D") + "/" + version.ToString("D") + "/";
            _verifiedFiles.RemoveWhere(verified => verified.StartsWith(prefix, StringComparison.Ordinal));
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
        var ledger = File.Exists(path) ? await ReadAsync<Ledger>(path, ct) : new Ledger();
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
    private static string ReadTitle(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var title = document.RootElement.GetProperty("note").GetProperty("title");
        var value = title.ValueKind == JsonValueKind.String ? title.GetString() : null;
        if (value is null || value.Length > 500) throw new InvalidDataException("Invalid native note title metadata");
        return value;
    }
    private static NoteUploadedMaterial FileAt(IReadOnlyList<NoteUploadedMaterial> files, int index) => index >= 0 && index < files.Count
        ? files[index] : throw new DomainException(40401, "材料文件不存在");
    private static int PartCount(NoteUploadedMaterial file) => checked((int)((file.SizeBytes + NoteMaterialUploadOptions.PartSizeBytes - 1) / NoteMaterialUploadOptions.PartSizeBytes));
    private static long PartLength(NoteUploadedMaterial file, int index) => Math.Min(NoteMaterialUploadOptions.PartSizeBytes,
        file.SizeBytes - (long)index * NoteMaterialUploadOptions.PartSizeBytes);
    private static async Task<string> HashAsync(Stream stream, CancellationToken ct) => Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Conflict(string message) => new(40901, message);
    private sealed class Ledger
    {
        public Dictionary<string, StoredVersion> Versions { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, Guid> Heads { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> Deletions { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, StoredNoteState> Notes { get; init; } = new(StringComparer.Ordinal);
        public int SchemaVersion { get; init; } = 1;
    }

    private sealed record StoredVersion(Guid NoteId, Guid? BaseVersionId, string Fingerprint, long SizeBytes,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Phase, bool HasConflict = false);

    private sealed record StoredNoteState(string Status, DateTimeOffset UpdatedAt,
        DateTimeOffset? RecycledAt = null);
}

internal sealed record NativeNoteSearchSource(
    Guid NoteId,
    Guid VersionId,
    string InputJson,
    DateTimeOffset CreatedAt,
    bool HasConflict);
