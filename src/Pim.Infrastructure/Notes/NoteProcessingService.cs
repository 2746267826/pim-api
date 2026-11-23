using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// High-level, owner-scoped orchestration for one immutable material version. The child task ids are
/// derived from owner/version/type, so replay can converge existing files instead of allocating a
/// second ASR or derived task. This layer never calls providers; it only persists task intents.
/// </summary>
public sealed class NoteProcessingService(
    NoteProcessingStore processing,
    NoteMaterialUploadStore materials,
    NoteAsrService asr,
    NoteDerivedService derived,
    NoteDerivedJob derivedJob,
    NoteAsrTaskStore asrTasks,
    NoteDerivedStore derivedTasks,
    PimDbContext db,
    ICurrentUserService user,
    ILogger<NoteProcessingService> logger)
{
    internal const string FullVersionScope = "FullVersion";
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");

    public async Task<NoteProcessingStatusDto> StartAsync(StartNoteProcessingRequest request,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await StartForOwnerAsync(owner, request, automatic: false, ct)
            ?? throw new DomainException(40901, "该材料版本当前不能发起处理");
    }

    public async Task<NoteProcessingStatusDto> GetAsync(Guid noteId, Guid versionId,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await GetForOwnerAsync(owner, noteId, versionId, ct);
    }

    public async Task<NoteProcessingStatusDto> GetByProcessingIdAsync(Guid processingId,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var state = await processing.ReadAsync(owner, processingId, ct);
        return await GetForOwnerAsync(owner, state.NoteId, state.VersionId, ct);
    }

    public async Task<NoteProcessingStatusDto?> TryAutoStartAfterCompletionAsync(
        Guid owner, Guid versionId, CancellationToken ct)
    {
        try
        {
            var version = await materials.GetAsync(owner, versionId, ct);
            if (version.Upload.Phase != "Uploaded" || !version.Upload.IsCurrent) return null;
            var request = new StartNoteProcessingRequest(version.Upload.NoteId, versionId);
            return await StartForOwnerAsync(owner, request, automatic: true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(
                "Auto note processing did not start for version {VersionId} ({ErrorType}); material completion remains authoritative",
                versionId, error.GetType().Name);
            return null;
        }
    }

    private async Task<NoteProcessingStatusDto?> StartForOwnerAsync(Guid owner,
        StartNoteProcessingRequest request, bool automatic, CancellationToken ct)
    {
        if (request.NoteId == Guid.Empty || request.VersionId == Guid.Empty)
            throw new DomainException(40001, "笔记或材料版本标识无效");
        var autoEnabled = await AutoEnabledAsync(owner, ct);
        var supplied = await materials.GetAsync(owner, request.VersionId, ct);
        if (supplied.Upload.NoteId != request.NoteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");

        var processingId = NoteProcessingIdentity.Processing(owner, request.VersionId);
        return await processing.WithLockAsync(owner, async lockedCt =>
        {
            var version = await materials.GetAsync(owner, request.VersionId, lockedCt);
            var state = await processing.TryReadAsync(owner, processingId, lockedCt)
                ?? new NoteProcessingRecord
                {
                    Owner = owner,
                    ProcessingId = processingId,
                    NoteId = request.NoteId,
                    VersionId = request.VersionId,
                    AutoProcessingEnabled = autoEnabled
                };
            if (state.NoteId != request.NoteId || state.VersionId != request.VersionId)
                throw new DomainException(40901, "处理编排身份与材料版本不一致");
            state.AutoProcessingEnabled = autoEnabled;
            if (automatic) state.AutoRequested = true;
            else state.ManualStarted = true;

            if (version.Upload.Phase == "Uploading")
            {
                if (!automatic)
                    throw new DomainException(40901, "请先完成固定材料上传，再发起高层处理");
                return await SavePendingAsync(state, "material_incomplete",
                    "材料仍在上传，等待完整版本后再发起处理", lockedCt);
            }

            var activeOwner = await IsActiveOwnerAsync(owner, lockedCt);
            if (!activeOwner)
            {
                if (!automatic)
                    throw new DomainException(40301, "账号已停用，不能发起笔记处理");
                return await SavePendingAsync(state, "owner_inactive",
                    "账号已停用，材料保持待发起；启用账号后可手动处理", lockedCt);
            }
            var hasAudio = HasAudio(version);
            var asrTaskId = NoteProcessingIdentity.Task(owner, request.VersionId,
                NoteProcessingTaskKind.Asr);
            var derivedTaskId = NoteProcessingIdentity.Task(owner, request.VersionId,
                NoteProcessingTaskKind.Derived);
            var asrState = hasAudio ? await TryReadAsrAsync(owner, asrTaskId, lockedCt) : null;
            if (asrState is not null)
            {
                try
                {
                    asrState = await asr.EnsureAttemptForOwnerAsync(owner, asrTaskId,
                        request.VersionId, lockedCt);
                }
                catch (DomainException error)
                {
                    state.PendingReason = error.Message;
                    state.Issues =
                    [
                        new("asr_attempt_recovery", error.Message, NoteProcessingTaskKind.Asr)
                    ];
                    _ = await processing.SaveLockedAsync(state, lockedCt);
                    if (!automatic) throw;
                    return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
                }
            }
            var asrCreated = asrState is not null;
            var derivedCreated = await TryReadDerivedAsync(owner, derivedTaskId, lockedCt) is not null;
            if ((!hasAudio || asrCreated) && derivedCreated)
            {
                if (state.StartedAt is null || state.PendingReason is not null ||
                    state.Issues.Count > 0)
                {
                    state.PendingReason = null;
                    state.Issues = [];
                    state.StartedAt ??= DateTimeOffset.UtcNow;
                    _ = await processing.SaveLockedAsync(state, lockedCt);
                }
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            }
            if (automatic && !autoEnabled)
                return await SavePendingAsync(state, "auto_processing_disabled",
                    "自动笔记处理未开启；材料已完整，可在笔记处理页手动发起", lockedCt);

            NoteProcessingTaskKind? startingTask = null;
            try
            {
                if (!hasAudio)
                    state.AsrRequest = null;
                else if (!asrCreated)
                    state.AsrRequest = await asr.BuildDefaultRequestForProcessingAsync(
                        owner, version, lockedCt);
                if (!derivedCreated)
                {
                    var consent = await derived.ResolveDefaultConsentForProcessingAsync(
                        owner, version, lockedCt);
                    state.DerivedRequest = new StartNoteDerivedTaskRequest(derivedTaskId,
                        owner, request.VersionId, hasAudio ? asrTaskId : null, consent);
                }
                state.PendingReason = "处理任务正在持久化入队";
                state.Issues = [];
                _ = await processing.SaveLockedAsync(state, lockedCt);

                if (hasAudio && !asrCreated)
                {
                    startingTask = NoteProcessingTaskKind.Asr;
                    _ = await asr.StartForOwnerAsync(owner, state.AsrRequest!, lockedCt);
                }
                if (!derivedCreated)
                {
                    startingTask = NoteProcessingTaskKind.Derived;
                    _ = await derived.StartForOwnerAsync(owner, state.DerivedRequest,
                        waitForAsr: hasAudio, allowIncompleteAsr: false, lockedCt);
                }
            }
            catch (DomainException error)
            {
                state.PendingReason = error.Message;
                state.Issues =
                [
                    new("processing_not_started", error.Message,
                        startingTask ?? (hasAudio ? NoteProcessingTaskKind.Asr : NoteProcessingTaskKind.Derived))
                ];
                _ = await processing.SaveLockedAsync(state, lockedCt);
                if (!automatic) throw;
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            }
            catch (Exception error) when (automatic && error is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Note processing start failed for version {VersionId} ({ErrorType}); no provider call was made",
                    request.VersionId, error.GetType().Name);
                return await SavePendingAsync(state, "processing_start_failed",
                    "处理任务未能持久化入队，材料保持待发起；请稍后手动重试", lockedCt);
            }

            state.StartedAt = DateTimeOffset.UtcNow;
            state.AutoStarted = automatic;
            state.PendingReason = null;
            state.Issues = [];
            _ = await processing.SaveLockedAsync(state, lockedCt);
            return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
        }, ct);
    }

    private async Task<NoteProcessingStatusDto> GetForOwnerAsync(Guid owner, Guid noteId,
        Guid versionId, CancellationToken ct)
    {
        await RequireActiveOwnerAsync(owner, ct);
        var version = await materials.GetAsync(owner, versionId, ct);
        if (version.Upload.NoteId != noteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
        var autoEnabled = await AutoEnabledAsync(owner, ct);
        var state = await processing.TryReadAsync(owner,
            NoteProcessingIdentity.Processing(owner, versionId), ct);
        return await BuildStatusAsync(owner, version, state, autoEnabled, ct);
    }

    private async Task<NoteProcessingStatusDto> SavePendingAsync(NoteProcessingRecord state,
        string code, string message, CancellationToken ct)
    {
        state.PendingReason = message;
        state.Issues = [new(code, message)];
        _ = await processing.SaveLockedAsync(state, ct);
        var version = await materials.GetAsync(state.Owner, state.VersionId, ct);
        return await BuildStatusAsync(state.Owner, version, state,
            state.AutoProcessingEnabled, ct);
    }

    private async Task<NoteProcessingStatusDto> BuildStatusAsync(Guid owner,
        NoteMaterialVersionDto version, NoteProcessingRecord? state, bool autoEnabled,
        CancellationToken ct)
    {
        var hasAudio = HasAudio(version);
        var asrTaskId = NoteProcessingIdentity.Task(owner, version.Upload.VersionId,
            NoteProcessingTaskKind.Asr);
        var derivedTaskId = NoteProcessingIdentity.Task(owner, version.Upload.VersionId,
            NoteProcessingTaskKind.Derived);
        var asrState = hasAudio ? await TryReadAsrAsync(owner, asrTaskId, ct) : null;
        var derivedState = await TryReadDerivedAsync(owner, derivedTaskId, ct);
        var asrChain = await DescribeAsrAsync(owner, asrState, hasAudio, version, state, ct);
        var derivedChain = await DescribeDerivedAsync(owner, derivedState, hasAudio, asrState,
            version, state, ct);
        var artifacts = await ArtifactsAsync(owner, derivedState, ct);
        var allRequiredCreated = (!hasAudio || asrState is not null) && derivedState is not null;
        var pendingReason = PendingReason(version, state, autoEnabled, allRequiredCreated);
        var overall = OverallState(version, hasAudio, asrChain, derivedChain,
            allRequiredCreated);
        var created = state?.CreatedAt ?? version.Upload.CreatedAt;
        var updated = state?.UpdatedAt ?? version.Upload.CreatedAt;
        var issues = (state?.Issues ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(pendingReason) &&
            issues.All(x => x.Message != pendingReason))
            issues.Add(new("processing_pending", pendingReason));
        issues = issues.DistinctBy(x => (x.Code, x.Message, x.Task)).ToList();
        return new(NoteProcessingIdentity.Processing(owner, version.Upload.VersionId),
            version.Upload.NoteId, version.Upload.VersionId, overall, autoEnabled,
            state?.AutoStarted == true,
            version.Upload.Phase != "Uploading" && !allRequiredCreated, FullVersionScope,
            IncrementalScopeSupported: false, pendingReason, asrChain, derivedChain,
            artifacts, issues, created, updated);
    }

    private async Task<NoteProcessingChainDto> DescribeAsrAsync(Guid owner, NoteAsrState? task,
        bool required, NoteMaterialVersionDto version, NoteProcessingRecord? processing,
        CancellationToken ct)
    {
        await Task.CompletedTask;
        var taskId = NoteProcessingIdentity.Task(owner, version.Upload.VersionId,
            NoteProcessingTaskKind.Asr);
        if (!required)
            return new(NoteProcessingTaskKind.Asr, NoteProcessingChainState.NotApplicable,
                null, false, null, null, 0, 0, 0, false, [], null);
        if (task is null)
        {
            var state = version.Upload.Phase == "Uploading"
                ? NoteProcessingChainState.WaitingForMaterial
                : NoteProcessingChainState.PendingStart;
            return new(NoteProcessingTaskKind.Asr, state, taskId, false, null, null,
                0, 0, 0, false, [], processing?.PendingReason ?? "ASR 任务尚未创建");
        }

        var phase = NoteAsrTaskStore.Describe(task).Phase;
        var errors = task.Steps
            .SelectMany(step => step.Attempts)
            .Where(attempt => attempt.Status is not ("Queued" or "Running" or "Succeeded"))
            .Select(attempt => attempt.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => SafeError(message!))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        var completed = task.Steps.Count(step => step.Current.Status == "Succeeded");
        var failed = task.Steps.Count(step => step.Current.Status is not
            ("Queued" or "Running" or "Succeeded"));
        return new(NoteProcessingTaskKind.Asr, MapAsrPhase(phase), taskId, true,
            task.Revision, phase, task.Steps.Count, completed, failed, completed > 0,
            errors, null);
    }

    private async Task<NoteProcessingChainDto> DescribeDerivedAsync(Guid owner,
        NoteDerivedTaskState? task, bool hasAudio, NoteAsrState? asrTask,
        NoteMaterialVersionDto version, NoteProcessingRecord? processing,
        CancellationToken ct)
    {
        await Task.CompletedTask;
        var taskId = NoteProcessingIdentity.Task(owner, version.Upload.VersionId,
            NoteProcessingTaskKind.Derived);
        if (task is null)
        {
            var state = NoteProcessingChainState.PendingStart;
            return new(NoteProcessingTaskKind.Derived, state, taskId, false, null, null,
                0, 0, 0, false, [], processing?.PendingReason ?? "整理任务尚未创建");
        }

        var phase = NoteDerivedStore.DescribePhase(task);
        var errors = task.Issues.Select(issue => SafeError(issue.Message))
            .Concat(task.Steps.SelectMany(step => step.Attempts)
                .Where(attempt => attempt.Status is not ("Queued" or "Running" or "Succeeded"))
                .Select(attempt => attempt.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => SafeError(message!)))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        var completed = task.Steps.Count(step => step.Current.Status == "Succeeded");
        var failed = task.Steps.Count(step => step.Current.Status is not
            ("Queued" or "Running" or "Succeeded"));
        return new(NoteProcessingTaskKind.Derived, MapDerivedPhase(phase), taskId, true,
            task.Revision, phase, task.Steps.Count, completed, failed,
            completed > 0 || phase == "Succeeded", errors, null);
    }

    private async Task<IReadOnlyList<NoteProcessingArtifactDto>> ArtifactsAsync(Guid owner,
        NoteDerivedTaskState? task, CancellationToken ct)
    {
        if (task is null) return [];
        var result = new List<NoteProcessingArtifactDto>(2);
        foreach (var artifactId in new[] { task.CompleteRecordArtifactId,
                     task.IndependentAnalysisArtifactId }.Where(id => id != Guid.Empty))
        {
            try
            {
                var artifact = await derivedTasks.GetArtifactAsync(owner, artifactId, ct);
                result.Add(new(artifact.ArtifactId, artifact.Kind, artifact.HeadVersionId));
            }
            catch (DomainException error) when (error.ErrorCode == 40401)
            {
            }
        }
        return result;
    }

    private static NoteProcessingState OverallState(NoteMaterialVersionDto version,
        bool hasAudio, NoteProcessingChainDto asr, NoteProcessingChainDto derived,
        bool allRequiredCreated)
    {
        if (version.Upload.Phase == "Uploading")
            return NoteProcessingState.WaitingForMaterial;
        if (asr.State is NoteProcessingChainState.NeedsAttention or NoteProcessingChainState.Stopped ||
            derived.State is NoteProcessingChainState.NeedsAttention or NoteProcessingChainState.Stopped)
            return NoteProcessingState.NeedsAttention;
        var asrDone = !hasAudio || asr.State == NoteProcessingChainState.Succeeded;
        var derivedDone = derived.State == NoteProcessingChainState.Succeeded;
        if (asrDone && derivedDone) return NoteProcessingState.Completed;
        if (!allRequiredCreated && asr.State == NoteProcessingChainState.PendingStart &&
            derived.State == NoteProcessingChainState.PendingStart)
            return NoteProcessingState.PendingProcessing;
        if (hasAudio && asr.State == NoteProcessingChainState.Succeeded && !derivedDone)
            return NoteProcessingState.PartiallyCompleted;
        if (!hasAudio && derived.State == NoteProcessingChainState.PendingStart)
            return NoteProcessingState.PendingProcessing;
        return NoteProcessingState.Processing;
    }

    private static string? PendingReason(NoteMaterialVersionDto version,
        NoteProcessingRecord? state, bool autoEnabled, bool allRequiredCreated)
    {
        if (version.Upload.Phase == "Uploading")
            return "材料尚未完整，等待所有分块回执";
        if (!string.IsNullOrWhiteSpace(state?.PendingReason))
            return state.PendingReason;
        if (allRequiredCreated) return null;
        if (!autoEnabled && state?.StartedAt is null)
            return "自动笔记处理未开启；材料已完整，可在笔记处理页手动发起";
        return "材料已完整，处理任务尚未发起";
    }

    private static NoteProcessingChainState MapAsrPhase(string phase) => phase switch
    {
        "Queued" => NoteProcessingChainState.Queued,
        "Running" => NoteProcessingChainState.Running,
        "Succeeded" => NoteProcessingChainState.Succeeded,
        "Stopped" => NoteProcessingChainState.Stopped,
        "NeedsAttention" => NoteProcessingChainState.NeedsAttention,
        _ => NoteProcessingChainState.NeedsAttention
    };

    private static NoteProcessingChainState MapDerivedPhase(string phase) => phase switch
    {
        "WaitingForAsr" => NoteProcessingChainState.WaitingForAsr,
        "Queued" => NoteProcessingChainState.Queued,
        "Running" => NoteProcessingChainState.Running,
        "Succeeded" => NoteProcessingChainState.Succeeded,
        "Stopped" => NoteProcessingChainState.Stopped,
        "Blocked" or "NeedsAttention" => NoteProcessingChainState.NeedsAttention,
        _ => NoteProcessingChainState.NeedsAttention
    };

    private async Task<NoteAsrState?> TryReadAsrAsync(Guid owner, Guid taskId,
        CancellationToken ct)
    {
        try { return await asrTasks.ReadAsync(owner, taskId, ct); }
        catch (DomainException error) when (error.ErrorCode == 40401) { return null; }
    }

    private async Task<NoteDerivedTaskState?> TryReadDerivedAsync(Guid owner, Guid taskId,
        CancellationToken ct)
    {
        var state = await ReadDerivedRawAsync(owner, taskId, ct);
        if (state is not null && NoteDerivedStore.NeedsAsrRecovery(state))
        {
            await derivedJob.ReconcileAsync(owner, taskId, ct);
            state = await ReadDerivedRawAsync(owner, taskId, ct);
        }
        return state;
    }

    private async Task<NoteDerivedTaskState?> ReadDerivedRawAsync(Guid owner, Guid taskId,
        CancellationToken ct)
    {
        try { return await derivedTasks.ReadTaskAsync(owner, taskId, ct); }
        catch (DomainException error) when (error.ErrorCode == 40401) { return null; }
    }

    private static bool HasAudio(NoteMaterialVersionDto version)
    {
        using var document = JsonDocument.Parse(version.InputJson);
        return document.RootElement.GetProperty("recordings").GetArrayLength() > 0;
    }

    private static string SafeError(string message)
    {
        var redacted = System.Text.RegularExpressions.Regex.Replace(message,
            @"https?://[^\s""'<>]+", "[地址已隐藏]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
        redacted = Pim.Infrastructure.Ai.AiRedactor.RedactPlainText(redacted) ?? "";
        redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
            @"(?<![\w/])(?:/[^\s""'<>]+)+", "[路径已隐藏]",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(100));
        return redacted.Length <= 500 ? redacted : redacted[..500];
    }

    private Task<bool> AutoEnabledAsync(Guid owner, CancellationToken ct) =>
        db.NoteProcessingPreferences.AsNoTracking()
            .Where(x => x.OwnerId == owner)
            .Select(x => x.AutoNoteProcessingEnabled)
            .SingleOrDefaultAsync(ct);

    private Task<bool> IsActiveOwnerAsync(Guid owner, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct);

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        await RequireActiveOwnerAsync(owner, ct);
        return owner;
    }

    private async Task RequireActiveOwnerAsync(Guid owner, CancellationToken ct)
    {
        if (!await IsActiveOwnerAsync(owner, ct))
            throw new DomainException(40301, "账号已停用，不能发起或查看笔记处理");
    }
}
