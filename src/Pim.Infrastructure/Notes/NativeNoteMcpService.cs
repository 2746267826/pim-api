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
    // Keep the MCP package at the same file-count ceiling as the material manifest. A 15-second
    // recording chunk can legitimately produce hundreds of entries, so a smaller transport cap
    // would reject packages the REST material contract accepts.
    internal const int MaxPackageEntries = 20_000;
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
        IReadOnlyList<NoteDerivedArtifactDto> artifacts = [];
        if (includeResults)
        {
            if (snapshot.CurrentVersionId is { } versionId)
                processingStatus = await processing.GetForMcpAsync(noteId, versionId, ct);
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
            processingStatus is null ? null : MapProcessing(processingStatus),
            artifacts.Select(MapArtifact).ToArray(),
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
            snapshot.CurrentVersionId is { } currentVersion
                ? MapProcessing(await processing.GetForMcpAsync(noteId, currentVersion, ct))
                : null,
            (await derived.ListArtifactsAsync(noteId, ct)).Select(MapArtifact).ToArray(),
            ReadReceipt("get_native_note_results", noteId, snapshot.Revision,
                "已读取处理状态、结果与回执"));
    }

    public async Task<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>
        GetProcessingAsync(Guid noteId, Guid? versionId, CancellationToken ct)
    {
        var owner = Owner;
        var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, ct);
        var targetVersionId = versionId ?? snapshot.CurrentVersionId
            ?? throw new DomainException(40401, "当前完整笔记不存在，无法读取处理状态");
        var version = await materials.GetForMcpAsync(owner, targetVersionId, ct);
        if (version.Upload.NoteId != noteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
        var status = await processing.GetForMcpAsync(noteId, targetVersionId, ct);
        return new NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>(
            ReadReceipt("get_native_note_processing", noteId, snapshot.Revision,
                "已读取处理任务、结果链条与回执状态"),
            MapProcessing(status));
    }

    public async Task<(Guid NoteId, Guid VersionId)> ResolvePackageAsync(Guid noteId,
        Guid? versionId, CancellationToken ct)
    {
        var owner = Owner;
        var status = await materials.GetMcpNoteStatusAsync(owner, noteId, ct);
        if (status != "Active")
            throw new DomainException(40901, "笔记在回收站，请先恢复后再下载整包材料");
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
        var packageSha = Hash(Encoding.UTF8.GetBytes(request.PackageBase64 ?? string.Empty));
        return await operations.RunAsync(
            owner,
            request.RequestId,
            noteId,
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
                var package = ParsePackage(noteId, request.PackageBase64 ?? string.Empty,
                    StableVersionId(owner, noteId, request.RequestId));
                NativeNoteMcpSnapshotDto? snapshot = null;
                try
                {
                    snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                }
                catch (DomainException error) when (error.ErrorCode == 40401)
                {
                    // A note does not exist until its first material package is committed. Allow
                    // that first upload, but never let explicit CAS inputs silently pass when there
                    // is no server-side head to compare against.
                }
                if (snapshot is null)
                {
                    if (request.ExpectedCurrentVersionId is not null || request.ExpectedRevision is not null)
                        throw new DomainException(40901,
                            "笔记尚不存在当前版本，不能校验 expectedCurrentVersionId 或 expectedRevision；请重新读取后使用新的 requestId 创建首版本");
                }
                else
                {
                    EnsureActive(snapshot, "上传整包材料");
                    CheckRevision(snapshot, request.ExpectedRevision);
                    if (request.ExpectedCurrentVersionId is { } expectedCurrent &&
                        snapshot.CurrentVersionId != expectedCurrent)
                        throw new DomainException(40901,
                            "当前材料版本已变化，请先重新读取 get_native_note_versions 后再上传");
                }
                var baseVersionId = request.ExpectedCurrentVersionId ?? snapshot?.CurrentVersionId;
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
                        NoteMcpOperationStore.BuildOperationId(owner,
                            "upload_native_note_package", request.RequestId),
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
            noteId,
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
                        NoteMcpOperationStore.BuildOperationId(owner,
                            "promote_native_note_version", request.RequestId),
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
            noteId,
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
                if (snapshot.Status == "Recycled")
                    throw new DomainException(40901, "笔记在回收站，请先恢复后再处理未完成上传");
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
                        NoteMcpOperationStore.BuildOperationId(owner,
                            "cancel_native_note_upload", request.RequestId),
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

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpRecycleDto>>
        RecycleAsync(Guid noteId, NativeNoteMcpRecycleRequest request, CancellationToken ct)
    {
        const string operation = "delete_native_note";
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            noteId,
            operation,
            new { noteId, ExpectedRevision = request.ExpectedRevision },
            async lockedCt =>
            {
                var before = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                CheckRevision(before, request.ExpectedRevision);
                var status = before.Status;
                if (status != "Recycled")
                {
                    var versionIds = await materials.ListNoteVersionIdsAsync(owner, noteId, lockedCt);
                    await processing.StopForRecycleAsync(owner, noteId, versionIds, lockedCt);
                }
                var result = await materials.RecycleAsync(owner, noteId,
                    request.ExpectedRevision, lockedCt);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpRecycleDto>(
                    new NativeNoteMcpReceiptDto(
                        NoteMcpOperationStore.BuildOperationId(owner, operation, request.RequestId),
                        operation,
                        noteId,
                        result.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        status == "Recycled"
                            ? "笔记已在回收站；材料与处理结果仍保留"
                            : "笔记已移入回收站；排队的处理已停止，材料与结果仍保留，可恢复后手动继续"),
                    result);
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpRecycleDto>>
        RestoreAsync(Guid noteId, NativeNoteMcpRecycleRequest request, CancellationToken ct)
    {
        const string operation = "restore_native_note";
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            noteId,
            operation,
            new { noteId, ExpectedRevision = request.ExpectedRevision },
            async lockedCt =>
            {
                var result = await materials.RestoreAsync(owner, noteId,
                    request.ExpectedRevision, lockedCt);
                await processing.MarkRestoredAsync(owner, noteId, lockedCt);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpRecycleDto>(
                    new NativeNoteMcpReceiptDto(
                        NoteMcpOperationStore.BuildOperationId(owner, operation, request.RequestId),
                        operation,
                        noteId,
                        result.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "笔记已恢复为可用状态；此前停止的处理不会自动继续，请手动确认后继续"),
                    result);
            },
            ct);
    }

    public async Task<NativeNoteMcpOperationStatusDto> GetOperationAsync(Guid noteId,
        Guid requestId, CancellationToken ct)
    {
        var result = await operations.GetAsync(Owner, noteId, requestId, ct);
        if (result.NoteId != noteId)
            throw new DomainException(40401, "该 requestId 没有对应当前笔记的操作记录");
        return result;
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>
        StartProcessingAsync(Guid noteId, NativeNoteMcpProcessingStartRequest request,
            CancellationToken ct)
    {
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            noteId,
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
                        NoteMcpOperationStore.BuildOperationId(owner,
                            "start_native_note_processing", request.RequestId),
                        "start_native_note_processing",
                        noteId,
                        snapshot.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "处理任务已按其固定材料版本入队；重复请求会返回同一处理编排"),
                    MapProcessing(status));
            },
            ct);
    }

    public Task<NativeNoteMcpOperationResultDto<NativeNoteMcpRetryDto>>
        RetryProcessingAsync(Guid noteId, NativeNoteMcpProcessingRetryRequest request,
            CancellationToken ct)
    {
        var stepKeys = (request.StepKeys ?? []).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var owner = Owner;
        return operations.RunAsync(
            owner,
            request.RequestId,
            noteId,
            "retry_native_note_processing",
            new
            {
                noteId,
                request.VersionId,
                ExpectedRevision = request.ExpectedRevision,
                stepKeys,
                Consent = request.Consent,
                AcknowledgePossibleDuplicateCharge = request.AcknowledgePossibleDuplicateCharge,
            },
            async lockedCt =>
            {
                if (stepKeys.Length is 0 or > 20 ||
                    stepKeys.Distinct(StringComparer.Ordinal).Count() != stepKeys.Length ||
                    stepKeys.Any(string.IsNullOrWhiteSpace) || request.Consent is null)
                    throw new DomainException(40001, "请选择待恢复步骤并核对处理目标");
                var snapshot = await materials.GetMcpSnapshotAsync(owner, noteId, 1, lockedCt);
                EnsureActive(snapshot, "恢复笔记处理步骤");
                var versionId = request.VersionId ?? snapshot.CurrentVersionId
                    ?? throw new DomainException(40401, "当前完整笔记不存在，无法恢复处理步骤");
                var version = await materials.GetAsync(owner, versionId, lockedCt);
                if (version.Upload.NoteId != noteId)
                    throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
                var tasks = await derived.ListAsync(noteId, lockedCt);
                var task = tasks
                    .Where(item => item.SourceVersionId == versionId)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefault()
                    ?? throw new DomainException(40401, "该材料版本尚无处理任务可恢复");
                if (request.ExpectedRevision is { } expectedRevision)
                {
                    var matchesSnapshot = expectedRevision == snapshot.Revision;
                    var matchesTask = expectedRevision == task.Revision;
                    if (!matchesSnapshot && !matchesTask)
                        throw new DomainException(40901,
                            "笔记或处理任务版本已变化，请先重新读取 get_native_note_versions 或 get_native_note_processing 后再试");
                }
                var expectedTaskRevision = request.ExpectedRevision is { } expected &&
                                           expected == task.Revision
                    ? expected
                    : task.Revision;
                var retryId = StableRetryId(owner, noteId, versionId, request.RequestId);
                _ = await derived.RetryAsync(task.TaskId,
                    new RetryNoteDerivedTaskRequest(
                        retryId,
                        owner,
                        expectedTaskRevision,
                        stepKeys,
                        request.Consent!,
                        request.AcknowledgePossibleDuplicateCharge),
                    lockedCt);
                var status = await processing.GetAsync(noteId, versionId, lockedCt);
                return new NativeNoteMcpOperationResultDto<NativeNoteMcpRetryDto>(
                    new NativeNoteMcpReceiptDto(
                        NoteMcpOperationStore.BuildOperationId(owner,
                            "retry_native_note_processing", request.RequestId),
                        "retry_native_note_processing",
                        noteId,
                        snapshot.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "已恢复所选处理步骤；请核对可能的重复计费提示与最新回执"),
                    new NativeNoteMcpRetryDto(MapProcessing(status)));
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
            noteId,
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
                        NoteMcpOperationStore.BuildOperationId(owner,
                            "adopt_native_note_result", request.RequestId),
                        "adopt_native_note_result",
                        noteId,
                        snapshot.Revision,
                        "applied",
                        false,
                        _clock.GetUtcNow(),
                        "已采用所选处理结果版本；其他版本仍保留"),
                    new NativeNoteMcpAdoptDto(MapArtifact(selected), request.VersionId,
                        selected.HeadVersionId != request.VersionId));
            },
            ct);
    }

    public NativeNoteMcpReceiptDto ReadReceipt(string operation, Guid noteId, Guid? revision,
        string message) =>
        new(NoteMcpOperationStore.BuildReadOperationId(Owner, operation, noteId, revision),
            operation, noteId, revision, "ok", false, _clock.GetUtcNow(), message);

    private static void EnsureActive(NativeNoteMcpSnapshotDto snapshot, string operation)
    {
        if (snapshot.Status == "Active") return;
        throw new DomainException(40901, $"笔记在回收站，请先恢复后再{operation}");
    }

    internal static NativeNoteMcpProcessingDto MapProcessing(NoteProcessingStatusDto status)
        => new(
            status.NoteId,
            status.VersionId,
            status.State.ToString(),
            status.AutoProcessingEnabled,
            status.AutoStarted,
            status.ManualStartAvailable,
            status.RecycleStopped,
            status.ResumePending,
            status.Scope,
            status.IncrementalScopeSupported,
            SanitizePendingReason(status.PendingReason),
            MapChain("asr", status.Asr),
            MapChain("organization", status.Derived),
            status.Artifacts
                .Select(item => new NativeNoteMcpProcessingArtifactDto(item.ArtifactId,
                    item.Kind.ToString(), item.HeadVersionId))
                .ToArray(),
            status.Issues
                .Select(item => new NativeNoteMcpIssueDto(item.Code,
                    SanitizePendingReason(item.Message) ?? string.Empty,
                    item.Task?.ToString().ToLowerInvariant()))
                .ToArray(),
            status.CreatedAt,
            status.UpdatedAt);

    private static string? SanitizePendingReason(string? message)
        => string.IsNullOrWhiteSpace(message)
            ? message
            : NoteErrorSanitizer.SanitizeOperationMessage(message, 40901);

    internal static NativeNoteMcpProcessingChainDto MapChain(string domain,
        NoteProcessingChainDto chain) => new(
            domain,
            chain.State.ToString(),
            chain.Revision,
            chain.TotalItems,
            chain.CompletedItems,
            chain.FailedItems,
            chain.HasResult,
            chain.Errors,
            chain.WaitingReason,
            (chain.Items ?? [])
                .Select(item => new NativeNoteMcpProcessingItemDto(item.Key, item.State,
                    item.Error))
                .ToArray());

    internal static NativeNoteMcpResultArtifactDto MapArtifact(NoteDerivedArtifactDto artifact)
        => new(
            artifact.ArtifactId,
            artifact.Kind.ToString(),
            artifact.HeadVersionId,
            artifact.Versions.Any(version => version.HasConflict),
            artifact.Versions
                .Select(version => new NativeNoteMcpResultVersionDto(
                    version.VersionId,
                    version.Author.ToString(),
                    version.ParentVersionId,
                    version.SourceVersionId,
                    version.ContentSha256,
                    version.HasConflict,
                    version.CreatedAt,
                    version.IsHead))
                .ToArray());

    private static void CheckRevision(NativeNoteMcpSnapshotDto snapshot, Guid? expected)
    {
        if (expected is { } value && value != snapshot.Revision)
            throw new DomainException(40901, "笔记版本已变化，请先重新读取 get_native_note_versions 后再试");
    }

    internal static PackageContents ParsePackage(Guid noteId, string packageBase64, Guid versionId)
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
            // Bound the central directory before per-entry decompression/caching so a ZIP with
            // an unreasonable number of tiny entries cannot exhaust CPU or the file ledger.
            if (archive.Entries.Count > MaxPackageEntries)
                throw new DomainException(41301,
                    $"材料包条目数超过 {MaxPackageEntries} 个上限");
            entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.Length == 0 || path.EndsWith('/') ||
                    path.Length > 512 ||
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

            manifest["versionId"] = versionId.ToString("D");
            var rewrittenInput = RewriteInputVersionId(inputText, versionId);
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

    private static string RewriteInputVersionId(string inputText, Guid versionId)
    {
        using var document = JsonDocument.Parse(inputText);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new DomainException(40001, "material-version.json 不是 JSON 对象");

        var builder = new StringBuilder(inputText.Length + 64);
        builder.Append('{');
        var first = true;
        var wroteId = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!first) builder.Append(',');
            first = false;
            builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
            if (property.NameEquals("id"))
            {
                builder.Append(JsonSerializer.Serialize(versionId.ToString("D")));
                wroteId = true;
            }
            else
            {
                // Preserve each nested token verbatim. The manifest hashes note.json and the
                // recording manifests from these raw representations, so reserializing the
                // enclosing document would change escaping/whitespace and reject valid packages.
                builder.Append(property.Value.GetRawText());
            }
        }
        if (!wroteId)
        {
            if (!first) builder.Append(',');
            builder.Append("\"id\":").Append(JsonSerializer.Serialize(versionId.ToString("D")));
        }
        builder.Append('}');
        return builder.ToString();
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

    private static Guid StableRetryId(Guid owner, Guid noteId, Guid versionId, Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new DomainException(40001, "requestId 不能为空");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{owner:D}:{noteId:D}:{versionId:D}:{requestId:D}:native-note-retry"));
        var identity = bytes.AsSpan(0, 16).ToArray();
        identity[6] = (byte)((identity[6] & 0x0f) | 0x50);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new Guid(identity);
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Hash(string text)
        => Hash(Encoding.UTF8.GetBytes(text));

    internal sealed record PackageContents(
        Guid VersionId,
        byte[] ZipBytes,
        string InputJson,
        string ManifestJson,
        IReadOnlyDictionary<string, byte[]> Files);
}
