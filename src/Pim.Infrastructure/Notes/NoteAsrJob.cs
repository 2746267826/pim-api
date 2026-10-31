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

public sealed class NoteAsrDispatchJob(NoteAsrTaskStore tasks, IBackgroundJobClient jobs, ILogger<NoteAsrDispatchJob> logger)
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
            if (state.AttemptId is { } routedAttempt && await db.NoteAsrBatchJobs.AsNoTracking()
                    .AnyAsync(x => x.OwnerId == owner && x.AttemptId == routedAttempt, ct))
                return;
            var step = state.Steps.FirstOrDefault(x => x.Current.Status == "Running")
                ?? (!state.StopRequested ? state.Steps.FirstOrDefault(x => x.Current.Status == "Queued") : null);
            if (step is null) return;
            var requestId = step.Current.RequestId;
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
                        Math.Min(NoteAsrAdaptiveOptions.Default.MaxCalls, remaining), ct);
                    if (adaptive.Status == NoteAsrAdaptiveRunStatus.Succeeded)
                    {
                        await RecordAdaptiveSuccessAsync(owner, id, state, step, adaptive, ct);
                        continue;
                    }
                    await RecordAdaptiveFailureAsync(owner, id, step, adaptive, ct);
                    return;
                }
                await tasks.UpdateAsync(owner, id, current =>
                {
                    if (!current.StopRequested) current.Steps[step.Index].Current.Status = "Running";
                }, ct);
                await SyncCloudAttemptAsync(owner, id, ct);
                var bytes = await materials.ReadAudioSliceAsync(owner, state.Request.VersionId,
                    step.FileIndex, step.SourceOffset, step.Samples, ct);
                if ((await tasks.ReadAsync(owner, id, ct)).StopRequested)
                {
                    await MarkAsync("Cancelled", "任务已停止，此片未发送", false);
                    return;
                }
                var request = new AiAsrCallRequest(requestId, owner,
                    new($"segment-{step.Index + 1:D5}.wav", "wav", Convert.ToBase64String(bytes)),
                    state.Request.Consent, state.Request.Language, state.Request.Consent.ModelId);
                var result = await calls.CallForOwnerAsync(owner, request, id, ct);
                await RecordAsync(result);
                if (result.Status is AiRequestStatus.Running or AiRequestStatus.Blocked) return;
            }
            catch (OperationCanceledException) { throw; }
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
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tasks.UpdateAsync(owner, id, current =>
                {
                    var attempt = current.Steps[step.Index].Current;
                    if (attempt.RequestId != requestId)
                        throw new InvalidDataException("ASR attempt changed during execution");
                    attempt.LogId = result.LogId;
                    attempt.Status = result.Status.ToString();
                    attempt.ErrorMessage = result.ErrorMessage;
                    current.StopRequested |= result.Status == AiRequestStatus.Blocked;
                    current.CompletionAcknowledged |= current.Route is not null &&
                        current.Steps.All(x => x.Current.Status == "Succeeded");
                }, budget.Token);
                await SyncCloudAttemptAsync(owner, id, budget.Token);
            }

            async Task MarkAsync(string status, string message, bool stop)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tasks.UpdateAsync(owner, id, current =>
                {
                    var attempt = current.Steps[step.Index].Current;
                    attempt.Status = status;
                    attempt.ErrorMessage = message;
                    current.StopRequested |= stop;
                }, budget.Token);
                await SyncCloudAttemptAsync(owner, id, budget.Token);
            }
        }
    }

    private async Task<NoteAsrAdaptiveRun> RunAdaptiveAsync(Guid owner, Guid taskId, NoteAsrState rootState,
        NoteAsrStep rootStep, int maxCalls, CancellationToken ct)
    {
        var options = NoteAsrAdaptiveOptions.Default with { MaxCalls = maxCalls };
        var engine = new NoteAsrAdaptiveTranscription(
            options,
            async (request, token) =>
            {
                await tasks.UpdateAsync(owner, taskId, state =>
                {
                    if (state.AttemptId != rootState.AttemptId || state.StopRequested)
                        throw new DomainException(40901, "任务执行快照或停止状态已变化");
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
            },
            async (request, token) =>
            {
                var current = await tasks.ReadAsync(owner, taskId, token);
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
                await CallAdaptiveAsync(owner, taskId, rootState, rootStep, request, token));
        return await engine.RunAsync(rootStep.Current.RequestId,
            new NoteAsrAdaptiveRange(0, rootStep.Samples, NoteAsrPlan.SampleRate), ct);
    }

    private async ValueTask<NoteAsrAdaptiveResult> CallAdaptiveAsync(Guid owner, Guid taskId,
        NoteAsrState rootState, NoteAsrStep rootStep, NoteAsrAdaptiveRequest request, CancellationToken ct)
    {
        var current = await tasks.ReadAsync(owner, taskId, ct);
        if (current.StopRequested)
            return new(NoteAsrAdaptiveOutcome.Cancelled, ReasonCode: "stop_requested");
        var version = await materials.GetAsync(owner, rootState.Request.VersionId, ct);
        if (NoteMaterialManifestValidator.Hash(version.InputJson) != rootState.InputSha256 ||
            NoteMaterialManifestValidator.Hash(version.ManifestJson) != rootState.ManifestSha256)
            return new(NoteAsrAdaptiveOutcome.ConfigurationRejected, ReasonCode: "material_changed");
        var start = checked(rootStep.SourceOffset + request.Range.OffsetSamples);
        if (start < rootStep.SourceOffset ||
            checked(start + request.Range.Samples) > rootStep.SourceOffset + rootStep.Samples)
            return new(NoteAsrAdaptiveOutcome.ConfigurationRejected, ReasonCode: "range_outside_fixed_segment");
        var bytes = await materials.ReadAudioSliceAsync(owner, rootState.Request.VersionId,
            rootStep.FileIndex, start, request.Range.Samples, ct);
        var routedAttempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == rootState.AttemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40901, "ASR 执行快照不存在");
        if (string.IsNullOrWhiteSpace(routedAttempt.PromptRendered) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptSha256) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptRevision) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptTemplateSha256) ||
            string.IsNullOrWhiteSpace(routedAttempt.PromptTemplateRevision))
            throw new DomainException(40901, "ASR 提示词快照缺失，不能在当前配置下重发");
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
            result = await calls.CallForOwnerAsync(owner, callRequest, taskId, ct);
        }
        catch (OperationCanceledException)
        {
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                "Unknown", "ASR 调用取消，未确认供应商是否处理；不会自动重发", null, CancellationToken.None);
            throw;
        }
        catch (DomainException error)
        {
            var busy = error.ErrorCode == 50302;
            await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
                busy ? "Queued" : "Blocked", error.Message, null, CancellationToken.None);
            return busy
                ? new(NoteAsrAdaptiveOutcome.TransientFailure, ReasonCode: "admission_busy", Reason: error.Message)
                : new(NoteAsrAdaptiveOutcome.ConfigurationRejected,
                    ReasonCode: "provider_configuration_or_admission_rejected", Reason: error.Message);
        }
        await PersistAdaptiveAttemptAsync(owner, taskId, rootStep.Index, request.ChildRequestId,
            result.Status.ToString(), result.ErrorMessage, result.LogId, CancellationToken.None);
        var row = await db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == result.LogId &&
            x.UserId == owner && x.Purpose == "ai.asr-call" && x.SourceObjectId == taskId.ToString(), ct);
        return Classify(row, null);
    }

    private async Task PersistAdaptiveAttemptAsync(Guid owner, Guid taskId, int segmentIndex,
        Guid requestId, string status, string? error, Guid? logId, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await tasks.UpdateAsync(owner, taskId, state =>
        {
            var attempt = LocateAttempt(state.Steps, segmentIndex, requestId)
                ?? throw new InvalidDataException("Adaptive ASR child attempt is missing");
            attempt.Status = status;
            attempt.ErrorMessage = error;
            attempt.LogId = logId ?? attempt.LogId;
        }, linked.Token);
    }

    private async Task RecordAdaptiveSuccessAsync(Guid owner, Guid taskId, NoteAsrState state,
        NoteAsrStep step, NoteAsrAdaptiveRun run, CancellationToken ct)
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
        var providerHandle = "adaptive:" + step.Current.RequestId.ToString("N");
        var existing = await db.NoteAsrSegmentEvidence.SingleOrDefaultAsync(x => x.OwnerId == owner &&
            x.AttemptId == attemptEntity.Id && x.FencingToken == attemptEntity.FencingToken &&
            x.SegmentIndex == step.Index && x.ReceiptKind == "adaptive", ct);
        if (existing is null)
        {
            existing = new NoteAsrSegmentEvidenceEntity
            {
                OwnerId = owner, AttemptId = attemptEntity.Id, TaskId = taskId,
                VersionId = attemptEntity.VersionId, Route = attemptEntity.Route,
                FencingToken = attemptEntity.FencingToken, SegmentIndex = step.Index,
                RecordingId = step.RecordingId, RecordingSegment = step.RecordingSegment,
                ChunkIndex = step.ChunkIndex, SourceFileIndex = step.FileIndex,
                SourceSha256 = step.Sha256, SourceOffsetSamples = step.SourceOffset, Samples = step.Samples,
                ReceiptKind = "adaptive", ProviderHandle = providerHandle,
                ResultText = transcript, ResultSha256 = resultSha, CoverageJson = coverage
            };
            db.NoteAsrSegmentEvidence.Add(existing);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (IsUniqueViolation(error))
            {
                db.Entry(existing).State = EntityState.Detached;
                existing = await db.NoteAsrSegmentEvidence.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OwnerId == owner && x.AttemptId == attemptEntity.Id &&
                    x.FencingToken == attemptEntity.FencingToken && x.SegmentIndex == step.Index &&
                    x.ReceiptKind == "adaptive", ct);
                if (existing is null || existing.ProviderHandle != providerHandle ||
                    existing.ResultSha256 != resultSha || existing.CoverageJson != coverage)
                    throw new DomainException(40901, "同一分片已有不同自适应成功证据，原记录保留");
            }
        }
        else if (existing.ProviderHandle != providerHandle || existing.ResultSha256 != resultSha ||
                 existing.CoverageJson != coverage)
            throw new DomainException(40901, "同一分片已有不同自适应成功证据，原记录保留");
        var evidenceId = existing.Id;
        await tasks.UpdateAsync(owner, taskId, current =>
        {
            if (current.AttemptId != attemptEntity.Id || current.StopRequested) return;
            var target = current.Steps[step.Index].Current;
            target.Status = "Succeeded";
            target.ErrorMessage = null;
            target.LogId = rootLogId;
            target.EvidenceId = evidenceId;
            current.CompletionAcknowledged = current.Steps.All(x => x.Current.Status == "Succeeded");
        }, ct);
        var completed = await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x =>
                x.OwnerId == owner && x.AttemptId == attemptEntity.Id &&
                x.FencingToken == attemptEntity.FencingToken)
            .Select(x => x.SegmentIndex).Distinct().CountAsync(ct);
        await db.NoteAsrAttempts.Where(x => x.Id == attemptEntity.Id && x.OwnerId == owner &&
                x.State != "Succeeded")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedSegments, completed)
                .SetProperty(x => x.State, "Running")
                .SetProperty(x => x.LastErrorCode, (string?)null)
                .SetProperty(x => x.LastErrorMessage, (string?)null)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        await SyncCloudAttemptAsync(owner, taskId, ct);
    }

    private async Task RecordAdaptiveFailureAsync(Guid owner, Guid taskId, NoteAsrStep step,
        NoteAsrAdaptiveRun run, CancellationToken ct)
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
            NoteAsrAdaptiveOutcome.UnconfirmedTimeout or NoteAsrAdaptiveOutcome.Unknown =>
                ("Unknown", "ASR 结果未确认；已保留已有成功子片和真实记录，请人工核对"),
            _ => ("Failed", "自动分片重试未能覆盖完整原分片；已成功子片和真实记录保留")
        };
        var detail = run.PartialReason ?? run.StopResult?.Reason;
        await MarkAsync(status, string.IsNullOrWhiteSpace(detail) ? message : message + "：" + detail, true);

        async Task MarkAsync(string nextStatus, string error, bool stop)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            await tasks.UpdateAsync(owner, taskId, current =>
            {
                if (current.Steps.Count <= step.Index) return;
                var target = current.Steps[step.Index].Current;
                target.Status = nextStatus;
                target.ErrorMessage = error;
                current.StopRequested |= stop;
                current.CompletionAcknowledged = false;
            }, linked.Token);
            await SyncCloudAttemptAsync(owner, taskId, linked.Token);
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

    private async Task SyncCloudAttemptAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        var state = await tasks.ReadAsync(owner, taskId, ct);
        if (state.AttemptId is not { } attemptId) return;
        var completed = state.Steps.Count(x => x.Current.Status == "Succeeded");
        var failed = state.Steps.Count(x => x.Current.Status is not ("Queued" or "Running" or "Succeeded"));
        var terminal = state.Steps.Count > 0 && completed == state.Steps.Count;
        var stopped = state.StopRequested;
        await db.NoteAsrAttempts.Where(x => x.Id == attemptId && x.OwnerId == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedSegments, completed)
                .SetProperty(x => x.FailedSegments, failed)
                .SetProperty(x => x.State, terminal ? "Succeeded" : stopped ? "Failed" : "Running")
                .SetProperty(x => x.StopRequested, stopped)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }
}
