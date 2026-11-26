using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Owner-scoped orchestration for the native-note MCP tools. Every operation runs through the
/// current user from the MCP-scoped JWT; the material, processing and derived stores keep their
/// own owner boundaries, so one account can never address another account's note by id.
/// </summary>
public sealed class NativeNoteMcpService(
    NoteMaterialUploadStore materials,
    NoteProcessingService processing,
    NoteDerivedService derived,
    NoteMcpOperationStore operations,
    ICurrentUserService user,
    TimeProvider? clock = null)
{
    public const int MaxPackageBytes = 64 * 1024 * 1024;
    private const int MaxBase64Characters = ((MaxPackageBytes + 2) / 3) * 4 + 16;
    private static readonly string[] PackageMetadataPaths = ["material-version.json", "materials.json"];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");

    public Task<NativeNoteMcpPageDto> ListAsync(string? status, int offset, int limit,
        CancellationToken ct) =>
        materials.ListMcpNotesAsync(Owner, status, offset, limit, ct);

    public async Task<NativeNoteMcpDetailDto> GetAsync(Guid noteId, bool includeResults,
        int versionLimit, CancellationToken ct)
    {
        var owner = Owner;
        var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, versionLimit, ct);
        var current = snapshot.Versions.FirstOrDefault(item => item.VersionId == snapshot.CurrentVersionId);
        NoteProcessingStatusDto? processingStatus = null;
        IReadOnlyList<NoteDerivedTaskDto> tasks = [];
        IReadOnlyList<NoteDerivedArtifactDto> artifacts = [];
        if (includeResults)
        {
            if (snapshot.CurrentVersionId is { } versionId)
                processingStatus = await processing.GetAsync(noteId, versionId, ct);
            tasks = await derived.ListAsync(noteId, ct);
            artifacts = await derived.ListArtifactsAsync(noteId, ct);
        }
        return new NativeNoteMcpDetailDto(
            snapshot.NoteId,
            snapshot.Title,
            snapshot.Status,
            snapshot.Revision,
            snapshot.CurrentVersionId,
            snapshot.HasConflict,
            snapshot.TotalVersions,
            snapshot.UpdatedAt,
            current,
            snapshot.Versions,
            snapshot.ConflictCandidates,
            processingStatus,
            tasks,
            artifacts,
            ReadReceipt("get_native_note", noteId, snapshot.Revision, "已读取笔记聚合内容"));
    }

    public async Task<NativeNoteMcpVersionsDto> GetVersionsAsync(Guid noteId, int limit,
        CancellationToken ct)
    {
        var snapshot = await materials.GetMcpSnapshotAsync(Owner, noteId, limit, ct);
        return new NativeNoteMcpVersionsDto(
            snapshot.NoteId,
            snapshot.Revision,
            snapshot.CurrentVersionId,
            snapshot.TotalVersions,
            snapshot.HasConflict,
            snapshot.Versions,
            snapshot.ConflictCandidates,
            ReadReceipt("get_native_note_versions", noteId, snapshot.Revision,
                "已读取当前版本与冲突候选"));
    }

    public async Task<NativeNoteMcpResultsDto> GetResultsAsync(Guid noteId,
        CancellationToken ct)
    {
        var snapshot = await materials.GetMcpSnapshotAsync(Owner, noteId, 1, ct);
        return new NativeNoteMcpResultsDto(
            noteId,
            snapshot.Revision,
            await derived.ListAsync(noteId, ct),
            await derived.ListArtifactsAsync(noteId, ct),
            ReadReceipt("get_native_note_results", noteId, snapshot.Revision,
                "已读取处理任务、结果与回执"));
    }

    public async Task<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>
        GetProcessingAsync(Guid noteId, Guid? versionId, CancellationToken ct)
    {
        var owner = Owner;
        var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, ct);
        var targetVersionId = versionId ?? snapshot.CurrentVersionId
            ?? throw new DomainException(40401, "当前完整笔记不存在，无法读取处理状态");
        var version = await materials.GetAsync(owner, targetVersionId, ct);
        if (version.Upload.NoteId != noteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
        var status = await processing.GetAsync(noteId, targetVersionId, ct);
        return new NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>(
            ReadReceipt("get_native_note_processing", noteId, snapshot.Revision,
                "已读取处理任务、结果链条与回执状态"),
            new NativeNoteMcpProcessingDto(status));
    }

    public async Task<(Guid NoteId, Guid VersionId)> ResolvePackageAsync(Guid noteId,
        Guid? versionId, CancellationToken ct)
    {
        var owner = Owner;
        var status = await materials.GetMcpNoteStatusAsync(owner, noteId, ct);
        if (status == "Deleted")
            throw new DomainException(40901, "笔记已删除，请先恢复后再下载整包材料");
        if (versionId is { } requested)
        {
            var version = await materials.GetAsync(owner, requested, ct);
            if (version.Upload.NoteId != noteId)
                throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
            if (version.Upload.Phase == "Uploading")
                throw new DomainException(40901, "未完成的材料版本不能导出");
            return (noteId, requested);
        }
        var current = await materials.GetCurrentAsync(owner, noteId, ct);
        return (noteId, current.Upload.VersionId);
    }

    public Task WritePackageAsync(Guid noteId, Guid versionId, Stream output, CancellationToken ct)
        => materials.WriteBundleAsync(Owner, versionId, output, ct);

    public async Task<NativeNoteMcpOperationResultDto<NativeNoteMcpPackageUploadDto>>
        UploadPackageAsync(Guid noteId, NativeNoteMcpPackageRequest request, CancellationToken ct)
    {
        var owner = Owner;
        var package = ParsePackage(noteId, request.PackageBase64, StableVersionId(owner, noteId, request.RequestId));
        var packageSha = Hash(package.ZipBytes);
        return await operations.RunAsync(
            owner,
            request.RequestId,
            "upload_native_note_package",
            new
            {
                noteId,
                packageSha256 = packageSha,
                ExpectedCurrentVersionId = request.ExpectedCurrentVersionId,
                ExpectedRevision = request.ExpectedRevision,
                request.Promote,
            },
            async lockedCt =>
            {
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                EnsureActive(snapshot, "上传整包材料");
                CheckRevision(snapshot, request.ExpectedRevision);
                if (request.ExpectedCurrentVersionId is { } expectedCurrent &&
                    snapshot.CurrentVersionId != expectedCurrent)
                    throw new DomainException(40901,
                        "当前材料版本已变化，请先重新读取 get_native_note_versions 后再上传");
                var baseVersionId = request.ExpectedCurrentVersionId ?? snapshot.CurrentVersionId;
                var beginRequest = new BeginNoteMaterialUploadRequest(
                    package.VersionId,
                    noteId,
                    baseVersionId,
                    package.InputJson,
                    package.ManifestJson,
                    StorageConsent: true);
                var begun = await materials.BeginAsync(owner, beginRequest, lockedCt);
                foreach (var file in begun.Files)
                {
                    if (!package.Files.TryGetValue(file.Path, out var bytes) ||
                        bytes.Length != file.SizeBytes)
                        throw new DomainException(40001, "材料包文件缺失或大小与材料清单不一致");
                    for (var part = 0; part < file.PartCount; part++)
                    {
                        var offset = part * begun.Limits.PartSizeBytes;
                        var length = Math.Min(begun.Limits.PartSizeBytes, bytes.Length - offset);
                        var chunk = bytes.AsSpan(offset, length).ToArray();
                        await materials.UploadPartAsync(owner, begun.VersionId, file.Index, part,
                            Hash(chunk), new MemoryStream(chunk, writable: false), lockedCt);
                    }
                }
                var completed = await materials.CompleteAsync(owner, begun.VersionId,
                    request.Promote, lockedCt);
                var after = await materials.GetMcpSnapshotAsync(owner, noteId, 100, lockedCt);
                var upload = after.Versions.FirstOrDefault(item => item.VersionId == begun.VersionId)
                    ?? completed;
                var conflict = upload.HasConflict || upload.Phase == "Conflict";
                var isCurrent = after.CurrentVersionId == begun.VersionId;
                var result = new NativeNoteMcpPackageUploadDto(
                    upload,
                    after.CurrentVersionId,
                    after.Revision,
                    conflict,
                    after.ConflictCandidates);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpPackageUploadDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        "upload_native_note_package",
                        noteId,
                        after.Revision,
                        conflict ? "conflict" : "applied",
                        false,
                        _clock.GetUtcNow(),
                        conflict
                            ? "整包材料已保存为冲突候选；请核对当前版本后用 promote_native_note_version 选择"
                            : isCurrent
                                ? "整包材料已保存并设为当前版本"
                                : "整包材料已保存；按要求未设为当前版本，可稍后用 promote_native_note_version 选择"),
                    result);
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpVersionMutationDto>>
        PromoteAsync(Guid noteId, NativeNoteMcpPromoteRequest request, CancellationToken ct)
    {
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            "promote_native_note_version",
            new
            {
                noteId,
                request.VersionId,
                ExpectedCurrentVersionId = request.ExpectedCurrentVersionId,
                ExpectedRevision = request.ExpectedRevision,
            },
            async lockedCt =>
            {
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                EnsureActive(snapshot, "选择当前材料版本");
                CheckRevision(snapshot, request.ExpectedRevision);
                var version = await materials.GetAsync(owner, request.VersionId, lockedCt);
                if (version.Upload.NoteId != noteId)
                    throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
                var promoted = await materials.PromoteAsync(owner, request.VersionId,
                    request.ExpectedCurrentVersionId ?? snapshot.CurrentVersionId, lockedCt);
                var after = await materials.GetMcpSnapshotAsync(owner, noteId, 100, lockedCt);
                var result = new NativeNoteMcpVersionMutationDto(
                    promoted,
                    after.CurrentVersionId,
                    after.Revision,
                    after.ConflictCandidates);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpVersionMutationDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        "promote_native_note_version",
                        noteId,
                        after.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "已选择该材料版本为当前版本；旧版本仍保留"),
                    result);
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpCancelUploadDto>>
        CancelUploadAsync(Guid noteId, NativeNoteMcpCancelUploadRequest request, CancellationToken ct)
    {
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            "cancel_native_note_upload",
            new
            {
                noteId,
                request.VersionId,
                ExpectedRevision = request.ExpectedRevision,
            },
            async lockedCt =>
            {
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 100, lockedCt);
                if (snapshot.Status == "Deleted")
                    throw new DomainException(40901, "笔记已删除，请先恢复后再处理未完成上传");
                CheckRevision(snapshot, request.ExpectedRevision);
                var known = snapshot.Versions.FirstOrDefault(item => item.VersionId == request.VersionId);
                var removed = known is { Phase: "Uploading" };
                if (removed)
                    await materials.CancelAsync(owner, request.VersionId, lockedCt);
                NativeNoteMcpSnapshotDto? after = null;
                try
                {
                    after = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                }
                catch (DomainException error) when (error.ErrorCode == 40401)
                {
                    // The cancelled version was the note's only version.
                }
                var result = new NativeNoteMcpCancelUploadDto(
                    request.VersionId,
                    removed,
                    after?.Revision);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpCancelUploadDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        "cancel_native_note_upload",
                        noteId,
                        after?.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        removed ? "未完成上传已取消，本机原材料不受影响" : "该版本不在上传中，未做改动"),
                    result);
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpLifecycleDto>>
        LifecycleAsync(Guid noteId, string action, NativeNoteMcpLifecycleRequest request,
            CancellationToken ct)
    {
        var owner = Owner;
        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        var operation = normalizedAction switch
        {
            "archive" => "archive_native_note",
            "restore" => "restore_native_note",
            "delete" => "delete_native_note",
            _ => throw new DomainException(40001, "action 必须是 archive、restore 或 delete"),
        };
        return operations.RunAsync(
            owner,
            request.RequestId,
            operation,
            new
            {
                noteId,
                action = normalizedAction,
                ExpectedRevision = request.ExpectedRevision,
            },
            async lockedCt =>
            {
                var result = await materials.UpdateLifecycleAsync(owner, noteId, normalizedAction,
                    request.ExpectedRevision, lockedCt);
                var message = normalizedAction switch
                {
                    "archive" => "笔记已归档；材料与处理结果仍保留，可随时恢复",
                    "restore" => "笔记已恢复为可用状态",
                    "delete" => "笔记已删除；材料仍保留，可恢复后继续使用",
                    _ => "笔记状态已更新",
                };
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpLifecycleDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        operation,
                        noteId,
                        result.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        message),
                    result);
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>
        StartProcessingAsync(Guid noteId, NativeNoteMcpProcessingStartRequest request,
            CancellationToken ct)
    {
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            "start_native_note_processing",
            new
            {
                noteId,
                request.VersionId,
                ExpectedRevision = request.ExpectedRevision,
            },
            async lockedCt =>
            {
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                EnsureActive(snapshot, "发起笔记处理");
                CheckRevision(snapshot, request.ExpectedRevision);
                var version = await materials.GetAsync(owner, request.VersionId, lockedCt);
                if (version.Upload.NoteId != noteId)
                    throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
                if (version.Upload.Phase == "Uploading")
                    throw new DomainException(40901, "请先完成整包材料上传，再发起处理");
                var status = await processing.StartAsync(
                    new StartNoteProcessingRequest(noteId, request.VersionId), lockedCt);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        "start_native_note_processing",
                        noteId,
                        snapshot.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "处理任务已按其固定材料版本入队；重复请求会返回同一处理编排"),
                    new NativeNoteMcpProcessingDto(status));
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpRetryDto>>
        RetryProcessingAsync(Guid noteId, NativeNoteMcpProcessingRetryRequest request,
            CancellationToken ct)
    {
        if (request.StepKeys is not { Count: > 0 } || request.Consent is null)
            throw new DomainException(40001, "请选择待恢复步骤并核对处理目标");
        var stepKeys = request.StepKeys;
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            "retry_native_note_processing",
            new
            {
                noteId,
                request.TaskId,
                request.RetryId,
                ExpectedRevision = request.ExpectedRevision,
                stepKeys = stepKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                Consent = request.Consent,
                AcknowledgePossibleDuplicateCharge = request.AcknowledgePossibleDuplicateCharge,
            },
            async lockedCt =>
            {
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                EnsureActive(snapshot, "恢复笔记处理步骤");
                var task = await derived.GetAsync(request.TaskId, lockedCt);
                if (task.NoteId != noteId)
                    throw new DomainException(40401, "处理任务不存在或不属于当前笔记");
                var updated = await derived.RetryAsync(request.TaskId,
                    new RetryNoteDerivedTaskRequest(
                        request.RetryId,
                        owner,
                        request.ExpectedRevision,
                        stepKeys,
                        request.Consent,
                        request.AcknowledgePossibleDuplicateCharge),
                    lockedCt);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpRetryDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        "retry_native_note_processing",
                        noteId,
                        snapshot.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "已恢复所选处理步骤；请核对可能的重复计费提示与最新回执"),
                    new NativeNoteMcpRetryDto(updated));
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpAdoptDto>>
        AdoptResultAsync(Guid noteId, Guid artifactId, NativeNoteMcpAdoptRequest request,
            CancellationToken ct)
    {
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            "adopt_native_note_result",
            new
            {
                noteId,
                artifactId,
                request.VersionId,
                ExpectedHeadRevision = request.ExpectedHeadRevision,
            },
            async lockedCt =>
            {
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                EnsureActive(snapshot, "采用处理结果");
                var artifact = await derived.GetArtifactAsync(artifactId, lockedCt);
                if (artifact.NoteId != noteId)
                    throw new DomainException(40401, "处理结果不存在或不属于当前笔记");
                var selected = await derived.SelectAsync(artifactId,
                    new SelectNoteDerivedVersionRequest(request.ExpectedHeadRevision, request.VersionId),
                    lockedCt);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpAdoptDto>(
                    new NativeNoteMcpReceiptDto(
                        request.RequestId,
                        "adopt_native_note_result",
                        noteId,
                        snapshot.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "已采用所选处理结果版本；其他版本仍保留"),
                    new NativeNoteMcpAdoptDto(selected, request.VersionId, selected.HeadVersionId != request.VersionId));
            },
            ct);
    }

    public NativeNoteMcpReceiptDto ReadReceipt(string operation, Guid noteId, Guid? revision,
        string message) =>
        new(null, operation, noteId, revision, "ok", false, _clock.GetUtcNow(), message);

    private static void EnsureActive(NativeNoteMcpSnapshotDto snapshot, string operation)
    {
        if (snapshot.Status == "Active") return;
        throw new DomainException(40901, snapshot.Status == "Deleted"
            ? $"笔记已删除，请先恢复后再{operation}"
            : $"笔记已归档，请先恢复后再{operation}");
    }

    private static void CheckRevision(NativeNoteMcpSnapshotDto snapshot, Guid? expected)
    {
        if (expected is { } value && value != snapshot.Revision)
            throw new DomainException(40901, "笔记版本已变化，请先重新读取 get_native_note_versions 后再试");
    }

    private static PackageContents ParsePackage(Guid noteId, string packageBase64, Guid versionId)
    {
        if (string.IsNullOrWhiteSpace(packageBase64))
            throw new DomainException(40001, "材料包为空");
        if (packageBase64.Length > MaxBase64Characters)
            throw new DomainException(41301, "材料包超过 MCP 单次 64 MB 上限，请改用 REST 分块上传");
        byte[] zipBytes;
        try
        {
            zipBytes = Convert.FromBase64String(packageBase64);
        }
        catch (FormatException)
        {
            throw new DomainException(40001, "packageBase64 不是有效的 Base64 材料包");
        }
        if (zipBytes.Length == 0) throw new DomainException(40001, "材料包为空");
        if (zipBytes.Length > MaxPackageBytes)
            throw new DomainException(41301, "材料包超过 MCP 单次 64 MB 上限，请改用 REST 分块上传");

        Dictionary<string, byte[]> entries;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zipBytes, writable: false),
                ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0)
                throw new DomainException(40001, "材料包为空");
            entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.Length == 0 || path.EndsWith('/') ||
                    path.StartsWith('/') || path.Contains(':') ||
                    path.Split('/').Any(segment => segment is "" or "." or ".."))
                    throw new DomainException(40001, "材料包包含无效路径");
                if (!entries.TryAdd(path, []))
                    throw new DomainException(40001, "材料包包含重复路径");
                if (entry.Length is < 0 or > MaxPackageBytes ||
                    total > MaxPackageBytes - entry.Length)
                    throw new DomainException(41301, "材料包解压后超过 64 MB 上限");
                using var source = entry.Open();
                using var target = new MemoryStream(checked((int)entry.Length));
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    if (target.Length + read > MaxPackageBytes - total)
                        throw new DomainException(41301, "材料包解压后超过 64 MB 上限");
                    target.Write(buffer, 0, read);
                }
                total += target.Length;
                entries[path] = target.ToArray();
            }
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                               or NotSupportedException)
        {
            throw new DomainException(40001, "材料包不是有效的 ZIP 文件");
        }

        if (!entries.TryGetValue("material-version.json", out var inputBytes) ||
            !entries.TryGetValue("materials.json", out var manifestBytes))
            throw new DomainException(40001, "材料包缺少 material-version.json 或 materials.json");

        try
        {
            var inputText = Encoding.UTF8.GetString(inputBytes);
            var manifestText = Encoding.UTF8.GetString(manifestBytes);
            var input = JsonNode.Parse(inputText)?.AsObject()
                ?? throw new DomainException(40001, "material-version.json 不是 JSON 对象");
            var manifest = JsonNode.Parse(manifestText)?.AsObject()
                ?? throw new DomainException(40001, "materials.json 不是 JSON 对象");
            var manifestNoteId = manifest["noteId"]?.GetValue<string>();
            if (!string.Equals(manifestNoteId, noteId.ToString("D"), StringComparison.Ordinal))
                throw new DomainException(40001, "材料包不属于当前笔记");
            if (manifest["files"] is not JsonArray manifestFiles)
                throw new DomainException(40001, "materials.json 缺少文件清单");
            var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in manifestFiles)
            {
                var path = file?["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path) || !expectedPaths.Add(path))
                    throw new DomainException(40001, "材料清单包含无效或重复路径");
            }
            if (entries.Keys.Any(path => !expectedPaths.Contains(path) &&
                    !PackageMetadataPaths.Contains(path, StringComparer.Ordinal)))
                throw new DomainException(40001, "材料包包含清单之外的文件");
            if (expectedPaths.Any(path => !entries.ContainsKey(path)))
                throw new DomainException(40001, "材料包缺少材料清单中的文件");

            input["id"] = versionId.ToString("D");
            manifest["versionId"] = versionId.ToString("D");
            var rewrittenInput = input.ToJsonString();
            manifest["inputSha256"] = Hash(Encoding.UTF8.GetBytes(rewrittenInput));
            var rewrittenManifest = manifest.ToJsonString();
            return new PackageContents(versionId, zipBytes, rewrittenInput, rewrittenManifest,
                expectedPaths.ToDictionary(path => path, path => entries[path], StringComparer.Ordinal));
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException
            or FormatException or ArgumentException)
        {
            throw new DomainException(40001, "材料包元数据格式无效");
        }
    }

    private static Guid StableVersionId(Guid owner, Guid noteId, Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new DomainException(40001, "requestId 不能为空");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{owner:D}:{noteId:D}:{requestId:D}:native-note-package"));
        var identity = bytes.AsSpan(0, 16).ToArray();
        identity[6] = (byte)((identity[6] & 0x0f) | 0x50);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new Guid(identity);
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Hash(string text)
        => Hash(Encoding.UTF8.GetBytes(text));

    private sealed record PackageContents(
        Guid VersionId,
        byte[] ZipBytes,
        string InputJson,
        string ManifestJson,
        IReadOnlyDictionary<string, byte[]> Files);
}
