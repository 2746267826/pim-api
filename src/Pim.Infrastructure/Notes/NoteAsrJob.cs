using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

public sealed class NoteAsrDispatchJob(NoteAsrTaskStore tasks, NoteAsrService asr,
    IBackgroundJobClient jobs, ILogger<NoteAsrDispatchJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var (owner, id) in tasks.Enumerate())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await tasks.ReadAsync(owner, id, ct);
                if (state.Route is not null)
                    state = await asr.EnsureAttemptForOwnerAsync(owner, id,
                        state.Request.VersionId, ct);
                if (!NoteAsrTaskStore.Active(state)) continue;
                using var lease = tasks.TryRunLease(owner, id);
                if (lease is not null) jobs.Enqueue<NoteAsrJob>(job => job.RunAsync(owner, id, CancellationToken.None));
            }
            catch (Exception error) when (error is IOException or DomainException or JsonException)
            { logger.LogWarning("An ASR checkpoint could not be dispatched ({ErrorType}); other tasks continue", error.GetType().Name); }
        }
    }
}

public sealed class NoteAsrJob(NoteAsrTaskStore tasks, NoteMaterialUploadStore materials,
    PimDbContext db, AiAsrCallService calls)
{
    private const int AdaptiveCallAllowance = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex TimedTranscriptLine = new(
        @"^(?<start>\d+(?:\.\d+)?)\s*[-–—]\s*(?<end>\d+(?:\.\d+)?)(?<rest>\s*\|.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid owner, Guid id, CancellationToken ct)
    {
        using var lease = tasks.TryRunLease(owner, id);
        if (lease is null) return;
        for (var processed = 0; processed < 64; processed++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await tasks.ReadAsync(owner, id, ct);
            if (!NoteAsrTaskStore.CloudPipeline(state)) return;
            if (state.Route is not null && state.AttemptId is null) return;
            if (state.AttemptId is { } routedAttempt)
            {
                var attempt = await db.NoteAsrAttempts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == routedAttempt && x.OwnerId == owner, ct);
                if (attempt is null || !CanDispatchAttempt(attempt, owner, id, state)) return;
            }
            if (state.AttemptId is { } routedAttemptId && await db.NoteAsrBatchJobs.AsNoTracking()
                    .AnyAsync(x => x.OwnerId == owner && x.AttemptId == routedAttemptId, ct))
                return;
            var step = state.Steps.FirstOrDefault(x => x.Current.Status == "Running")
                ?? (!state.StopRequested ? state.Steps.FirstOrDefault(x => x.Current.Status == "Queued") : null);
            if (step is null) return;
            var requestId = step.Current.RequestId;
            var executionEpoch = step.ExecutionEpoch;
            await AiCallRecovery.RecoverExpiredAsync(db, ct);
            var receipt = await db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(
                x => x.UserId == owner && x.ClientRequestId == requestId, ct);
            if (receipt is not null)
            {
                if (receipt.Purpose != "ai.asr-call" || receipt.SourceObjectId != id.ToString())
                {
                    await MarkAsync("Blocked", "请求标识已被另一条调用占用，请核对记录后明确恢复", true);
                    return;
                }
                if (state.Route is null && receipt.Status == "running") return;
                if (state.Route is null)
                {
                    var recovered = AiCallAudit.Result(receipt, true);
                    await RecordAsync(recovered);
                    if (recovered.Status is AiRequestStatus.Running or AiRequestStatus.Blocked) return;
                    continue;
                }
            }
            if (state.StopRequested)
            {
                await MarkAsync("Cancelled", "任务已停止，此片没有发送记录", false);
                continue;
            }
            if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            {
                await MarkAsync("Blocked", "发起账号已停用，任务已停止", true);
                return;
            }
            try
            {
                var version = await materials.GetAsync(owner, state.Request.VersionId, ct);
                if (NoteMaterialManifestValidator.Hash(version.InputJson) != state.InputSha256 ||
                    NoteMaterialManifestValidator.Hash(version.ManifestJson) != state.ManifestSha256)
                    throw new DomainException(40901, "固定材料已改变，任务已停止");
                if (state.AttemptId is not null)
                {
                    var usedCalls = await db.AiRequestLogs.AsNoTracking().CountAsync(x => x.UserId == owner &&
                        x.Purpose == "ai.asr-call" && x.SourceObjectType == "native-note-asr-task" &&
                        x.SourceObjectId == id.ToString(), ct);
                    var remaining = checked(state.Steps.Count + AdaptiveCallAllowance - usedCalls);
                    if (remaining <= 0)
                    {
                        await MarkAsync("Unknown", "自动分片重试总调用预算已用尽，请核对调用记录后人工处理", true);
                        return;
                    }
                    var adaptive = await RunAdaptiveAsync(owner, id, state, step,
                        Math.Min(NoteAsrAdaptiveOptions.Default.MaxCalls, remaining), executionEpoch, ct);
                    var latest = await tasks.ReadAsync(owner, id, ct);
                    if (latest.AttemptId != state.AttemptId ||
                        !EpochMatches(latest, step.Index, executionEpoch)) return;
                    if (adaptive.Status == NoteAsrAdaptiveRunStatus.Succeeded)
                    {
                        await RecordAdaptiveSuccessAsync(owner, id, state, step, adaptive,
                            executionEpoch, ct);
                        continue;
                    }
                    await RecordAdaptiveFailureAsync(owner, id, state.AttemptId!.Value, step, adaptive,
                        executionEpoch, ct);
                    return;
                }
                byte[] bytes;
                try
                {
                    bytes = await materials.ReadAudioSliceAsync(owner, state.Request.VersionId,
                        step.FileIndex, step.SourceOffset, step.Samples, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or
                    UnauthorizedAccessException)
                {
                    await MarkAsync("Blocked",
                        "本地音频输入读取失败，未发送至转写服务；请核对材料后显式重试", true);
                    return;
                }
                if ((await tasks.ReadAsync(owner, id, ct)).StopRequested)
                {
                    await MarkAsync("Cancelled", "任务已停止，此片未发送", false);
                    return;
                }
                await tasks.UpdateAsync(owner, id, current =>
                {
                    if (!EpochMatches(current, step.Index, executionEpoch))
                        throw new StaleExecutionException();
                    if (!current.StopRequested) current.Steps[step.Index].Current.Status = "Running";
                }, ct);
                await SyncCloudAttemptAsync(owner, id, ct, step.Index, executionEpoch);
                var request = new AiAsrCallRequest(requestId, owner,
                    new($"segment-{step.Index + 1:D5}.wav", "wav", Convert.ToBase64String(bytes)),
                    state.Request.Consent, state.Request.Language, state.Request.Consent.ModelId);
                var result = await calls.CallForOwnerAsync(owner, request, id, ct);
                await RecordAsync(result);
                if (result.Status is AiRequestStatus.Running or AiRequestStatus.Blocked) return;
            }
            catch (OperationCanceledException) { throw; }
            catch (StaleExecutionException) { return; }
            catch (DomainException error)
            {
                if (error.ErrorCode == 50302)
                {
                    await MarkAsync("Queued", error.Message, false);
                    return;
                }
                await MarkAsync(error.ErrorCode == 50301 ? "Unknown" : "Blocked",
                    error.ErrorCode == 50301 ? "调用结果未确认保存，请核对调用记录后明确重试" : error.Message, true);
                return;
            }
            catch (Exception error) when (error is IOException or DbUpdateException)
            {
                await MarkAsync("Unknown", "材料或检查点读写中断，请核对调用记录；任务已停止", true);
                return;
            }

            async Task RecordAsync(AiTextCallResultDto result)
            {
                try
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await tasks.UpdateAsync(owner, id, current =>
                    {
                        if (!EpochMatches(current, step.Index, executionEpoch))
                            throw new StaleExecutionException();
                        var attempt = current.Steps[step.Index].Current;
                        if (attempt.RequestId != requestId)
                            throw new StaleExecutionException();
                        attempt.LogId = result.LogId;
                        attempt.Status = result.Status.ToString();
                        attempt.ErrorMessage = result.ErrorMessage;
                        current.StopRequested |= result.Status == AiRequestStatus.Blocked;
                        current.CompletionAcknowledged |= current.Route is not null &&
                            current.Steps.All(x => x.Current.Status == "Succeeded");
                    }, budget.Token);
                    await SyncCloudAttemptAsync(owner, id, budget.Token, step.Index, executionEpoch);
                }
                catch (StaleExecutionException) { }
            }

            async Task MarkAsync(string status, string message, bool stop)
            {
                try
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await tasks.UpdateAsync(owner, id, current =>
                    {
                        if (!EpochMatches(current, step.Index, executionEpoch))
                            throw new StaleExecutionException();
                        var attempt = current.Steps[step.Index].Current;
                        attempt.Status = status;
                        attempt.ErrorMessage = message;
                        current.StopRequested |= stop;
                    }, budget.Token);
                    await SyncCloudAttemptAsync(owner, id, budget.Token, step.Index, executionEpoch);
                }
                catch (StaleExecutionException) { }
            }
        }
    }

    private async Task<NoteAsrAdaptiveRun> RunAdaptiveAsync(Guid owner, Guid taskId, NoteAsrState rootState,
        NoteAsrStep rootStep, int maxCalls, Guid? executionEpoch, CancellationToken ct)
    {
        var options = NoteAsrAdaptiveOptions.Default with { MaxCalls = maxCalls };
        var preparedAudio = new Dictionary<Guid, byte[]>();
        var engine = new NoteAsrAdaptiveTranscription(
            options,
            async (request, token) =>
            {
                var current = await tasks.ReadAsync(owner, taskId, token);
                if (current.StopRequested) throw new OperationCanceledException(token);
                if (current.AttemptId != rootState.AttemptId)
                    throw new DomainException(40901, "任务执行快照已变化");
                if (!EpochMatches(current, rootStep.Index, executionEpoch))
                    throw new StaleExecutionException();
                var version = await materials.GetAsync(owner, rootState.Request.VersionId, token);
                if (NoteMaterialManifestValidator.Hash(version.InputJson) != rootState.InputSha256 ||
                    NoteMaterialManifestValidator.Hash(version.ManifestJson) != rootState.ManifestSha256)
                    throw new DomainException(40901, "固定材料已改变，本片未发送");
                var start = checked(rootStep.SourceOffset + request.Range.OffsetSamples);
                if (start < rootStep.SourceOffset ||
                    checked(start + request.Range.Samples) > rootStep.SourceOffset + rootStep.Samples)
                    throw new DomainException(40901, "本地音频范围超出固定分片，本片未发送");
                var bytes = await materials.ReadAudioSliceAsync(owner, rootState.Request.VersionId,
                    rootStep.FileIndex, start, request.Range.Samples, token);
                current = await tasks.ReadAsync(owner, taskId, token);
                if (current.StopRequested) throw new OperationCanceledException(token);
                if (current.AttemptId != rootState.AttemptId)
                    throw new DomainException(40901, "任务执行快照已变化");
                if (!EpochMatches(current, rootStep.Index, executionEpoch))
                    throw new StaleExecutionException();
                await tasks.UpdateAsync(owner, taskId, state =>
                {
                    if (state.AttemptId != rootState.AttemptId || state.StopRequested)
                        throw new DomainException(40901, "任务执行快照或停止状态已变化");
                    if (!EpochMatches(state, rootStep.Index, executionEpoch))
                        throw new StaleExecutionException();
                    var attempt = LocateAttempt(state.Steps, rootStep.Index, request.ChildRequestId);
                    if (attempt is null)
                    {
                        var parent = request.ParentRequestId is { } parentId
                            ? LocateAttempt(state.Steps, rootStep.Index, parentId)
                            : null;
                        if (parent is null)
                            throw new InvalidDataException("Adaptive ASR parent attempt is missing");
                        attempt = new NoteAsrAttempt
                        {
                            RequestId = request.ChildRequestId,
                            SourceOffsetSamples = checked(rootStep.SourceOffset + request.Range.OffsetSamples),
                            Samples = request.Range.Samples,
                            AdaptiveDepth = request.Depth,
                            AdaptiveBranch = request.Branch
                        };
                        parent.Children.Add(attempt);
                    }
                    if (attempt.Status is not ("Queued" or "Running"))
                        throw new DomainException(40901, "Adaptive ASR attempt 已处于不可重发状态");
                    attempt.Status = "Running";
                    attempt.ErrorMessage = null;
                }, token);
                preparedAudio[request.ChildRequestId] = bytes;
            },
            async (request, token) =>
            {
                var current = await tasks.ReadAsync(owner, taskId, token);
                if (!EpochMatches(current, rootStep.Index, executionEpoch))
                    return new(NoteAsrAdaptiveOutcome.Cancelled, ReasonCode: "execution_superseded");
                var attempt = LocateAttempt(current.Steps, rootStep.Index, request.ChildRequestId);
                if (attempt is null) return null;
                if (attempt.LogId is not { } logId)
                    return attempt.Status is "Running" or "Queued" ? null : TerminalWithoutLog(attempt);
                var row = await db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == logId &&
                    x.UserId == owner && x.Purpose == "ai.asr-call" &&
                    x.SourceObjectId == taskId.ToString() && x.ClientRequestId == request.ChildRequestId, token);
                return row is null ? TerminalWithoutLog(attempt) : Classify(row, attempt);
            },
            async (request, token) =>
                await CallAdaptiveAsync(owner, taskId, rootState, rootStep, request, preparedAudio,
                    executionEpoch, token));
        return await engine.RunAsync(rootStep.Current.RequestId,
            new NoteAsrAdaptiveRange(0, rootStep.Samples, NoteAsrPlan.SampleRate), ct);
    }

    private async ValueTask<NoteAsrAdaptiveResult> CallAdaptiveAsync(Guid owner, Guid taskId,
        NoteAsrState rootState, NoteAsrStep rootStep, NoteAsrAdaptiveRequest request,
        Dictionary<Guid, byte[]> preparedAudio, Guid? executionEpoch, CancellationToken ct)
    {
        NoteAsrState current;
        NoteAsrAttemptEntity? routedAttempt;
        try
        {
            current = await tasks.ReadAsync(owner, taskId, ct);
            if (current.AttemptId != rootState.AttemptId ||
                !EpochMatches(current, rootStep.Index, executionEpoch))
            {
                // A retry can happen after preparation marked the child Running. The retry
                // supersedes the open old attempt tree, so the stale pass must neither call the
                // provider nor write into the new step.
                return new(NoteAsrAdaptiveOutcome.Cancelled, ReasonCode: "attempt_changed");
            }
            if (current.StopRequested)
            {
                await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                    "Cancelled", "任务已停止，此调用未发送", null, executionEpoch,
                    CancellationToken.None);
                return new(NoteAsrAdaptiveOutcome.Cancelled, ReasonCode: "stop_requested");
            }
            routedAttempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == rootState.AttemptId && x.OwnerId == owner, ct);
        }
        catch (OperationCanceledException)
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Cancelled", "任务已停止或调用取消，此调用未发送", null, executionEpoch,
                CancellationToken.None);
            throw;
        }
        if (!preparedAudio.Remove(request.ChildRequestId, out var bytes))
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Blocked", "本地音频输入尚未准备完成，未发送至转写服务", null, executionEpoch,
                CancellationToken.None);
            return new(NoteAsrAdaptiveOutcome.PreparationFailed,
                ReasonCode: "input_not_prepared", Reason: "本地音频输入尚未准备完成");
        }
        if (routedAttempt is null)
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Blocked", "ASR 执行快照不存在，本片未发送", null, executionEpoch,
                CancellationToken.None);
            return new(NoteAsrAdaptiveOutcome.ConfigurationRejected,
                ReasonCode: "attempt_missing", Reason: "ASR 执行快照不存在");
        }
        if (!CanDispatchAttempt(routedAttempt, owner, taskId, rootState))
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Blocked", "ASR 执行快照已停止或处于不可执行状态，本片未发送", null,
                executionEpoch, CancellationToken.None);
            return new(NoteAsrAdaptiveOutcome.ConfigurationRejected,
                ReasonCode: "attempt_not_dispatchable",
                Reason: "ASR 执行快照已停止或处于不可执行状态");
        }
        if (string.IsNullOrWhiteSpace(routedAttempt.PromptRendered) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptSha256) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptRevision) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptTemplateSha256) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptTemplateRevision))
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Blocked", "ASR 提示词快照缺失，本片未发送", null, executionEpoch,
                CancellationToken.None);
            return new(NoteAsrAdaptiveOutcome.ConfigurationRejected,
                ReasonCode: "prompt_snapshot_missing", Reason: "ASR 提示词快照缺失");
        }
        var prompt = new AiAsrPromptSnapshot(routedAttempt.PromptRendered,
            rootState.Request.Language, routedAttempt.PromptSha256, routedAttempt.PromptRevision,
            routedAttempt.PromptTemplateSha256, routedAttempt.PromptTemplateRevision);
        var callRequest = new AiAsrCallRequest(request.ChildRequestId, owner,
            new($"segment-{rootStep.Index + 1:D5}-{SafeBranch(request.Branch)}.wav", "wav",
                Convert.ToBase64String(bytes)), rootState.Request.Consent, rootState.Request.Language,
            rootState.Request.Consent.ModelId, prompt);
        AiTextCallResultDto result;
        try
        {
            var latest = await tasks.ReadAsync(owner, taskId, ct);
            if (!EpochMatches(latest, rootStep.Index, executionEpoch))
                return new(NoteAsrAdaptiveOutcome.Cancelled,
                    ReasonCode: "execution_superseded");
            result = await calls.CallForOwnerAsync(owner, callRequest, taskId, ct);
        }
        catch (OperationCanceledException)
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Unknown", "ASR 调用取消，未确认供应商是否处理；不会自动重发", null,
                executionEpoch, CancellationToken.None);
            throw;
        }
        catch (DomainException error)
        {
            var busy = error.ErrorCode == 50302;
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                busy ? "Queued" : "Blocked", error.Message, null, executionEpoch,
                CancellationToken.None);
            return busy
                ? new(NoteAsrAdaptiveOutcome.TransientFailure, ReasonCode: "admission_busy", Reason: error.Message)
                : new(NoteAsrAdaptiveOutcome.ConfigurationRejected,
                    ReasonCode: "provider_configuration_or_admission_rejected", Reason: error.Message);
        }
        await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
            result.Status.ToString(), result.ErrorMessage, result.LogId, executionEpoch,
            CancellationToken.None);
        var row = await db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == result.LogId &&
            x.UserId == owner && x.Purpose == "ai.asr-call" && x.SourceObjectId == taskId.ToString(), ct);
        return Classify(row, null);
    }

    private async Task PersistAdaptiveAttemptAsync(Guid owner, Guid taskId, int segmentIndex,
        Guid requestId, string status, string? error, Guid? logId, Guid? executionEpoch,
        CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await tasks.UpdateAsync(owner, taskId, state =>
        {
            if (!EpochMatches(state, segmentIndex, executionEpoch))
                throw new StaleExecutionException();
            var attempt = LocateAttempt(state.Steps, segmentIndex, requestId)
                ?? throw new InvalidDataException("Adaptive ASR child attempt is missing");
            attempt.Status = status;
            attempt.ErrorMessage = error;
            attempt.LogId = logId ?? attempt.LogId;
        }, linked.Token);
    }

    private static bool CanDispatchAttempt(NoteAsrAttemptEntity attempt, Guid owner, Guid taskId,
        NoteAsrState state) =>
        attempt.OwnerId == owner &&
        attempt.TaskId == taskId &&
        attempt.VersionId == state.Request.VersionId &&
        attempt.InputSha256 == state.InputSha256 &&
        attempt.Route == state.Route &&
        !attempt.StopRequested &&
        attempt.State is ("Queued" or "Running");

    private async Task RecordAdaptiveSuccessAsync(Guid owner, Guid taskId, NoteAsrState state,
        NoteAsrStep step, NoteAsrAdaptiveRun run, Guid? executionEpoch, CancellationToken ct)
    {
        if (run.Status != NoteAsrAdaptiveRunStatus.Succeeded || run.Successes.Count == 0)
            throw new InvalidDataException("Adaptive ASR success requires complete ordered transcript coverage");
        var transcript = ComposeAdaptiveTranscript(run.Successes, NoteAsrPlan.SampleRate);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidDataException("Adaptive ASR success has no visible transcript");
        var attemptEntity = state.AttemptId is { } id
            ? await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == id && x.OwnerId == owner, ct)
            : null;
        if (attemptEntity is null)
            throw new DomainException(40901, "Adaptive ASR 缺少固定执行快照");
        var rootLogId = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == owner && x.ClientRequestId == step.Current.RequestId &&
                x.Purpose == "ai.asr-call" && x.SourceObjectId == taskId.ToString())
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        var coverage = JsonSerializer.Serialize(new
        {
            recordFormat = "adaptive-transcription-v1",
            rootRequestId = step.Current.RequestId,
            executionEpoch = executionEpoch,
            promptRevision = attemptEntity.PromptRevision,
            promptSha256 = attemptEntity.PromptSha256,
            children = run.Successes.Select(x => new
            {
                requestId = x.Request.ChildRequestId,
                offsetSamples = x.Request.Range.OffsetSamples,
                samples = x.Request.Range.Samples,
                textSha256 = Sha256(x.Text)
            }).ToArray()
        }, Json);
        var resultSha = Sha256(transcript);
        var providerHandle = NoteAsrAcceptedEvidence.AdaptiveProviderHandle(step.Current.RequestId);
        var evidenceId = Guid.Empty;
        // Retry uses the same account-scoped task lock. Holding it across the epoch check,
        // evidence insert and checkpoint update makes stale adaptive success either fully win
        // before the retry or observe the retry and write nothing. The checkpoint is the only
        // commit point: if its write fails, the just-inserted row is compensated so a later
        // retry cannot be blocked by evidence that no checkpoint ever accepted.
        var recorded = await tasks.WithLockAsync(owner, async lockedCt =>
        {
            var current = await tasks.ReadAsync(owner, taskId, lockedCt);
            if (!EpochMatches(current, step.Index, executionEpoch))
                throw new StaleExecutionException();
            if (current.AttemptId != attemptEntity.Id || current.StopRequested) return false;
            var existing = await db.NoteAsrSegmentEvidence.SingleOrDefaultAsync(x => x.OwnerId == owner &&
                x.AttemptId == attemptEntity.Id && x.FencingToken == attemptEntity.FencingToken &&
                x.SegmentIndex == step.Index && x.ReceiptKind == "adaptive", lockedCt);
            var referenced = existing is not null &&
                CheckpointReferencesEvidence(current, step.Index, existing.Id);
            var created = false;
            if (existing is not null &&
                !AdaptiveEvidenceMatches(existing, providerHandle, resultSha, coverage))
            {
                // A previous process may have committed evidence and then failed before the
                // checkpoint, so nothing consumes it. Only a referenced row is immutable.
                if (referenced)
                    throw new DomainException(40901, "同一分片已有不同自适应成功证据，原记录保留");
                db.NoteAsrSegmentEvidence.Remove(existing);
                await db.SaveChangesAsync(lockedCt);
                existing = null;
            }
            if (existing is null)
            {
                var candidate = NewAdaptiveEvidence(owner, taskId, attemptEntity, step,
                    providerHandle, transcript, resultSha, coverage);
                db.NoteAsrSegmentEvidence.Add(candidate);
                try
                {
                    await db.SaveChangesAsync(lockedCt);
                    existing = candidate;
                    created = true;
                }
                catch (DbUpdateException error) when (IsUniqueViolation(error))
                {
                    db.Entry(candidate).State = EntityState.Detached;
                    existing = await db.NoteAsrSegmentEvidence.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.OwnerId == owner && x.AttemptId == attemptEntity.Id &&
                        x.FencingToken == attemptEntity.FencingToken && x.SegmentIndex == step.Index &&
                        x.ReceiptKind == "adaptive", lockedCt)
                        ?? throw new DomainException(40901, "同一分片已有不同自适应成功证据，原记录保留");
                    referenced = CheckpointReferencesEvidence(current, step.Index, existing.Id);
                    if (!AdaptiveEvidenceMatches(existing, providerHandle, resultSha, coverage))
                    {
                        if (referenced)
                            throw new DomainException(40901,
                                "同一分片已有不同自适应成功证据，原记录保留");
                        db.NoteAsrSegmentEvidence.Remove(existing);
                        await db.SaveChangesAsync(lockedCt);
                        var replacement = NewAdaptiveEvidence(owner, taskId, attemptEntity, step,
                            providerHandle, transcript, resultSha, coverage);
                        db.NoteAsrSegmentEvidence.Add(replacement);
                        await db.SaveChangesAsync(lockedCt);
                        existing = replacement;
                        created = true;
                    }
                }
            }
            evidenceId = existing.Id;
            try
            {
                await tasks.UpdateLockedAsync(owner, taskId, latest =>
                {
                    if (!EpochMatches(latest, step.Index, executionEpoch))
                        throw new StaleExecutionException();
                    if (latest.AttemptId != attemptEntity.Id || latest.StopRequested) return;
                    var target = latest.Steps[step.Index].Current;
                    target.Status = "Succeeded";
                    target.ErrorMessage = null;
                    target.LogId = rootLogId;
                    target.EvidenceId = evidenceId;
                    latest.CompletionAcknowledged = latest.Steps.All(x => x.Current.Status == "Succeeded");
                }, lockedCt);
            }
            catch
            {
                if (created || !referenced)
                    await TryDeleteUncommittedEvidenceAsync(owner, evidenceId);
                throw;
            }
            return true;
        }, ct);
        if (!recorded || evidenceId == Guid.Empty)
        {
            // A stop or attempt change prevents the step checkpoint, but the durable attempt
            // still needs to leave StopAttemptAsync's non-terminal Unknown state.
            await SyncCloudAttemptAsync(owner, taskId, ct, step.Index, executionEpoch);
            return;
        }
        var completed = await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x =>
                x.OwnerId == owner && x.AttemptId == attemptEntity.Id &&
                x.FencingToken == attemptEntity.FencingToken)
            .Select(x => x.SegmentIndex).Distinct().CountAsync(ct);
        await UpdateAttemptCountersAsync(owner, attemptEntity, completed, ct);
        await SyncCloudAttemptAsync(owner, taskId, ct, step.Index, executionEpoch);
    }

    /// <summary>
    /// Atomically advances the frozen attempt for an adaptive success, guarding route, version,
    /// fence and a concurrent stop. Non-relational test providers use an explicit tracked branch.
    /// </summary>
    private async Task UpdateAttemptCountersAsync(Guid owner, NoteAsrAttemptEntity attempt, int completed,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await db.NoteAsrAttempts.Where(x => x.Id == attempt.Id && x.OwnerId == owner &&
                    x.Route == attempt.Route && x.VersionId == attempt.VersionId &&
                    x.FencingToken == attempt.FencingToken && !x.StopRequested &&
                    x.State != "Succeeded" && x.State != "Cancelled" && x.State != "Failed")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CompletedSegments, completed)
                    .SetProperty(x => x.State, x => x.State == "Queued" ? "Running" : x.State)
                    .SetProperty(x => x.LastErrorCode, (string?)null)
                    .SetProperty(x => x.LastErrorMessage, (string?)null)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return;
        }
        var tracked = await db.NoteAsrAttempts.SingleOrDefaultAsync(x => x.Id == attempt.Id && x.OwnerId == owner, ct);
        if (tracked is null || tracked.Route != attempt.Route || tracked.VersionId != attempt.VersionId ||
            tracked.FencingToken != attempt.FencingToken || tracked.StopRequested ||
            tracked.State is "Succeeded" or "Cancelled" or "Failed") return;
        tracked.CompletedSegments = completed;
        if (tracked.State == "Queued") tracked.State = "Running";
        tracked.LastErrorCode = null; tracked.LastErrorMessage = null;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordAdaptiveFailureAsync(Guid owner, Guid taskId, Guid expectedAttemptId,
        NoteAsrStep step, NoteAsrAdaptiveRun run, Guid? executionEpoch, CancellationToken ct)
    {
        if (run.StopResult?.Outcome == NoteAsrAdaptiveOutcome.TransientFailure)
        {
            await MarkAsync("Queued", run.StopResult.Reason ?? "ASR并发已满，本片保持排队", false);
            return;
        }
        var (status, message) = run.StopResult?.Outcome switch
        {
            NoteAsrAdaptiveOutcome.Cancelled =>
                ("Cancelled", "ASR 调用已取消；供应商是否处理未确认，不会自动重发"),
            NoteAsrAdaptiveOutcome.ConfigurationRejected or NoteAsrAdaptiveOutcome.ConsentRejected =>
                ("Blocked", "ASR 配置、目标授权或同意已拒绝，本片未继续发送"),
            NoteAsrAdaptiveOutcome.PreparationFailed =>
                ("Blocked", "本地音频输入准备失败，未发送至转写服务；请核对材料后显式重试"),
            NoteAsrAdaptiveOutcome.UnconfirmedTimeout or NoteAsrAdaptiveOutcome.Unknown =>
                ("Unknown", "ASR 结果未确认；已保留已有成功子片和真实记录，请人工核对"),
            _ => ("Failed", "自动分片重试未能覆盖完整原分片；已成功子片和真实记录保留")
        };
        var detail = run.StopResult?.Outcome == NoteAsrAdaptiveOutcome.PreparationFailed
            ? null
            : run.PartialReason ?? run.StopResult?.Reason;
        await MarkAsync(status, string.IsNullOrWhiteSpace(detail) ? message : message + "：" + detail, true);

        async Task MarkAsync(string nextStatus, string error, bool stop)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            await tasks.UpdateAsync(owner, taskId, current =>
            {
                if (!EpochMatches(current, step.Index, executionEpoch))
                    throw new StaleExecutionException();
                if (current.AttemptId != expectedAttemptId) return;
                if (current.Steps.Count <= step.Index) return;
                var target = current.Steps[step.Index].Current;
                target.Status = nextStatus;
                target.ErrorMessage = error;
                current.StopRequested |= stop;
                current.CompletionAcknowledged = false;
            }, linked.Token);
            await SyncCloudAttemptAsync(owner, taskId, linked.Token, step.Index, executionEpoch);
        }
    }

    private static NoteAsrAttempt? LocateAttempt(IReadOnlyList<NoteAsrStep> steps, int segmentIndex, Guid requestId)
    {
        if (segmentIndex < 0 || segmentIndex >= steps.Count) return null;
        NoteAsrAttempt? Find(NoteAsrAttempt attempt)
        {
            if (attempt.RequestId == requestId) return attempt;
            foreach (var child in attempt.Children)
                if (Find(child) is { } match) return match;
            return null;
        }
        foreach (var attempt in steps[segmentIndex].Attempts)
            if (Find(attempt) is { } match) return match;
        return null;
    }

    private static NoteAsrAdaptiveResult TerminalWithoutLog(NoteAsrAttempt attempt) =>
        attempt.Status switch
        {
            "Cancelled" => new(NoteAsrAdaptiveOutcome.Cancelled, ReasonCode: "cancelled_without_receipt"),
            _ => new(NoteAsrAdaptiveOutcome.Unknown, ReasonCode: "attempt_without_receipt")
        };

    private static NoteAsrAdaptiveResult Classify(AiRequestLogEntity row, NoteAsrAttempt? attempt)
    {
        if (row.Status == "running")
            return new(NoteAsrAdaptiveOutcome.Unknown, ReasonCode: "call_still_running");
        if (row.Status == "succeeded")
            return new(NoteAsrAdaptiveOutcome.Succeeded, row.ResponseText);
        if (row.Status == "incomplete")
        {
            var reason = row.FinishReason ?? "";
            if (reason.Contains("truncation", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase))
                return new(NoteAsrAdaptiveOutcome.Truncated, ReasonCode: "provider_truncation");
            if (reason.Contains("repetition", StringComparison.OrdinalIgnoreCase))
                return new(NoteAsrAdaptiveOutcome.Repetition, ReasonCode: "provider_repetition");
            if (reason.Contains("empty", StringComparison.OrdinalIgnoreCase))
                return new(NoteAsrAdaptiveOutcome.Empty, ReasonCode: "provider_empty");
            return new(NoteAsrAdaptiveOutcome.Unknown, ReasonCode: row.ErrorCode ?? "incomplete_unconfirmed");
        }
        if (row.Status is "timed_out" or "cancelled")
            return new(NoteAsrAdaptiveOutcome.UnconfirmedTimeout, ReasonCode: row.ErrorCode ?? row.Status);
        if (row.Status == "blocked" || row.ErrorCode == "configuration_unavailable" ||
            row.ErrorCode is "upstream_http_400" or "upstream_http_401" or
                "upstream_http_403" or "upstream_http_404" or "upstream_http_422")
            return new(NoteAsrAdaptiveOutcome.ConfigurationRejected, ReasonCode: row.ErrorCode ?? "blocked");
        return new(NoteAsrAdaptiveOutcome.Unknown, ReasonCode: row.ErrorCode ?? attempt?.Status ?? row.Status);
    }

    private static string SafeBranch(string branch) =>
        new(branch.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static string ComposeAdaptiveTranscript(IReadOnlyList<NoteAsrAdaptiveSuccess> successes, int sampleRate)
    {
        var blocks = new List<string>(successes.Count);
        foreach (var success in successes.OrderBy(x => x.Request.Range.OffsetSamples))
        {
            var offsetSeconds = success.Request.Range.OffsetSamples / (double)sampleRate;
            var lines = success.Text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            blocks.Add(string.Join("\n", lines.Select(line => OffsetTimestamp(line, offsetSeconds))));
        }
        return string.Join("\n", blocks.Where(x => x.Length > 0));
    }

    private static string OffsetTimestamp(string line, double offsetSeconds)
    {
        var match = TimedTranscriptLine.Match(line);
        if (!match.Success ||
            !double.TryParse(match.Groups["start"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(match.Groups["end"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var end))
            return line;
        return string.Create(CultureInfo.InvariantCulture,
            $"{start + offsetSeconds:0.###}-{end + offsetSeconds:0.###}{match.Groups["rest"].Value}");
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool AdaptiveEvidenceMatches(NoteAsrSegmentEvidenceEntity row,
        string providerHandle, string resultSha, string coverage) =>
        row.ProviderHandle == providerHandle && row.ResultSha256 == resultSha &&
        string.Equals(row.CoverageJson, coverage, StringComparison.Ordinal);

    private static NoteAsrSegmentEvidenceEntity NewAdaptiveEvidence(Guid owner, Guid taskId,
        NoteAsrAttemptEntity attempt, NoteAsrStep step, string providerHandle, string transcript,
        string resultSha, string coverage) => new()
    {
        OwnerId = owner, AttemptId = attempt.Id, TaskId = taskId, VersionId = attempt.VersionId,
        Route = attempt.Route, FencingToken = attempt.FencingToken, SegmentIndex = step.Index,
        RecordingId = step.RecordingId, RecordingSegment = step.RecordingSegment,
        ChunkIndex = step.ChunkIndex, SourceFileIndex = step.FileIndex,
        SourceSha256 = step.Sha256, SourceOffsetSamples = step.SourceOffset, Samples = step.Samples,
        ReceiptKind = "adaptive", ProviderHandle = providerHandle, ResultText = transcript,
        ResultSha256 = resultSha, CoverageJson = coverage
    };

    private static bool CheckpointReferencesEvidence(NoteAsrState state, int stepIndex, Guid evidenceId) =>
        stepIndex >= 0 && stepIndex < state.Steps.Count &&
        state.Steps[stepIndex].Current.EvidenceId == evidenceId;

    /// <summary>
    /// Best-effort compensation for a failed checkpoint write. Leaving the row behind is safe for
    /// readers (acceptance requires the checkpoint reference), but deleting it lets a later retry
    /// reuse the same attempt/fence/segment unique slot without waiting for recovery.
    /// </summary>
    private async Task TryDeleteUncommittedEvidenceAsync(Guid owner, Guid evidenceId)
    {
        try
        {
            var row = db.NoteAsrSegmentEvidence.Local.FirstOrDefault(x =>
                    x.OwnerId == owner && x.Id == evidenceId)
                ?? await db.NoteAsrSegmentEvidence.FirstOrDefaultAsync(x =>
                    x.OwnerId == owner && x.Id == evidenceId, CancellationToken.None);
            if (row is null) return;
            db.NoteAsrSegmentEvidence.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception error) when (error is DbUpdateException or InvalidOperationException)
        {
            // The original checkpoint failure is the actionable error. A leftover row is ignored
            // by readers and replaced on the next adaptive success for this segment.
        }
    }

    private static bool IsUniqueViolation(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" }) return true;
            if (current.GetType().Name == "SqliteException" &&
                current.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool EpochMatches(NoteAsrState state, int stepIndex, Guid? epoch) =>
        stepIndex >= 0 && stepIndex < state.Steps.Count &&
        state.Steps[stepIndex].ExecutionEpoch == epoch;

    private sealed class StaleExecutionException : Exception { }

    private async Task SyncCloudAttemptAsync(Guid owner, Guid taskId, CancellationToken ct,
        int? stepIndex = null, Guid? executionEpoch = null)
    {
        var state = await tasks.ReadAsync(owner, taskId, ct);
        if (stepIndex is { } index && !EpochMatches(state, index, executionEpoch)) return;
        if (state.AttemptId is not { } attemptId) return;
        var completed = state.Steps.Count(x => x.Current.Status == "Succeeded");
        var failed = state.Steps.Count(x => x.Current.Status is not ("Queued" or "Running" or "Succeeded"));
        var terminal = state.Steps.Count > 0 && completed == state.Steps.Count;
        var stopped = state.StopRequested;
        var nextState = terminal ? "Succeeded" : stopped ? "Failed" : "Running";
        // CAS against the exact observed attempt row: a concurrent stop, retry convergence,
        // new attempt version or terminal transition must not be overwritten by this stale pass.
        var observed = await db.NoteAsrAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct);
        if (observed is null) return;
        // A stop that landed after this pass read the task file is never cleared by the stale pass.
        if (observed.StopRequested && !stopped) return;
        if (db.Database.IsRelational())
        {
            await db.NoteAsrAttempts.Where(x => x.Id == attemptId && x.OwnerId == owner &&
                    x.Route == observed.Route && x.VersionId == observed.VersionId &&
                    x.FencingToken == observed.FencingToken && x.StopRequested == observed.StopRequested &&
                    x.UpdatedAt == observed.UpdatedAt &&
                    x.State != "Succeeded" && x.State != "Cancelled")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CompletedSegments, completed)
                    .SetProperty(x => x.FailedSegments, failed)
                    .SetProperty(x => x.State, nextState)
                    .SetProperty(x => x.StopRequested, stopped)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return;
        }
        // Explicit non-relational branch for in-memory test providers; production always uses the
        // single-statement CAS above.
        var tracked = await db.NoteAsrAttempts.SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct);
        if (tracked is null || tracked.StopRequested && !stopped ||
            tracked.UpdatedAt != observed.UpdatedAt ||
            tracked.State is "Succeeded" or "Cancelled") return;
        tracked.CompletedSegments = completed;
        tracked.FailedSegments = failed;
        tracked.State = nextState;
        tracked.StopRequested = stopped;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
