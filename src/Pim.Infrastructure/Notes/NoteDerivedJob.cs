using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

public sealed class NoteDerivedDispatchJob(
    NoteDerivedStore tasks,
    IBackgroundJobClient jobs,
    ILogger<NoteDerivedDispatchJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var (owner, taskId) in tasks.EnumerateTasks())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await tasks.ReadTaskAsync(owner, taskId, ct);
                var needsStopCheckpoint = state.StopRequested &&
                    state.Steps.Any(step => step.Current.Status == "Queued");
                if (!NoteDerivedStore.IsActive(state) && !needsStopCheckpoint) continue;
                using var lease = tasks.TryRunLease(owner, taskId);
                if (lease is not null)
                    jobs.Enqueue<NoteDerivedJob>(job => job.RunAsync(owner, taskId, CancellationToken.None));
            }
            catch (Exception error) when (error is IOException or DomainException or
                System.Text.Json.JsonException or InvalidDataException)
            {
                logger.LogWarning("A derived checkpoint could not be dispatched ({ErrorType}); other tasks continue",
                    error.GetType().Name);
            }
        }
    }
}

public sealed class NoteDerivedJob(
    NoteDerivedStore tasks,
    NoteMaterialUploadStore materials,
    NoteAsrTaskStore asrTasks,
    NoteDerivedSnapshotBuilder snapshots,
    PimDbContext db,
    AiTextCallService calls,
    IOptions<NoteDerivedAudioContextOptions> audioContext,
    ILogger<NoteDerivedJob> logger,
    IServiceScopeFactory? scopeFactory = null,
    Func<CancellationToken, Task>? publishBoundaryProbe = null)
{
    private static readonly TimeSpan UnknownWithoutReceiptAfter = TimeSpan.FromMinutes(15);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        using var lease = tasks.TryRunLease(owner, taskId);
        if (lease is null) return;

        // Fairness bound mirrors ASR. Every exit is a durable checkpoint and never allocates a new attempt.
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        for (var processed = 0; processed < 256; processed++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await tasks.ReadTaskAsync(owner, taskId, ct);
            if (state.AwaitingAsr || NoteDerivedStore.NeedsAsrRecovery(state))
            {
                if (!await MaterializeAsrInputsAsync(owner, taskId, ct)) return;
                state = await tasks.ReadTaskAsync(owner, taskId, ct);
            }
            if (state.Blocked) return;
            if (state.StopRequested && state.Steps.All(x => x.Current.Status != "Running"))
            {
                if (state.Steps.Any(x => x.Current.Status == "Queued"))
                {
                    await CancelQueuedAfterStopAsync(owner, taskId, ct);
                    await ReconcileCoverageAsync(owner, taskId, ct);
                }
                return;
            }

            var ready = ReadySteps(state).Where(x => !deferred.Contains(x.Key)).ToArray();
            if (ready.Length == 0)
            {
                await ReconcileCoverageAsync(owner, taskId, ct);
                return;
            }
            if (ready.Length == 1 || scopeFactory is null)
            {
                var outcome = await RunStepLockedAsync(owner, taskId, ready[0].Key, ct);
                if (outcome == StepOutcome.Stop) return;
                if (outcome == StepOutcome.Deferred) deferred.Add(ready[0].Key);
                continue;
            }

            var limit = Math.Clamp(audioContext.Value.MaxParallelCalls, 1,
                NoteDerivedAudioContextOptions.HardMaxParallelCalls);
            var wave = ready.Take(limit).Select(x => x.Key).ToArray();
            var outcomes = await Task.WhenAll(wave.Select(key =>
                RunStepInScopeAsync(owner, taskId, key, ct)));
            for (var index = 0; index < wave.Length; index++)
            {
                if (outcomes[index] == StepOutcome.Stop) return;
                if (outcomes[index] == StepOutcome.Deferred) deferred.Add(wave[index]);
            }
        }
    }

    private async Task ReconcileCoverageAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        var latest = await tasks.ReadTaskAsync(owner, taskId, ct);
        if (latest.Steps.Any(x => x.Current.Status is "Queued" or "Running")) return;
        var coverage = await BuildCoverageAsync(latest, ct);
        if (SameCoverage(coverage.Coverage, latest.Coverage)) return;
        await tasks.UpdateTaskAsync(owner, taskId, current =>
        {
            current.Coverage = coverage.Coverage;
            current.Issues = coverage.Coverage.Issues.ToList();
        }, ct);
    }

    private static bool SameCoverage(NoteDerivedCoverageDto left, NoteDerivedCoverageDto right) =>
        left.TextBlocks == right.TextBlocks &&
        left.TranscriptSegments == right.TranscriptSegments &&
        left.CompletedTranscriptSegments == right.CompletedTranscriptSegments &&
        left.Photos == right.Photos &&
        left.CompletedPhotos == right.CompletedPhotos &&
        left.InkPreviews == right.InkPreviews &&
        left.CompletedInkPreviews == right.CompletedInkPreviews &&
        left.VisionBatches == right.VisionBatches &&
        left.CompletedVisionBatches == right.CompletedVisionBatches &&
        left.Complete == right.Complete &&
        left.Issues.Count == right.Issues.Count &&
        left.Issues.All(right.Issues.Contains);

    private async Task<StepOutcome> RunStepInScopeAsync(Guid owner, Guid taskId, string stepKey,
        CancellationToken ct)
    {
        if (scopeFactory is null)
            return await RunStepLockedAsync(owner, taskId, stepKey, ct);
        using var scope = scopeFactory.CreateScope();
        var job = new NoteDerivedJob(
            tasks,
            materials,
            asrTasks,
            scope.ServiceProvider.GetRequiredService<NoteDerivedSnapshotBuilder>(),
            scope.ServiceProvider.GetRequiredService<PimDbContext>(),
            scope.ServiceProvider.GetRequiredService<AiTextCallService>(),
            audioContext,
            logger,
            scopeFactory: null,
            publishBoundaryProbe: publishBoundaryProbe);
        return await job.RunStepLockedAsync(owner, taskId, stepKey, ct);
    }

    private async Task<StepOutcome> RunStepLockedAsync(Guid owner, Guid taskId, string stepKey,
        CancellationToken ct)
    {
        var state = await tasks.ReadTaskAsync(owner, taskId, ct);
        var step = state.Steps.SingleOrDefault(x => x.Key == stepKey);
        if (step is null) return StepOutcome.Stop;
        var requestId = step.Current.RequestId;

        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var synthesis = step.Kind is NoteDerivedStepKind.RecordSynthesis or
            NoteDerivedStepKind.AnalysisSynthesis;
        var boundLogId = step.Current.LogId;
        var receipt = boundLogId is { } logId && synthesis
            ? await FindReceiptByIdAsync(owner, logId, ct)
            : await FindReceiptAsync(owner, requestId, ct);
        if (receipt is not null)
        {
            if (boundLogId is not null && synthesis)
            {
                var identity = NoteDerivedReceipts.ValidateSynthesisReceiptIdentity(
                    state, step, receipt, boundLogId,
                    requireSucceeded: receipt.Status == "succeeded");
                if (identity.Count > 0)
                {
                    await StopWithIssueAsync(owner, taskId, step.Key, identity[0].Code,
                        identity[0].Message, "Blocked", ct);
                    return StepOutcome.Stop;
                }
            }
            else if (!ReceiptMatches(step, receipt, state, out var bindingError))
            {
                await StopWithIssueAsync(owner, taskId, step.Key, "ai_receipt_source_mismatch",
                    bindingError, "Blocked", ct);
                return StepOutcome.Stop;
            }
            if (receipt.Status == "running")
            {
                if (receipt.ExecutionExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                    await StopWithIssueAsync(owner, taskId, step.Key, "ai_outcome_unknown",
                        "调用执行期限已过，结果无法确认；不会自动重发，请显式确认可能重复收费后重试",
                        "Unknown", ct);
                return StepOutcome.Deferred;
            }

            var recovered = AiCallAudit.Result(receipt, true);
            return await ApplyResultAsync(owner, taskId, step.Key, requestId, recovered, receipt, ct);
        }

        if (step.Current.Status is "Running" or "Unknown")
        {
            if (boundLogId is not null && synthesis)
            {
                await StopWithIssueAsync(owner, taskId, step.Key, "synthesis_receipt_missing",
                    "恢复时找不到检查点绑定的合成回执；不生成候选版本", "Blocked", ct);
                return StepOutcome.Stop;
            }
            if (await IsStaleWithoutReceiptAsync(state, step, ct))
            {
                await StopWithIssueAsync(owner, taskId, step.Key, "ai_outcome_unknown",
                    "已持久化发送意图但未找到调用回执，结果无法确认；不会自动重发，请显式确认可能重复收费后重试",
                    "Unknown", ct);
                return StepOutcome.Stop;
            }
            return StepOutcome.Deferred;
        }
        if (state.StopRequested || step.Current.Status != "Queued") return StepOutcome.Deferred;

        var prepared = await PrepareAsync(state, step, ct);
        if (prepared.Issue is not null)
        {
            await StopWithIssueAsync(owner, taskId, step.Key, prepared.Issue.Code,
                prepared.Issue.Message, "Blocked", ct);
            return StepOutcome.Stop;
        }

        // The durable attempt is marked Running before the only possible external send.
        await tasks.UpdateTaskAsync(owner, taskId, current =>
        {
            var stored = current.Steps.Single(x => x.Key == step.Key);
            if (stored.Current.RequestId != requestId)
                throw new InvalidDataException("Derived attempt changed during execution");
            stored.InputCharacters = prepared.InputCharacters;
            if (!current.StopRequested) stored.Current.Status = "Running";
        }, ct);
        if ((await tasks.ReadTaskAsync(owner, taskId, ct)).StopRequested)
            return StepOutcome.Deferred;

        try
        {
            var result = await SendAsync(owner, state, step, prepared, requestId, ct);
            var receiptAfterSend = await FindReceiptAsync(owner, requestId, ct);
            return await ApplyResultAsync(owner, taskId, step.Key, requestId, result,
                receiptAfterSend, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (DomainException error)
        {
            var unknown = error.ErrorCode == 50301;
            await StopWithIssueAsync(owner, taskId, step.Key,
                unknown ? "ai_outcome_unknown" : "ai_call_blocked",
                unknown
                    ? "调用结果未能确认保存；不会自动重发，请先核对调用记录"
                    : error.Message,
                unknown ? "Unknown" : "Blocked", ct);
            return StepOutcome.Stop;
        }
        catch (Exception error) when (error is IOException or DbUpdateException or DbException or
            TimeoutException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.LogWarning("Derived task {TaskId} step {StepKey} lost its checkpoint ({ErrorType})",
                taskId, step.Key, error.GetType().Name);
            await StopWithIssueAsync(owner, taskId, step.Key, "checkpoint_unknown",
                "材料或检查点读写中断，结果无法确认；任务已停止且不会自动重发", "Unknown", ct);
            return StepOutcome.Stop;
        }
    }

    private async Task<bool> MaterializeAsrInputsAsync(Guid owner, Guid taskId,
        CancellationToken ct)
    {
        var legacy = false;
        try
        {
            var state = await tasks.ReadTaskAsync(owner, taskId, ct);
            legacy = !state.AwaitingAsr && NoteDerivedStore.NeedsAsrRecovery(state);
            if ((!state.AwaitingAsr && !legacy) || state.Request.AsrTaskId is null)
                return false;
            if (state.StopRequested)
            {
                if (legacy) await BlockLegacyAsrAsync(owner, taskId, ct);
                return false;
            }
            if (legacy && state.Steps.Any(step => step.Current.Status != "Queued"))
            {
                await BlockLegacyAsrAsync(owner, taskId, ct);
                return false;
            }
            var version = await materials.GetAsync(owner, state.Request.VersionId, ct);
            var built = await snapshots.BuildAsync(owner, version, state.InputSha256,
                state.ManifestSha256, state.Request.AsrTaskId,
                waitForAsr: true, allowIncompleteAsr: false, ct);
            var plan = new NoteDerivedPlanner(audioContext.Value).Build(built.Snapshot);
            if (built.AwaitingAsr)
            {
                if (legacy)
                {
                    await tasks.UpdateTaskAsync(owner, taskId, current =>
                    {
                        if (!NoteDerivedStore.NeedsAsrRecovery(current) ||
                            current.StopRequested) return;
                        if (current.Steps.Any(step => step.Current.Status != "Queued"))
                        {
                            current.Blocked = true;
                            current.Issues = new[] { LegacyIssue() }.Concat(current.Issues)
                                .DistinctBy(issue => (issue.Code, issue.Message,
                                    issue.SourcePath, issue.BlockId, issue.StepKey)).ToList();
                            current.Coverage = current.Coverage with
                            {
                                Complete = false,
                                Issues = current.Issues
                            };
                            return;
                        }
                        ApplyPlan(current, plan, awaitingAsr: true);
                    }, ct);
                }
                return false;
            }
            await tasks.UpdateTaskAsync(owner, taskId, current =>
            {
                if (current.StopRequested) return;
                var currentLegacy = !current.AwaitingAsr &&
                    NoteDerivedStore.NeedsAsrRecovery(current);
                if (!current.AwaitingAsr && !currentLegacy) return;
                if (currentLegacy &&
                    current.Steps.Any(step => step.Current.Status != "Queued"))
                {
                    current.Blocked = true;
                    current.Issues = new[] { LegacyIssue() }.Concat(current.Issues)
                        .DistinctBy(issue => (issue.Code, issue.Message,
                            issue.SourcePath, issue.BlockId, issue.StepKey)).ToList();
                    current.Coverage = current.Coverage with
                    {
                        Complete = false,
                        Issues = current.Issues
                    };
                    return;
                }
                ApplyPlan(current, plan, awaitingAsr: false);
            }, ct);
            return true;
        }
        catch (Exception error) when (error is DomainException or IOException or
            System.Text.Json.JsonException or InvalidDataException)
        {
            logger.LogWarning("Derived task {TaskId} could not materialize ASR inputs ({ErrorType})",
                taskId, error.GetType().Name);
            if (legacy && error is DomainException)
                await BlockLegacyAsrAsync(owner, taskId, ct);
            return false;
        }
    }

    private async Task BlockLegacyAsrAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        await tasks.UpdateTaskAsync(owner, taskId, current =>
        {
            if (!NoteDerivedStore.NeedsAsrRecovery(current)) return;
            current.Blocked = true;
            var issues = new[] { LegacyIssue() }.Concat(current.Issues)
                .DistinctBy(issue => (issue.Code, issue.Message, issue.SourcePath,
                    issue.BlockId, issue.StepKey)).ToList();
            current.Issues = issues;
            current.Coverage = current.Coverage with { Complete = false, Issues = issues };
            foreach (var step in current.Steps.Where(step =>
                         step.Current.Status is "Queued" or "Running"))
            {
                step.Current.Status = "Cancelled";
                step.Current.ErrorCode = "legacy_asr_transcript_missing";
                step.Current.ErrorMessage =
                    "旧整理任务缺少完整 ASR 转写，已阻断且不会继续发送";
            }
        }, ct);
    }

    private static void ApplyPlan(NoteDerivedTaskState current, NoteDerivedPlan plan,
        bool awaitingAsr)
    {
        current.Plan = plan;
        current.Coverage = plan.InitialCoverage;
        current.Issues = plan.InitialCoverage.Issues.ToList();
        current.Steps = plan.Steps.Select(step => new NoteDerivedStepState
        {
            Key = step.Key,
            Kind = step.Kind,
            DependsOn = step.DependsOn,
            Images = step.Images,
            InputCharacters = step.InputCharacters,
            Audio = step.Audio ?? [],
            TranscriptOrdinals = step.TranscriptOrdinals ?? [],
            MaxOutputCharacters = step.MaxOutputCharacters,
            Attempts = [new()]
        }).ToList();
        current.Blocked = !awaitingAsr &&
            (plan.Steps.Count == 0 || plan.InitialCoverage.Issues.Count > 0);
        current.AwaitingAsr = awaitingAsr;
    }

    private static NoteDerivedIssueDto LegacyIssue() => Issue(
        "legacy_asr_transcript_missing",
        "旧整理任务缺少完整 ASR 转写，已阻断，不会以空转写成功；请恢复 ASR 后重新发起整理");

    internal async Task ReconcileAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        using var lease = tasks.TryRunLease(owner, taskId);
        if (lease is null) return;
        await ReconcileLockedAsync(owner, taskId, ct);
    }

    internal async Task ReconcileLockedAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        var state = await tasks.ReadTaskAsync(owner, taskId, ct);
        if (NoteDerivedStore.NeedsAsrRecovery(state))
        {
            _ = await MaterializeAsrInputsAsync(owner, taskId, ct);
            state = await tasks.ReadTaskAsync(owner, taskId, ct);
        }
        var candidates = state.Steps
            .Where(x => x.Current.RequestId != Guid.Empty &&
                x.Current.Status is "Running" or "Unknown")
            .Select(x => (x.Key, x.Current.RequestId))
            .ToArray();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            state = await tasks.ReadTaskAsync(owner, taskId, ct);
            var step = state.Steps.SingleOrDefault(x => x.Key == candidate.Key);
            if (step is null || step.Current.RequestId != candidate.RequestId ||
                step.Current.Status is not ("Running" or "Unknown"))
                continue;

            var synthesis = step.Kind is NoteDerivedStepKind.RecordSynthesis or
                NoteDerivedStepKind.AnalysisSynthesis;
            var boundLogId = step.Current.LogId;
            var receipt = boundLogId is { } logId && synthesis
                ? await FindReceiptByIdAsync(owner, logId, ct)
                : await FindReceiptAsync(owner, candidate.RequestId, ct);
            if (receipt is null)
            {
                if (boundLogId is not null && synthesis)
                {
                    await StopWithIssueAsync(owner, taskId, step.Key,
                        "synthesis_receipt_missing",
                        "恢复时找不到检查点绑定的合成回执；不生成候选版本", "Blocked", ct);
                    continue;
                }
                continue;
            }
            if (boundLogId is not null && synthesis)
            {
                if (receipt.Status != "succeeded") continue;
                var identity = NoteDerivedReceipts.ValidateSynthesisReceiptIdentity(
                    state, step, receipt, boundLogId);
                if (identity.Count > 0)
                {
                    await StopWithIssueAsync(owner, taskId, step.Key, identity[0].Code,
                        identity[0].Message, "Blocked", ct);
                    continue;
                }
            }
            else if (receipt.Status != "succeeded" ||
                !ReceiptMatches(step, receipt, state, out _))
                continue;

            var result = AiCallAudit.Result(receipt, true);
            _ = await ApplyResultAsync(owner, taskId, step.Key, candidate.RequestId, result, receipt, ct);
        }
        // Coverage and revision must converge even when the stop/recovery path has no more
        // running receipt candidates; otherwise a stopped task can remain "complete" or keep
        // returning a revision that no longer matches cancelled steps.
        await ReconcileCoverageAsync(owner, taskId, ct);
    }

    internal async Task<NoteDerivedTaskReceiptDto> ReceiptAsync(Guid owner, Guid taskId,
        CancellationToken ct)
    {
        var state = await tasks.ReadTaskAsync(owner, taskId, ct);
        var source = await ValidateSourcesAsync(state, ct);
        var actual = await ReadActualImagesAsync(state, ct);
        var receiptContext = await LoadReceiptContextAsync(state, ct);
        var issues = NoteDerivedCoverage.DistinctIssues(source.Issues
            .Concat(actual.Issues)
            .Concat(state.Issues));
        return NoteDerivedReceipts.Build(state, actual.Images, source.CompletedLogIds,
            receiptContext, issues);
    }

    private async Task<NoteDerivedReceiptContext> LoadReceiptContextAsync(
        NoteDerivedTaskState state, CancellationToken ct)
    {
        NoteAsrState? asr = null;
        if (state.Request.AsrTaskId is { } asrTaskId)
        {
            try { asr = await asrTasks.ReadAsync(state.Owner, asrTaskId, ct); }
            catch (DomainException) { }
        }

        var logIds = state.Steps.Where(x => x.Current.LogId.HasValue)
            .Select(x => x.Current.LogId!.Value)
            .Concat(state.Plan.Input.Transcripts.Where(x => x.LogId.HasValue)
                .Select(x => x.LogId!.Value))
            .Distinct()
            .ToArray();
        var requestIds = state.Steps.Select(x => x.Current.RequestId)
            .Concat(asr?.Steps.Select(x => x.Current.RequestId) ?? [])
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToArray();

        var logs = new List<AiRequestLogEntity>();
        if (logIds.Length > 0)
            logs.AddRange(await db.AiRequestLogs.AsNoTracking()
                .Where(x => x.UserId == state.Owner && logIds.Contains(x.Id))
                .ToListAsync(ct));
        if (requestIds.Length > 0)
            logs.AddRange(await db.AiRequestLogs.AsNoTracking()
                .Where(x => x.UserId == state.Owner && x.ClientRequestId.HasValue &&
                    requestIds.Contains(x.ClientRequestId.Value))
                .ToListAsync(ct));
        var distinctLogs = logs.DistinctBy(x => x.Id).ToArray();
        var logsById = distinctLogs.ToDictionary(x => x.Id);
        var logsByRequestId = distinctLogs
            .Where(x => x.ClientRequestId.HasValue)
            .GroupBy(x => x.ClientRequestId!.Value)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<AiRequestLogEntity>)x
                .OrderBy(row => row.Id).ToArray());

        var allLogIds = distinctLogs.Select(x => x.Id).ToArray();
        var audioByLogId = allLogIds.Length == 0
            ? new Dictionary<Guid, AiCallAudioEntity>()
            : (await db.AiCallAudio.AsNoTracking()
                .Where(x => allLogIds.Contains(x.LogId))
                .ToListAsync(ct))
                .GroupBy(x => x.LogId)
                .ToDictionary(x => x.Key, x => x.OrderBy(row => row.Id).First());

        var versionsByStep = new Dictionary<string, NoteDerivedVersionDto>(StringComparer.Ordinal);
        foreach (var step in state.Steps.Where(x =>
                     x.Kind is NoteDerivedStepKind.RecordSynthesis or
                         NoteDerivedStepKind.AnalysisSynthesis))
        {
            var artifactId = step.Kind == NoteDerivedStepKind.RecordSynthesis
                ? state.CompleteRecordArtifactId
                : state.IndependentAnalysisArtifactId;
            if (artifactId == Guid.Empty || step.Current.RequestId == Guid.Empty) continue;
            var artifactKind = step.Kind == NoteDerivedStepKind.RecordSynthesis
                ? NoteDerivedArtifactKind.CompleteRecord
                : NoteDerivedArtifactKind.IndependentAnalysis;
            var versionId = VersionId(state.Request.TaskId, step.Current.RequestId, artifactKind);
            try
            {
                versionsByStep[step.Key] =
                    await tasks.GetVersionAsync(state.Owner, artifactId, versionId, ct);
            }
            catch (DomainException) { }
        }

        var attempt = asr?.AttemptId is { } attemptId
            ? await db.NoteAsrAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == state.Owner, ct)
            : null;
        var asrModel = asr?.Request.Consent is { } consent
            ? await db.AiModelProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == consent.ModelId, ct)
            : null;
        var accepted = attempt is null || asr is null
            ? new Dictionary<int, NoteAsrSegmentEvidenceEntity>()
            : await NoteAsrAcceptedEvidence.LoadAsync(db, state.Owner, attempt, asr, ct);
        return new(logsById, logsByRequestId, versionsByStep, audioByLogId, asr, attempt,
            asrModel, accepted);
    }

    private static IReadOnlyList<NoteDerivedStepState> ReadySteps(NoteDerivedTaskState state)
    {
        var running = state.Steps.Where(x => x.Current.Status == "Running").ToArray();
        if (running.Length > 0) return running;
        if (state.StopRequested) return [];
        return state.Steps.Where(candidate => candidate.Current.Status == "Queued" &&
            candidate.DependsOn.All(key => state.Steps.Any(step =>
                step.Key == key && step.Current.Status == "Succeeded"))).ToArray();
    }

    private async Task<PreparedCall> PrepareAsync(NoteDerivedTaskState state, NoteDerivedStepState step,
        CancellationToken ct)
    {
        var source = await ValidateSourcesAsync(state, ct);
        if (source.Issues.Count > 0)
            return PreparedCall.Failed(source.Issues[0]);

        if (step.Kind == NoteDerivedStepKind.ImageEvidence)
        {
            if (step.Images.Count is < 1 or > NoteDerivedLimits.MaxImagesPerVisionCall)
                return PreparedCall.Failed(Issue("vision_batch_invalid",
                    "视觉批次必须包含 1-4 张图片；不自动拆分或截断", step.Key));
            if (step.Images.Sum(x => x.SizeBytes) > NoteDerivedLimits.MaxVisionCallBytes)
                return PreparedCall.Failed(Issue("vision_batch_too_large",
                    "视觉批次合计超过 8MiB；不压缩、不转码、不截断", step.Key));

            var images = new List<AiVisionImageRequest>(step.Images.Count);
            foreach (var image in step.Images.OrderBy(x => x.Ordinal))
            {
                byte[] bytes;
                try { bytes = await materials.ReadImageBytesAsync(state.Owner, state.Request.VersionId,
                    image.FileIndex, ct); }
                catch (DomainException error)
                {
                    return PreparedCall.Failed(Issue(error.ErrorCode == 41301 ? "image_size_limit" :
                        error.ErrorCode == 40901 ? "image_changed" : "image_unreadable",
                        error.Message, step.Key, image.Path));
                }
                if (bytes.Length != image.SizeBytes || Digest(bytes) != image.Sha256)
                    return PreparedCall.Failed(Issue("image_changed",
                        "图片实际字节与固定材料摘要不一致，未发送", step.Key, image.Path));
                images.Add(new(NoteDerivedPrompts.ImageName(image), image.ContentType,
                    Convert.ToBase64String(bytes)));
            }
            var prompt = NoteDerivedPrompts.ImageEvidence(step.Key, step.Images);
            if (prompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
                return PreparedCall.Failed(Issue("vision_prompt_too_large",
                    "视觉提示超过 SDK 实际输入上限；未截断且不会发送", step.Key));
            return PreparedCall.Vision(prompt, images, prompt.Length);
        }

        if (step.Kind == NoteDerivedStepKind.AudioBatchEvidence)
        {
            if (step.Audio.Count is < 1 or > NoteDerivedAudioContext.MaxSlices)
                return PreparedCall.Failed(Issue("audio_batch_invalid",
                    "录音批次必须包含 1-5000 个固定片段；不自动拆分或截断", step.Key));
            var perBatchBytes = Math.Clamp(audioContext.Value.MaxBatchBytes, 1024,
                NoteDerivedAudioContext.MaxBytes);
            if (step.Audio.Sum(x => x.SizeBytes) > perBatchBytes)
                return PreparedCall.Failed(Issue("audio_batch_too_large",
                    $"录音批次合计超过 {perBatchBytes} 字节预算；不压缩、不截断", step.Key));
            var denied = await AudioContextDeniedAsync(state, step.Key, ct);
            if (denied is not null) return PreparedCall.Failed(denied);
            var loaded = await LoadAudioSlicesAsync(state, step.Audio, step.Key, ct);
            if (loaded.Issue is not null) return PreparedCall.Failed(loaded.Issue);
            var batchIndex = AudioBatchIndex(step.Key);
            var batchCount = state.Steps.Count(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
            var transcripts = state.Plan.Input.Transcripts
                .Where(x => step.TranscriptOrdinals.Contains(x.Ordinal));
            var audioBatchPrompt = NoteDerivedPrompts.AudioBatch(step.Key, batchIndex, batchCount,
                step.Audio, transcripts);
            if (audioBatchPrompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
                return PreparedCall.Failed(Issue("audio_batch_prompt_too_large",
                    "录音批次提示超过 SDK 实际输入上限；未截断且不会发送", step.Key));
            return PreparedCall.Text(audioBatchPrompt, audioBatchPrompt.Length, loaded.Items);
        }

        var evidence = await LoadEvidenceAsync(state, ct);
        if (evidence.Issue is not null) return PreparedCall.Failed(evidence.Issue);
        var audioBatched = state.Steps.Any(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        if (step.Kind == NoteDerivedStepKind.RecordSynthesis)
        {
            var coverage = await BuildCoverageAsync(state, ct, subjectStepKey: step.Key);
            if (!coverage.Coverage.Complete)
                return PreparedCall.Failed(Issue("record_coverage_incomplete",
                    "完整记录只能在全部 Markdown、ASR、照片和手写预览覆盖齐备后生成", step.Key));
        }

        var synthesis = await BuildSynthesisPromptAsync(state, step, evidence.Items, ct);
        if (synthesis.Issue is not null) return PreparedCall.Failed(synthesis.Issue);
        var audio = audioBatched
            ? new AudioCheck([], null)
            : await LoadAudioContextAsync(state, step.Key, ct);
        if (audio.Issue is not null) return PreparedCall.Failed(audio.Issue);
        return PreparedCall.Text(synthesis.Prompt!, synthesis.Prompt!.Length, audio.Items);
    }

    private async Task<SynthesisPromptCheck> BuildSynthesisPromptAsync(
        NoteDerivedTaskState state, NoteDerivedStepState step,
        IReadOnlyList<NoteDerivedEvidence> evidence, CancellationToken ct)
    {
        var audioBatched = state.Steps.Any(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        if (step.Kind == NoteDerivedStepKind.RecordSynthesis)
        {
            var prompt = NoteDerivedPrompts.Record(state.Plan.Input, evidence, audioBatched);
            return prompt.Length > NoteDerivedLimits.MaxSynthesisInputChars
                ? new(null, Issue("record_input_too_large",
                    $"完整记录输入 {prompt.Length} 字符，超过 SDK 实际上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                    step.Key))
                : new(prompt, null);
        }

        var record = state.Steps.Single(x => x.Kind == NoteDerivedStepKind.RecordSynthesis);
        if (record.Current.Status != "Succeeded" || record.Current.LogId is null)
            return new(null, Issue("record_version_missing",
                "独立分析必须引用已生成的完整记录版本", step.Key));
        var recordVersionId = VersionId(state.Request.TaskId, record.Current.RequestId,
            NoteDerivedArtifactKind.CompleteRecord);
        NoteDerivedVersionDto recordVersion;
        try
        {
            recordVersion = await tasks.GetVersionAsync(state.Owner,
                state.CompleteRecordArtifactId, recordVersionId, ct);
        }
        catch (DomainException)
        {
            return new(null, Issue("record_version_missing",
                "完整记录的具体版本尚不可读，独立分析未发送", step.Key));
        }
        if (recordVersion.Kind != NoteDerivedArtifactKind.CompleteRecord ||
            recordVersion.GenerationTaskId != state.Request.TaskId ||
            recordVersion.SourceVersionId != state.Request.VersionId ||
            recordVersion.InputSha256 != state.InputSha256 ||
            recordVersion.ManifestSha256 != state.ManifestSha256 ||
            recordVersion.AiCallLogId != record.Current.LogId ||
            string.IsNullOrWhiteSpace(recordVersion.ContentMarkdown))
            return new(null, Issue("record_version_invalid",
                "完整记录具体版本的身份、来源或正文校验失败，独立分析未发送", step.Key));
        var analysis = NoteDerivedPrompts.Analysis(state.Plan.Input, recordVersionId,
            recordVersion.ContentMarkdown, evidence, audioBatched);
        return analysis.Length > NoteDerivedLimits.MaxSynthesisInputChars
            ? new(null, Issue("analysis_input_too_large",
                $"独立分析输入 {analysis.Length} 字符，超过 SDK 实际上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                step.Key))
            : new(analysis, null);
    }

    private static int AudioBatchIndex(string key)
    {
        var separator = key.LastIndexOf(':');
        return separator >= 0 && int.TryParse(key[(separator + 1)..], out var number)
            ? Math.Max(0, number - 1) : 0;
    }

    private async Task<AudioCheck> LoadAudioContextAsync(NoteDerivedTaskState state, string stepKey,
        CancellationToken ct)
    {
        var planned = state.Plan.Input.Audio;
        if (planned.Count == 0) return new([], null);
        var denied = await AudioContextDeniedAsync(state, stepKey, ct);
        if (denied is not null) return new([], denied);
        return await LoadAudioSlicesAsync(state, planned, stepKey, ct);
    }

    private async Task<AudioCheck> LoadAudioSlicesAsync(NoteDerivedTaskState state,
        IReadOnlyList<NoteDerivedAudioInput> planned, string stepKey, CancellationToken ct)
    {
        var items = new List<AiAudioContextInput>(planned.Count);
        long total = 0;
        foreach (var audio in planned.OrderBy(x => x.Ordinal))
        {
            byte[] bytes;
            try
            {
                bytes = await materials.ReadAudioSliceAsync(state.Owner, state.Request.VersionId,
                    audio.FileIndex, audio.SourceOffsetSamples, audio.Samples, ct);
            }
            catch (Exception error) when (error is DomainException or IOException or InvalidDataException)
            {
                return new([], Issue("audio_context_unreadable",
                    "无法读取固定原始录音片段，未发送音频上下文：" + error.Message,
                    stepKey, audio.Path));
            }
            if (bytes.Length != audio.SizeBytes)
                return new([], Issue("audio_context_changed",
                    "固定原始录音片段实际大小与计划不一致，未发送音频上下文",
                    stepKey, audio.Path));
            var digest = Digest(bytes);
            total = checked(total + bytes.Length);
            if (total > NoteDerivedAudioContext.MaxBytes)
                return new([], Issue("audio_context_too_large",
                    $"原始录音上下文合计 {total} 字节，超过单次 {NoteDerivedAudioContext.MaxBytes} 字节上限；未截断、未发送",
                    stepKey, audio.Path));
            items.Add(new AiAudioContextInput(audio.Name, "audio/wav", audio.SourceSha256,
                digest, bytes.Length, state.Owner, state.NoteId, state.Request.VersionId,
                audio.RecordingId, audio.RecordingSegment, audio.ChunkIndex, audio.FileIndex,
                audio.SourceOffsetSamples, audio.Samples, audio.Path, bytes));
        }
        return new(items, null);
    }

    private async Task<NoteDerivedIssueDto?> AudioContextDeniedAsync(NoteDerivedTaskState state,
        string stepKey, CancellationToken ct)
    {
        var configured = audioContext.Value;
        if (configured.ModelIds.Length == 0 && configured.ModelNames.Length == 0)
            return Issue("audio_context_not_allowed",
                "当前服务端未授权该派生模型接收原始录音；未发送音频上下文", stepKey);
        var modelId = state.Request.Consent.ModelId;
        var profile = await db.AiModelProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == modelId, ct);
        if (profile is null || !configured.IsAllowed(profile.Id, profile.Model, profile.Name))
            return Issue("audio_context_not_allowed",
                "派生模型已不在服务端原始录音白名单；未发送音频上下文", stepKey);
        return null;
    }

    private async Task<AiTextCallResultDto> SendAsync(Guid owner, NoteDerivedTaskState state,
        NoteDerivedStepState step, PreparedCall prepared, Guid requestId, CancellationToken ct)
    {
        var consent = state.Request.Consent;
        if (step.Kind == NoteDerivedStepKind.ImageEvidence)
        {
            var request = new AiVisionCallRequest(requestId, owner, prepared.Prompt!, prepared.Images.Count,
                prepared.Images, new(consent.ModelId, consent.ModelRevision, consent.Endpoint, consent.Confirmed),
                ModelId: consent.ModelId);
            return await calls.CallVisionForOwnerAsync(owner, request, NoteDerivedPrompts.SourceObjectType,
                state.Request.TaskId.ToString("D"), NoteDerivedPrompts.VisionPurpose, ct);
        }
        var text = new AiTextCallRequest(requestId, owner, prepared.Prompt!, ModelId: consent.ModelId);
        var purpose = step.Kind switch
        {
            NoteDerivedStepKind.AudioBatchEvidence => NoteDerivedPrompts.AudioBatchPurpose,
            NoteDerivedStepKind.RecordSynthesis => NoteDerivedPrompts.RecordPurpose,
            _ => NoteDerivedPrompts.AnalysisPurpose
        };
        var destination = new AiVisionDestinationConsent(consent.ModelId, consent.ModelRevision,
            consent.Endpoint, consent.Confirmed);
        return prepared.Audio.Count > 0
            ? await calls.CallWithAudioForOwnerAsync(owner, text, prepared.Audio, destination,
                NoteDerivedPrompts.SourceObjectType, state.Request.TaskId.ToString("D"), purpose, ct)
            : await calls.CallForOwnerAsync(owner, text, destination,
                NoteDerivedPrompts.SourceObjectType, state.Request.TaskId.ToString("D"), purpose, ct);
    }

    private async Task<StepOutcome> ApplyResultAsync(Guid owner, Guid taskId, string stepKey, Guid requestId,
        AiTextCallResultDto result, Pim.Infrastructure.Data.Entities.AiRequestLogEntity? receipt,
        CancellationToken ct)
    {
        var state = await tasks.ReadTaskAsync(owner, taskId, ct);
        var step = state.Steps.Single(x => x.Key == stepKey);
        if (step.Current.RequestId != requestId)
            throw new InvalidDataException("Derived attempt changed while applying its result");

        if (result.Status == AiRequestStatus.Succeeded)
        {
            var synthesis = step.Kind is NoteDerivedStepKind.RecordSynthesis or
                NoteDerivedStepKind.AnalysisSynthesis;
            if (receipt is null)
            {
                if (synthesis)
                    await StopWithIssueAsync(owner, taskId, stepKey, "synthesis_receipt_missing",
                        "成功结果缺少可核验的合成回执；不生成候选版本", "Blocked", ct,
                        result.LogId);
                else
                    await StopWithIssueAsync(owner, taskId, stepKey, "ai_receipt_missing",
                        "成功结果缺少可核验的真实回执", "Unknown", ct);
                return StepOutcome.Stop;
            }
            if (receipt.Status != "succeeded")
            {
                if (synthesis)
                    await StopWithIssueAsync(owner, taskId, stepKey, "synthesis_status_mismatch",
                        "成功结果对应的合成回执不是成功状态；不生成候选版本", "Blocked", ct,
                        receipt.Id);
                else
                    await StopWithIssueAsync(owner, taskId, stepKey, "ai_receipt_missing",
                        "成功结果缺少可核验的真实回执", "Unknown", ct);
                return StepOutcome.Stop;
            }
            if (string.IsNullOrWhiteSpace(result.ResponseText))
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "ai_output_empty",
                    "模型成功回执没有可用正文；未生成派生版本", "Failed", ct,
                    stopTask: false, cancelDependentsOnly: true);
                return StepOutcome.Continue;
            }
            if (step.Kind == NoteDerivedStepKind.AudioBatchEvidence)
            {
                if (step.MaxOutputCharacters > 0 &&
                    result.ResponseText.Length > step.MaxOutputCharacters)
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, "audio_batch_output_too_large",
                        $"录音批次输出 {result.ResponseText.Length} 字符，超过本批 {step.MaxOutputCharacters} 字符预算；未截断，可显式重试",
                        "Failed", ct, stopTask: false, cancelDependentsOnly: true);
                    return StepOutcome.Continue;
                }
                var wireIssues = NoteDerivedReceipts.ValidateAudioBatchReceipt(
                    state, step, receipt, receipt.Id);
                if (wireIssues.Count > 0)
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, wireIssues[0].Code,
                        wireIssues[0].Message, "Failed", ct,
                        stopTask: false, cancelDependentsOnly: true);
                    return StepOutcome.Continue;
                }
                if (!string.Equals(receipt.ResponseText, result.ResponseText, StringComparison.Ordinal) ||
                    receipt.OutputHash != Digest(result.ResponseText))
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, "audio_batch_output_mismatch",
                        "录音批次成功回执正文或输出摘要与本次结果不一致；未标记成功，也不会触发下游合成",
                        "Failed", ct, stopTask: false, cancelDependentsOnly: true);
                    return StepOutcome.Continue;
                }
            }
            if (step.Kind == NoteDerivedStepKind.ImageEvidence &&
                result.ResponseText.Length > NoteDerivedLimits.MaxEvidenceCharsPerStep)
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "vision_evidence_too_large",
                    $"视觉证据 {result.ResponseText.Length} 字符，超过单步上限 {NoteDerivedLimits.MaxEvidenceCharsPerStep}；未截断",
                    "Blocked", ct);
                return StepOutcome.Stop;
            }
            if (step.Kind is not (NoteDerivedStepKind.ImageEvidence or
                    NoteDerivedStepKind.AudioBatchEvidence) &&
                result.ResponseText.Length > NoteDerivedLimits.MaxDerivedContentCharacters)
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "derived_content_too_large",
                    $"模型输出 {result.ResponseText.Length} 字符，超过派生产物上限 {NoteDerivedLimits.MaxDerivedContentCharacters}；未截断",
                    "Blocked", ct);
                return StepOutcome.Stop;
            }

            if (step.Kind == NoteDerivedStepKind.ImageEvidence)
            {
                var actual = await ReadActualImagesAsync(state, ct, receipt.Id);
                if (actual.Issues.Count > 0)
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, actual.Issues[0].Code,
                        actual.Issues[0].Message, "Failed", ct,
                        stopTask: false, cancelDependentsOnly: true);
                    return StepOutcome.Continue;
                }
                if (!BatchMatchesExactly(step, receipt.Id, actual.Images))
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, "vision_evidence_missing",
                        "视觉调用成功回执缺少完整的实际 AiCallImages；不按计划或清单伪造覆盖", "Failed", ct,
                        stopTask: false, cancelDependentsOnly: true);
                    return StepOutcome.Continue;
                }
            }
            else if (step.Kind != NoteDerivedStepKind.AudioBatchEvidence)
            {
                var resultHash = Digest(result.ResponseText);
                if (step.Current.ResultSha256 is { } checkpoint)
                {
                    if (checkpoint != resultHash)
                    {
                        await StopWithIssueAsync(owner, taskId, stepKey,
                            "synthesis_result_mismatch",
                            "合成成功回执正文与已保存的结果检查点不一致；不生成候选版本",
                            "Blocked", ct, receipt.Id);
                        return StepOutcome.Stop;
                    }
                }
                else
                {
                    // Persist the provider result digest before publication so a recovery
                    // cannot rewrite both the receipt body and OutputHash consistently.
                    await tasks.UpdateTaskAsync(owner, taskId, current =>
                    {
                        var stored = current.Steps.Single(x => x.Key == stepKey);
                        if (stored.Current.RequestId != requestId)
                            throw new InvalidDataException(
                                "Derived attempt changed while checkpointing its result");
                        stored.Current.ResultSha256 = resultHash;
                    }, ct);
                }
                var publishIssue = await PublishSynthesisVersionAsync(owner, taskId, stepKey,
                    requestId, receipt.Id, result.ResponseText, ct);
                if (publishIssue is not null)
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, publishIssue.Code,
                        publishIssue.Message, "Blocked", ct);
                    return StepOutcome.Stop;
                }
            }

            await tasks.UpdateTaskAsync(owner, taskId, current =>
            {
                var stored = current.Steps.Single(x => x.Key == stepKey);
                if (stored.Current.RequestId != requestId)
                    throw new InvalidDataException("Derived attempt changed while recording success");
                stored.Current.LogId = receipt.Id;
                stored.Current.Status = "Succeeded";
                stored.Current.ErrorCode = null;
                stored.Current.ErrorMessage = null;
                stored.Current.ResultSha256 = Digest(result.ResponseText);
            }, ct);
            var latest = await tasks.ReadTaskAsync(owner, taskId, ct);
            var coverageAfter = await BuildCoverageAsync(latest, ct);
            await tasks.UpdateTaskAsync(owner, taskId, current =>
            {
                current.Coverage = coverageAfter.Coverage;
                current.Issues = coverageAfter.Coverage.Issues.ToList();
            }, ct);
            return StepOutcome.Continue;
        }

        var status = result.Status switch
        {
            AiRequestStatus.Running => "Running",
            AiRequestStatus.Blocked => "Blocked",
            AiRequestStatus.Cancelled => "Cancelled",
            AiRequestStatus.Incomplete or AiRequestStatus.TimedOut => "Unknown",
            _ => "Failed"
        };
        var unknown = status == "Unknown";
        await StopWithIssueAsync(owner, taskId, stepKey,
            unknown ? "ai_outcome_unknown" : "ai_call_failed",
            unknown
                ? "调用结果未确认；不会自动重发，请显式确认可能重复收费后重试"
                : result.ErrorMessage ?? "模型调用失败",
            status, ct, result.LogId,
            stopTask: unknown || status == "Blocked",
            cancelDependentsOnly: !(unknown || status == "Blocked"));
        return unknown || status == "Blocked" ? StepOutcome.Stop : StepOutcome.Continue;
    }

    /// <summary>
    /// A synthesis result can be replayed long after its evidence was prepared. Re-read the
    /// current batch and synthesis receipts immediately before publishing, and hold relational
    /// row locks across the candidate append so a receipt changed or deleted in the
    /// prepare/send/apply window cannot produce a candidate version.
    /// </summary>
    private async Task<NoteDerivedIssueDto?> PublishSynthesisVersionAsync(Guid owner, Guid taskId,
        string stepKey, Guid requestId, Guid synthesisLogId, string resultText, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return await PublishSynthesisVersionCoreAsync(owner, taskId, stepKey, requestId,
                synthesisLogId, resultText, null, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var issue = await PublishSynthesisVersionCoreAsync(owner, taskId, stepKey, requestId,
                synthesisLogId, resultText, transaction, token);
            if (issue is null) await transaction.CommitAsync(token);
            return issue;
        }, ct);
    }

    private async Task<NoteDerivedIssueDto?> PublishSynthesisVersionCoreAsync(Guid owner, Guid taskId,
        string stepKey, Guid requestId, Guid synthesisLogId, string resultText,
        IDbContextTransaction? transaction, CancellationToken ct)
    {
        try
        {
            var state = await tasks.ReadTaskAsync(owner, taskId, ct);
            var step = state.Steps.SingleOrDefault(x => x.Key == stepKey);
            if (step is null || step.Current.RequestId != requestId)
                return Issue("publish_attempt_changed",
                    "发布前派生尝试已变化；未生成候选版本", stepKey);
            var first = await ValidatePublishEvidenceAsync(state, step, synthesisLogId,
                requestId, resultText, lockRows: transaction is not null, ct);
            if (first.Issue is not null) return first.Issue;

            if (publishBoundaryProbe is { } probe) await probe(ct);

            var finalState = await tasks.ReadTaskAsync(owner, taskId, ct);
            var finalStep = finalState.Steps.SingleOrDefault(x => x.Key == stepKey);
            if (finalStep is null || finalStep.Current.RequestId != requestId)
                return Issue("publish_attempt_changed",
                    "发布前派生尝试已变化；未生成候选版本", stepKey);
            if (finalState.StopRequested)
                return Issue("publish_stopped", "任务已停止，未生成候选版本", stepKey);
            var final = await ValidatePublishEvidenceAsync(finalState, finalStep, synthesisLogId,
                requestId, resultText, lockRows: false, ct);
            if (final.Issue is not null) return final.Issue;
            if (final.SynthesisReceipt is null ||
                !string.Equals(final.Fingerprint, first.Fingerprint, StringComparison.Ordinal))
                return Issue("publish_evidence_changed",
                    "发布边界内回执或证据指纹发生变化；未生成候选版本", stepKey);
            var coverage = await BuildCoverageAsync(finalState, ct, stepKey, stepKey);
            if (!coverage.Coverage.Complete)
                return Issue("synthesis_coverage_incomplete",
                    "材料覆盖尚未齐备，模型输出只保留为检查点，不发布为完整版本", stepKey);
            await AppendVersionAsync(finalState, finalStep, final.SynthesisReceipt, resultText,
                coverage.Coverage, ct);
            return null;
        }
        catch (DomainException error)
        {
            return Issue("publish_blocked", error.Message, stepKey);
        }
        catch (Exception error) when (error is IOException or DbUpdateException or DbException or
            TimeoutException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.LogWarning("Derived task {TaskId} step {StepKey} could not publish ({ErrorType})",
                taskId, stepKey, error.GetType().Name);
            return Issue("publish_checkpoint_unknown",
                "发布检查点读写中断，未确认候选版本；任务已停止", stepKey);
        }
    }

    private async Task<PublishEvidenceCheck> ValidatePublishEvidenceAsync(
        NoteDerivedTaskState state, NoteDerivedStepState step, Guid synthesisLogId,
        Guid requestId, string resultText, bool lockRows, CancellationToken ct)
    {
        var batches = state.Steps
            .Where(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        var receiptIds = batches.Where(x => x.Current.LogId.HasValue)
            .Select(x => x.Current.LogId!.Value)
            .Append(synthesisLogId)
            .Distinct()
            .ToArray();
        var receipts = await ReadReceiptsAsync(state.Owner, receiptIds, lockRows, ct);
        var keyed = new List<(string Key, AiRequestLogEntity Log)>(batches.Length + 1);
        foreach (var batch in batches)
        {
            if (batch.Current.Status != "Succeeded" ||
                batch.Current.LogId is not { } logId ||
                !receipts.TryGetValue(logId, out var batchReceipt) ||
                batchReceipt.Status != "succeeded")
                return PublishEvidenceCheck.Failed(Issue("audio_batch_receipt_missing",
                    "合成发布前找不到仍成功的录音批次回执；不生成候选版本", step.Key));
            var issues = NoteDerivedReceipts.ValidateAudioBatchReceipt(
                state, batch, batchReceipt);
            if (issues.Count > 0) return PublishEvidenceCheck.Failed(issues[0]);
            if (string.IsNullOrWhiteSpace(batchReceipt.ResponseText) ||
                batchReceipt.OutputHash != Digest(batchReceipt.ResponseText) ||
                batch.Current.ResultSha256 != Digest(batchReceipt.ResponseText))
                return PublishEvidenceCheck.Failed(Issue("audio_batch_output_mismatch",
                    "录音批次回执正文或输出摘要已与成功检查点不一致；不生成候选版本",
                    step.Key));
            var limit = batch.MaxOutputCharacters > 0
                ? batch.MaxOutputCharacters : NoteDerivedLimits.MaxEvidenceCharsPerStep;
            if (batchReceipt.ResponseText.Length > limit)
                return PublishEvidenceCheck.Failed(Issue("audio_batch_output_too_large",
                    "录音批次证据超过本批字符预算；不生成候选版本", step.Key));
            keyed.Add((batch.Key, batchReceipt));
        }

        if (!receipts.TryGetValue(synthesisLogId, out var synthesisReceipt))
            return PublishEvidenceCheck.Failed(Issue("synthesis_receipt_missing",
                "合成发布前找不到仍成功的合成回执；不生成候选版本", step.Key));
        if (synthesisReceipt.ClientRequestId != requestId)
            return PublishEvidenceCheck.Failed(Issue("synthesis_request_mismatch",
                "合成回执的 ClientRequestId 与当前步骤尝试不一致；不生成候选版本", step.Key));
        var expected = await BuildSynthesisInputHashAsync(state, step, ct);
        if (expected.Issue is not null) return PublishEvidenceCheck.Failed(expected.Issue);
        var synthesisIssues = NoteDerivedReceipts.ValidateSynthesisReceipt(
            state, step, synthesisReceipt, resultText, expected.InputSha256, synthesisLogId);
        if (synthesisIssues.Count > 0)
            return PublishEvidenceCheck.Failed(synthesisIssues[0]);
        var wireIssues = NoteDerivedReceipts.ValidateSynthesisAudioWire(
            state, step, synthesisReceipt);
        if (wireIssues.Count > 0) return PublishEvidenceCheck.Failed(wireIssues[0]);
        keyed.Add((step.Key, synthesisReceipt));
        return new(PublishFingerprint(keyed, resultText, expected.InputSha256!),
            synthesisReceipt, null);
    }

    private async Task<SynthesisInputCheck> BuildSynthesisInputHashAsync(
        NoteDerivedTaskState state, NoteDerivedStepState step, CancellationToken ct)
    {
        var evidence = await LoadEvidenceAsync(state, ct);
        if (evidence.Issue is not null) return new(null, evidence.Issue);
        var prompt = await BuildSynthesisPromptAsync(state, step, evidence.Items, ct);
        if (prompt.Issue is not null) return new(null, prompt.Issue);
        return new(NoteDerivedReceipts.SynthesisInputFingerprint(state, step, prompt.Prompt!), null);
    }

    private async Task<Dictionary<Guid, AiRequestLogEntity>> ReadReceiptsAsync(Guid owner,
        Guid[] ids, bool lockRows, CancellationToken ct)
    {
        if (ids.Length == 0) return new Dictionary<Guid, AiRequestLogEntity>();
        if (lockRows && db.Database.IsRelational())
        {
            var locked = await db.AiRequestLogs
                .FromSqlInterpolated(
                    $"SELECT * FROM ai_request_logs WHERE user_id = {owner} AND id = ANY({ids}) FOR SHARE")
                .AsNoTracking()
                .ToListAsync(ct);
            return locked.ToDictionary(x => x.Id);
        }
        var rows = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == owner && ids.Contains(x.Id))
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id);
    }

    private static string PublishFingerprint(
        IReadOnlyList<(string Key, AiRequestLogEntity Log)> receipts, string resultText,
        string expectedInputHash) =>
        Digest(System.Text.Json.JsonSerializer.Serialize(new
        {
            resultSha256 = Digest(resultText),
            expectedInputHash,
            receipts = receipts.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
            {
                x.Key,
                x.Log.Id,
                x.Log.UserId,
                x.Log.ClientRequestId,
                x.Log.Status,
                x.Log.Purpose,
                x.Log.SourceObjectType,
                x.Log.SourceObjectId,
                x.Log.ModelProfileId,
                x.Log.ModelRevision,
                x.Log.ModelEndpoint,
                x.Log.InputHash,
                x.Log.OutputHash,
                x.Log.ResponseText,
                x.Log.MetadataJson,
                x.Log.RequestMessagesJson
            })
        }));

    private async Task AppendVersionAsync(NoteDerivedTaskState state, NoteDerivedStepState step,
        Pim.Infrastructure.Data.Entities.AiRequestLogEntity receipt, string content,
        NoteDerivedCoverageDto coverage, CancellationToken ct)
    {
        var kind = step.Kind == NoteDerivedStepKind.RecordSynthesis
            ? NoteDerivedArtifactKind.CompleteRecord : NoteDerivedArtifactKind.IndependentAnalysis;
        var artifactId = kind == NoteDerivedArtifactKind.CompleteRecord
            ? state.CompleteRecordArtifactId : state.IndependentAnalysisArtifactId;
        var artifact = await tasks.GetArtifactAsync(state.Owner, artifactId, ct);
        Guid? recordVersion = null;
        if (kind == NoteDerivedArtifactKind.IndependentAnalysis)
        {
            var record = state.Steps.Single(x => x.Kind == NoteDerivedStepKind.RecordSynthesis);
            if (record.Current.Status != "Succeeded" || record.Current.LogId is null)
                throw new DomainException(40901, "完整记录版本尚未成功，独立分析不能发布");
            recordVersion = VersionId(state.Request.TaskId, record.Current.RequestId,
                NoteDerivedArtifactKind.CompleteRecord);
        }
        var draft = new NoteDerivedVersionDraft(
            VersionId(state.Request.TaskId, step.Current.RequestId, kind),
            kind,
            artifact.HeadVersionId,
            recordVersion,
            state.Request.VersionId,
            state.Request.TaskId,
            content,
            state.InputSha256,
            state.ManifestSha256,
            state.Plan.PlanVersion,
            NoteDerivedPrompts.PromptVersion,
            receipt.Id,
            state.Request.Consent.ModelId,
            state.Request.Consent.ModelRevision,
            state.Request.Consent.Endpoint,
            coverage);
        // AppendGenerated only creates a candidate; the store never advances the human head here.
        _ = await tasks.AppendGeneratedAsync(state.Owner, artifactId, draft, ct);
    }

    private async Task<SourceCheck> ValidateSourcesAsync(NoteDerivedTaskState state, CancellationToken ct)
    {
        var issues = new List<NoteDerivedIssueDto>();
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == state.Owner && x.IsActive, ct))
            issues.Add(Issue("owner_inactive", "发起账号已停用，派生任务未发送"));

        var consent = state.Request.Consent;
        var profile = await db.AiModelProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == consent.ModelId, ct);
        var runtimeEnabled = await db.AiModelRuntime.AsNoTracking().AnyAsync(x => x.Enabled, ct);
        var required = state.Plan.Input.Images.Count > 0 ? 3 : 1;
        if (consent.Confirmed != true || !runtimeEnabled || profile is null || !profile.Enabled ||
            (profile.Capabilities & required) != required || profile.Capabilities == 4)
            issues.Add(Issue("destination_unavailable", "生成模型、能力或 SDK 运行配置当前不可用"));
        else if (consent.ModelRevision != profile.Revision ||
            !string.Equals(consent.Endpoint, profile.BaseUrl, StringComparison.Ordinal))
            issues.Add(Issue("destination_changed", "生成模型 revision 或目标地址已变化；未发送"));

        NoteMaterialVersionDto version;
        try { version = await materials.GetAsync(state.Owner, state.Request.VersionId, ct); }
        catch (DomainException error)
        {
            issues.Add(Issue("material_unavailable", error.Message));
            return new(issues, new HashSet<Guid>());
        }
        if (NoteMaterialManifestValidator.Hash(version.InputJson) != state.InputSha256 ||
            NoteMaterialManifestValidator.Hash(version.ManifestJson) != state.ManifestSha256)
            issues.Add(Issue("material_changed", "固定材料在任务创建后发生变化；未发送"));

        var transcripts = await ValidateTranscriptsAsync(state, ct);
        issues.AddRange(transcripts.Issues);
        return new(NoteDerivedCoverage.DistinctIssues(issues), transcripts.CompletedLogIds);
    }

    private async Task<TranscriptCheck> ValidateTranscriptsAsync(NoteDerivedTaskState state,
        CancellationToken ct)
    {
        var issues = new List<NoteDerivedIssueDto>();
        var completed = new HashSet<Guid>();
        if (state.Plan.Input.Transcripts.Count == 0)
        {
            if (state.Plan.Input.Audio.Count == 0 &&
                (state.Request.AsrTaskId is not null ||
                 state.Plan.Input.AsrTaskId is not null))
                issues.Add(Issue("asr_transcripts_missing",
                    "材料绑定了 ASR 但固定派生快照没有任何成功转写；已阻断，不会以空转写继续"));
            return new(completed, issues);
        }
        if (state.Request.AsrTaskId is null)
        {
            issues.Add(Issue("asr_required", "材料包含录音但派生任务未绑定 ASR 任务"));
            return new(completed, issues);
        }

        NoteAsrState asr;
        try { asr = await asrTasks.ReadAsync(state.Owner, state.Request.AsrTaskId.Value, ct); }
        catch (DomainException error)
        {
            issues.Add(Issue("asr_missing", error.Message));
            return new(completed, issues);
        }
        if (asr.Request.ExpectedUserId != state.Owner ||
            asr.Request.VersionId != state.Request.VersionId ||
            asr.InputSha256 != state.InputSha256 || asr.ManifestSha256 != state.ManifestSha256)
            issues.Add(Issue("asr_source_mismatch", "ASR 任务不属于同一 owner、材料版本或固定摘要"));

        var bySegment = asr.Steps.ToDictionary(x => x.Index);
        var logIds = state.Plan.Input.Transcripts.Where(x => x.LogId.HasValue).Select(x => x.LogId!.Value).ToArray();
        var receipts = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == state.Owner && logIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var proofAttempt = asr.AttemptId is { } currentAttemptId
            ? await db.NoteAsrAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == currentAttemptId && x.OwnerId == state.Owner, ct)
            : null;
        var acceptedEvidence = proofAttempt is null
            ? []
            : await NoteAsrAcceptedEvidence.LoadAsync(db, state.Owner, proofAttempt, asr, ct);
        foreach (var transcript in state.Plan.Input.Transcripts.OrderBy(x => x.Ordinal))
        {
            if (!bySegment.TryGetValue(transcript.AsrSegmentIndex, out var slice) ||
                slice.RecordingId != transcript.RecordingId ||
                slice.RecordingSegment != transcript.RecordingSegment ||
                slice.ChunkIndex != transcript.ChunkIndex ||
                slice.FileIndex != transcript.SourceFileIndex || slice.Path != transcript.SourcePath ||
                slice.Sha256 != transcript.SourceSha256 ||
                slice.SourceOffset != transcript.SourceOffsetSamples ||
                slice.Samples != transcript.Samples)
            {
                issues.Add(Issue("asr_receipt_mismatch",
                    "ASR 片段 owner、版本、来源、摘要或覆盖范围不一致",
                    stepKey: $"asr:{transcript.Ordinal + 1:D5}"));
                continue;
            }
            if (transcript.EvidenceId is { } evidenceId)
            {
                if (!acceptedEvidence.TryGetValue(transcript.AsrSegmentIndex, out var proof) ||
                    proof.Id != evidenceId || proof.AttemptId != asr.AttemptId ||
                    proof.RecordingId != transcript.RecordingId ||
                    proof.RecordingSegment != transcript.RecordingSegment ||
                    proof.ChunkIndex != transcript.ChunkIndex ||
                    proof.SourceFileIndex != transcript.SourceFileIndex ||
                    proof.SourceSha256 != transcript.SourceSha256 ||
                    proof.SourceOffsetSamples != transcript.SourceOffsetSamples ||
                    proof.Samples != transcript.Samples ||
                    !string.Equals(proof.ResultText, transcript.Text, StringComparison.Ordinal) ||
                    !string.Equals(proof.ProviderHandle, transcript.ProviderHandle, StringComparison.Ordinal) ||
                    !string.Equals(proof.ReceiptKind, transcript.ReceiptKind, StringComparison.Ordinal))
                {
                    issues.Add(Issue("asr_evidence_mismatch",
                        "本地/Batch 分片证据 owner、attempt、来源、覆盖或结果不一致",
                        stepKey: $"asr:{transcript.Ordinal + 1:D5}"));
                    continue;
                }
                completed.Add(proof.Id);
                continue;
            }
            if (transcript.LogId is not { } logId || slice.Current.LogId != logId ||
                !receipts.TryGetValue(logId, out var receipt) ||
                receipt.ClientRequestId != slice.Current.RequestId ||
                receipt.Purpose != "ai.asr-call" ||
                receipt.SourceObjectType != "native-note-asr-task" ||
                receipt.SourceObjectId != state.Request.AsrTaskId.Value.ToString("D") ||
                receipt.Status != "succeeded" ||
                !string.Equals(receipt.ResponseText, transcript.Text, StringComparison.Ordinal))
            {
                issues.Add(Issue("asr_receipt_mismatch",
                    "ASR 片段 owner、版本、来源、摘要、尝试或成功回执不一致",
                    stepKey: $"asr:{transcript.Ordinal + 1:D5}"));
                continue;
            }
            completed.Add(logId);
        }
        var successful = asr.Steps.Where(x => x.Current.Status == "Succeeded").ToArray();
        if (successful.Length != state.Plan.Input.Transcripts.Count ||
            successful.Any(step => state.Plan.Input.Transcripts.All(x =>
                x.LogId != step.Current.LogId && x.EvidenceId != step.Current.EvidenceId)))
            issues.Add(Issue("asr_success_omitted", "ASR 中存在未纳入固定快照的成功片段"));
        return new(completed, issues);
    }

    private async Task<CoverageCheck> BuildCoverageAsync(NoteDerivedTaskState state, CancellationToken ct,
        string? successfulStepKey = null, string? subjectStepKey = null)
    {
        var transcripts = await ValidateTranscriptsAsync(state, ct);
        var issues = new List<NoteDerivedIssueDto>(state.Plan.InitialCoverage.Issues);
        issues.AddRange(transcripts.Issues);
        var relevant = subjectStepKey is null ? null : UpstreamStepKeys(state, subjectStepKey);
        foreach (var step in state.Steps.Where(x => x.Key != successfulStepKey &&
            (x.Current.Status is "Failed" or "Unknown" or "Blocked" or "Cancelled") &&
            (relevant is null || relevant.Contains(x.Key))))
        {
            issues.Add(Issue("step_" + step.Current.Status.ToLowerInvariant(),
                step.Current.ErrorMessage ?? "派生步骤未成功", stepKey: step.Key));
        }
        var actual = await ReadActualImagesAsync(state, ct);
        issues.AddRange(actual.Issues);
        var planned = state.Plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var states = state.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal);
        return new(NoteDerivedCoverage.Reconcile(state.Plan.Input, planned, states, actual.Images,
            transcripts.CompletedLogIds, NoteDerivedCoverage.DistinctIssues(issues)));
    }

    private static HashSet<string> UpstreamStepKeys(NoteDerivedTaskState state, string stepKey)
    {
        var byKey = state.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var relevant = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(stepKey);
        while (pending.TryPop(out var key))
        {
            if (!relevant.Add(key) || !byKey.TryGetValue(key, out var step)) continue;
            foreach (var dependency in step.DependsOn)
                pending.Push(dependency);
        }
        return relevant;
    }

    private async Task<ActualImageCheck> ReadActualImagesAsync(NoteDerivedTaskState state,
        CancellationToken ct, Guid? pendingLogId = null)
    {
        var stepLogIds = state.Steps.Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence &&
                x.Current.LogId is not null)
            .Select(x => x.Current.LogId!.Value).ToHashSet();
        if (pendingLogId is { } pending) stepLogIds.Add(pending);
        if (stepLogIds.Count == 0) return new([], []);
        var logs = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == state.Owner && stepLogIds.Contains(x.Id) &&
                x.Purpose == NoteDerivedPrompts.VisionPurpose &&
                x.SourceObjectType == NoteDerivedPrompts.SourceObjectType &&
                x.SourceObjectId == state.Request.TaskId.ToString("D") && x.Status == "succeeded")
            .ToListAsync(ct);
        var issues = new List<NoteDerivedIssueDto>();
        var verifiedLogIds = new HashSet<Guid>();
        foreach (var log in logs)
        {
            var step = state.Steps.SingleOrDefault(x => x.Current.LogId == log.Id);
            if (step is not null && log.ClientRequestId != step.Current.RequestId)
                issues.Add(Issue("vision_receipt_mismatch",
                    "视觉日志的请求身份与当前步骤尝试不一致", stepKey: step.Key));
            else if (!CompleteVisionWireMetadata(log.MetadataJson))
                issues.Add(Issue("vision_wire_incomplete",
                    "视觉成功回执缺少 providerAttempted、requestBodyCaptured、responseBodyComplete 或 imageInputsStored 证据",
                    stepKey: step?.Key));
            else verifiedLogIds.Add(log.Id);
        }
        foreach (var expectedLogId in stepLogIds.Where(id => !verifiedLogIds.Contains(id)))
        {
            var step = state.Steps.SingleOrDefault(x => x.Current.LogId == expectedLogId);
            if (step is not null)
                issues.Add(Issue("vision_receipt_missing",
                    "当前视觉步骤的成功日志不可读或缺少完整 wire 证据", stepKey: step.Key));
        }
        if (verifiedLogIds.Count == 0) return new([], NoteDerivedCoverage.DistinctIssues(issues));
        var rows = await db.AiCallImages.AsNoTracking()
            .Where(x => verifiedLogIds.Contains(x.LogId))
            .OrderBy(x => x.LogId).ThenBy(x => x.Index)
            .Select(x => new NoteDerivedActualImage(x.LogId, x.Index, x.Name, x.ContentType,
                x.SizeBytes, x.Sha256, x.ObjectKey != null && x.ProviderKey != null))
            .ToListAsync(ct);
        if (rows.Any(x => !x.Stored))
            issues.Add(Issue("vision_image_not_stored",
                "AiCallImages 存在未持久化的原图对象，不能计入覆盖"));
        return new(rows, NoteDerivedCoverage.DistinctIssues(issues));
    }

    private static bool BatchMatchesExactly(NoteDerivedStepState step, Guid logId,
        IReadOnlyList<NoteDerivedActualImage> actual)
    {
        var expected = step.Images.OrderBy(x => x.Ordinal).ToArray();
        var rows = actual.Where(x => x.LogId == logId).OrderBy(x => x.Index).ToArray();
        if (rows.Length != expected.Length) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            var row = rows[index];
            var image = expected[index];
            if (row.Index != index || !row.Stored ||
                row.Name != NoteDerivedPrompts.ImageName(image) ||
                row.Sha256 != image.Sha256 || row.SizeBytes != image.SizeBytes ||
                row.ContentType != image.ContentType) return false;
        }
        return true;
    }

    private static bool CompleteVisionWireMetadata(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            return Flag(root, "providerAttempted") && Flag(root, "requestBodyCaptured") &&
                Flag(root, "responseBodyComplete") && Flag(root, "imageInputsStored");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or
            InvalidOperationException) { return false; }

        static bool Flag(System.Text.Json.JsonElement root, string name) =>
            root.TryGetProperty(name, out var item) &&
            item.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private async Task<EvidenceCheck> LoadEvidenceAsync(NoteDerivedTaskState state, CancellationToken ct)
    {
        var issues = new List<NoteDerivedIssueDto>();
        var items = new List<NoteDerivedEvidence>();
        var actual = await ReadActualImagesAsync(state, ct);
        issues.AddRange(actual.Issues);
        var vision = state.Steps.Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence).ToArray();
        var audioBatches = state.Steps.Where(x =>
            x.Kind == NoteDerivedStepKind.AudioBatchEvidence).ToArray();
        var logIds = vision.Concat(audioBatches)
            .Where(x => x.Current.Status == "Succeeded" && x.Current.LogId is not null)
            .Select(x => x.Current.LogId!.Value).ToArray();
        var receipts = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == state.Owner && logIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        foreach (var step in vision)
        {
            if (step.Current.Status != "Succeeded" || step.Current.LogId is not { } logId ||
                !receipts.TryGetValue(logId, out var receipt) ||
                receipt.Status != "succeeded" || string.IsNullOrWhiteSpace(receipt.ResponseText) ||
                !BatchMatchesExactly(step, logId, actual.Images))
            {
                issues.Add(Issue("vision_evidence_missing", "图片批次缺少成功回执正文", stepKey: step.Key));
                continue;
            }
            if (receipt.ResponseText.Length > NoteDerivedLimits.MaxEvidenceCharsPerStep)
            {
                issues.Add(Issue("vision_evidence_too_large",
                    "图片证据超过单步字符上限；未截断", stepKey: step.Key));
                continue;
            }
            items.Add(new(step.Key, receipt.Id, receipt.ResponseText, step.Images));
        }
        foreach (var step in audioBatches)
        {
            if (step.Current.Status != "Succeeded" || step.Current.LogId is not { } logId ||
                !receipts.TryGetValue(logId, out var receipt) ||
                receipt.Status != "succeeded" ||
                receipt.Purpose != NoteDerivedPrompts.AudioBatchPurpose ||
                string.IsNullOrWhiteSpace(receipt.ResponseText) ||
                receipt.OutputHash != Digest(receipt.ResponseText) ||
                step.Current.ResultSha256 != Digest(receipt.ResponseText))
            {
                issues.Add(Issue("audio_batch_evidence_missing",
                    "录音批次缺少可核验的成功回执正文", stepKey: step.Key));
                continue;
            }
            var limit = step.MaxOutputCharacters > 0
                ? step.MaxOutputCharacters : NoteDerivedLimits.MaxEvidenceCharsPerStep;
            if (receipt.ResponseText.Length > limit)
            {
                issues.Add(Issue("audio_batch_evidence_too_large",
                    "录音批次证据超过本批字符预算；未截断", stepKey: step.Key));
                continue;
            }
            var wireIssues = NoteDerivedReceipts.ValidateAudioBatchReceipt(state, step, receipt);
            if (wireIssues.Count > 0)
            {
                // A batch marked succeeded without a verifiable provider wire snapshot must
                // never feed record/analysis. Otherwise a tampered or incomplete receipt
                // could still trigger a paid synthesis call.
                issues.AddRange(wireIssues);
                continue;
            }
            items.Add(new(step.Key, receipt.Id, receipt.ResponseText, []));
        }
        return issues.Count == 0 ? new(items, null) : new(items, issues[0]);
    }

    private async Task<Pim.Infrastructure.Data.Entities.AiRequestLogEntity?> FindReceiptAsync(
        Guid owner, Guid requestId, CancellationToken ct) =>
        await db.AiRequestLogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == owner && x.ClientRequestId == requestId, ct);

    private async Task<Pim.Infrastructure.Data.Entities.AiRequestLogEntity?> FindReceiptByIdAsync(
        Guid owner, Guid logId, CancellationToken ct) =>
        await db.AiRequestLogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == owner && x.Id == logId, ct);

    private static bool ReceiptMatches(NoteDerivedStepState step,
        Pim.Infrastructure.Data.Entities.AiRequestLogEntity receipt, NoteDerivedTaskState state,
        out string error)
    {
        var purpose = step.Kind switch
        {
            NoteDerivedStepKind.ImageEvidence => NoteDerivedPrompts.VisionPurpose,
            NoteDerivedStepKind.AudioBatchEvidence => NoteDerivedPrompts.AudioBatchPurpose,
            NoteDerivedStepKind.RecordSynthesis => NoteDerivedPrompts.RecordPurpose,
            _ => NoteDerivedPrompts.AnalysisPurpose
        };
        var consent = state.Request.Consent;
        if (receipt.Purpose == purpose &&
            receipt.SourceObjectType == NoteDerivedPrompts.SourceObjectType &&
            receipt.SourceObjectId == state.Request.TaskId.ToString("D") &&
            receipt.ModelProfileId == consent.ModelId &&
            receipt.ModelRevision == consent.ModelRevision &&
            string.Equals(receipt.ModelEndpoint, consent.Endpoint, StringComparison.Ordinal))
        {
            error = "";
            return true;
        }
        error = "调用请求标识已绑定到其他用途、来源或模型目标；未自动重发";
        return false;
    }

    private async Task<bool> IsStaleWithoutReceiptAsync(NoteDerivedTaskState state,
        NoteDerivedStepState step, CancellationToken ct)
    {
        var timeout = await db.AiModelProfiles.AsNoTracking()
            .Where(x => x.Id == state.Request.Consent.ModelId)
            .Select(x => (int?)x.TimeoutSeconds)
            .SingleOrDefaultAsync(ct);
        var limit = timeout is > 0 ? TimeSpan.FromSeconds(timeout.Value + 60) : UnknownWithoutReceiptAfter;
        return step.Current.Status == "Unknown" || state.UpdatedAt + limit <= DateTimeOffset.UtcNow;
    }

    private async Task StopWithIssueAsync(Guid owner, Guid taskId, string stepKey, string code,
        string message, string status, CancellationToken ct, Guid? logId = null,
        bool stopTask = true, bool cancelDependentsOnly = false)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tasks.UpdateTaskAsync(owner, taskId, current =>
        {
            var step = current.Steps.Single(x => x.Key == stepKey);
            if (logId is { } receiptId) step.Current.LogId = receiptId;
            if (status != "Running") step.Current.Status = status;
            step.Current.ErrorCode = code;
            step.Current.ErrorMessage = message;
            if (status is "Failed" or "Unknown" or "Cancelled" or "Blocked")
            {
                if (cancelDependentsOnly)
                {
                    var affected = new HashSet<string>(StringComparer.Ordinal) { step.Key };
                    var changed = true;
                    while (changed)
                    {
                        changed = false;
                        foreach (var queued in current.Steps.Where(x =>
                                     x.Current.Status == "Queued" &&
                                     x.DependsOn.Any(affected.Contains)))
                        {
                            queued.Current.Status = "Cancelled";
                            queued.Current.ErrorCode = "upstream_not_succeeded";
                            queued.Current.ErrorMessage = "前序步骤未成功，未发送后续外部调用";
                            affected.Add(queued.Key);
                            changed = true;
                        }
                    }
                }
                else
                {
                    foreach (var queued in current.Steps.Where(x => x.Current.Status == "Queued"))
                    {
                        queued.Current.Status = "Cancelled";
                        queued.Current.ErrorCode = "upstream_not_succeeded";
                        queued.Current.ErrorMessage = "前序步骤未成功，未发送后续外部调用";
                    }
                }
            }
            var issue = new NoteDerivedIssueDto(code, message, StepKey: stepKey);
            var issues = current.Issues.Append(issue)
                .DistinctBy(x => (x.Code, x.Message, x.SourcePath, x.BlockId, x.StepKey)).ToList();
            current.Issues = issues;
            current.Coverage = current.Coverage with { Complete = false, Issues = issues };
            if (status == "Blocked" && stopTask) current.StopRequested = true;
        }, budget.Token);
    }

    private async Task CancelQueuedAfterStopAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tasks.UpdateTaskAsync(owner, taskId, current =>
        {
            foreach (var step in current.Steps.Where(x => x.Current.Status == "Queued"))
            {
                step.Current.Status = "Cancelled";
                step.Current.ErrorCode = "stopped_by_user";
                step.Current.ErrorMessage = "任务已停止，此步骤尚未发送";
            }
        }, budget.Token);
    }

    private static Guid VersionId(Guid taskId, Guid requestId, NoteDerivedArtifactKind kind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{taskId:D}:{requestId:D}:{kind}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static NoteDerivedIssueDto Issue(string code, string message, string? stepKey = null,
        string? sourcePath = null) => new(code, message, sourcePath, StepKey: stepKey);

    private sealed record PreparedCall(string? Prompt, IReadOnlyList<AiVisionImageRequest> Images,
        IReadOnlyList<AiAudioContextInput> Audio, int InputCharacters, NoteDerivedIssueDto? Issue)
    {
        internal static PreparedCall Vision(string prompt, IReadOnlyList<AiVisionImageRequest> images,
            int inputCharacters) => new(prompt, images, [], inputCharacters, null);
        internal static PreparedCall Text(string prompt, int inputCharacters,
            IReadOnlyList<AiAudioContextInput>? audio = null) =>
            new(prompt, [], audio ?? [], inputCharacters, null);
        internal static PreparedCall Failed(NoteDerivedIssueDto issue) => new(null, [], [], 0, issue);
    }

    private sealed record SourceCheck(IReadOnlyList<NoteDerivedIssueDto> Issues,
        IReadOnlySet<Guid> CompletedLogIds);
    private sealed record TranscriptCheck(IReadOnlySet<Guid> CompletedLogIds,
        IReadOnlyList<NoteDerivedIssueDto> Issues);
    private sealed record CoverageCheck(NoteDerivedCoverageDto Coverage);
    private sealed record ActualImageCheck(IReadOnlyList<NoteDerivedActualImage> Images,
        IReadOnlyList<NoteDerivedIssueDto> Issues);
    private sealed record EvidenceCheck(IReadOnlyList<NoteDerivedEvidence> Items,
        NoteDerivedIssueDto? Issue);
    private sealed record AudioCheck(IReadOnlyList<AiAudioContextInput> Items,
        NoteDerivedIssueDto? Issue);
    private sealed record SynthesisPromptCheck(string? Prompt, NoteDerivedIssueDto? Issue);
    private sealed record SynthesisInputCheck(string? InputSha256, NoteDerivedIssueDto? Issue);
    private sealed record PublishEvidenceCheck(string? Fingerprint,
        AiRequestLogEntity? SynthesisReceipt, NoteDerivedIssueDto? Issue)
    {
        internal static PublishEvidenceCheck Failed(NoteDerivedIssueDto issue) =>
            new(null, null, issue);
    }

    private enum StepOutcome
    {
        Continue,
        Stop,
        Deferred
    }
}
