using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;

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
    PimDbContext db,
    AiTextCallService calls,
    ILogger<NoteDerivedJob> logger)
{
    private static readonly TimeSpan UnknownWithoutReceiptAfter = TimeSpan.FromMinutes(15);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        using var lease = tasks.TryRunLease(owner, taskId);
        if (lease is null) return;

        // Fairness bound mirrors ASR. Every exit is a durable checkpoint and never allocates a new attempt.
        for (var processed = 0; processed < 64; processed++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await tasks.ReadTaskAsync(owner, taskId, ct);
            if (state.Blocked) return;
            if (state.StopRequested && state.Steps.All(x => x.Current.Status != "Running"))
            {
                if (state.Steps.Any(x => x.Current.Status == "Queued"))
                    await CancelQueuedAfterStopAsync(owner, taskId, ct);
                return;
            }

            var step = ReadyStep(state);
            if (step is null) return;
            var requestId = step.Current.RequestId;

            await AiCallRecovery.RecoverExpiredAsync(db, ct);
            var receipt = await FindReceiptAsync(owner, requestId, ct);
            if (receipt is not null)
            {
                if (!ReceiptMatches(step, receipt, state, out var bindingError))
                {
                    await StopWithIssueAsync(owner, taskId, step.Key, "ai_receipt_source_mismatch",
                        bindingError, "Blocked", ct);
                    return;
                }
                if (receipt.Status == "running")
                {
                    if (receipt.ExecutionExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                        await StopWithIssueAsync(owner, taskId, step.Key, "ai_outcome_unknown",
                            "调用执行期限已过，结果无法确认；不会自动重发，请显式确认可能重复收费后重试",
                            "Unknown", ct);
                    return;
                }

                var result = AiCallAudit.Result(receipt, true);
                var completed = await ApplyResultAsync(owner, taskId, step.Key, requestId, result, receipt, ct);
                if (!completed) return;
                continue;
            }

            if (step.Current.Status is "Running" or "Unknown")
            {
                if (await IsStaleWithoutReceiptAsync(state, step, ct))
                {
                    await StopWithIssueAsync(owner, taskId, step.Key, "ai_outcome_unknown",
                        "已持久化发送意图但未找到调用回执，结果无法确认；不会自动重发，请显式确认可能重复收费后重试",
                        "Unknown", ct);
                }
                return;
            }
            if (state.StopRequested || step.Current.Status != "Queued") return;

            var prepared = await PrepareAsync(state, step, ct);
            if (prepared.Issue is not null)
            {
                await StopWithIssueAsync(owner, taskId, step.Key, prepared.Issue.Code,
                    prepared.Issue.Message, "Blocked", ct);
                return;
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
            if ((await tasks.ReadTaskAsync(owner, taskId, ct)).StopRequested) return;

            try
            {
                var result = await SendAsync(owner, state, step, prepared, requestId, ct);
                var receiptAfterSend = await FindReceiptAsync(owner, requestId, ct);
                var completed = await ApplyResultAsync(owner, taskId, step.Key, requestId, result,
                    receiptAfterSend, ct);
                if (!completed) return;
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
                return;
            }
            catch (Exception error) when (error is IOException or DbUpdateException or
                System.Text.Json.JsonException or InvalidDataException)
            {
                logger.LogWarning("Derived task {TaskId} step {StepKey} lost its checkpoint ({ErrorType})",
                    taskId, step.Key, error.GetType().Name);
                await StopWithIssueAsync(owner, taskId, step.Key, "checkpoint_unknown",
                    "材料或检查点读写中断，结果无法确认；任务已停止且不会自动重发", "Unknown", ct);
                return;
            }
        }
    }

    private static NoteDerivedStepState? ReadyStep(NoteDerivedTaskState state)
    {
        var running = state.Steps.FirstOrDefault(x => x.Current.Status == "Running");
        if (running is not null) return running;
        if (state.StopRequested) return null;
        return state.Steps.FirstOrDefault(candidate => candidate.Current.Status == "Queued" &&
            candidate.DependsOn.All(key => state.Steps.Any(step =>
                step.Key == key && step.Current.Status == "Succeeded")));
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

        var evidence = await LoadEvidenceAsync(state, ct);
        if (evidence.Issue is not null) return PreparedCall.Failed(evidence.Issue);
        if (step.Kind == NoteDerivedStepKind.RecordSynthesis)
        {
            var coverage = await BuildCoverageAsync(state, ct);
            if (!coverage.Coverage.Complete)
                return PreparedCall.Failed(Issue("record_coverage_incomplete",
                    "完整记录只能在全部 Markdown、ASR、照片和手写预览覆盖齐备后生成", step.Key));
            var prompt = NoteDerivedPrompts.Record(state.Plan.Input, evidence.Items);
            if (prompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
                return PreparedCall.Failed(Issue("record_input_too_large",
                    $"完整记录输入 {prompt.Length} 字符，超过 SDK 实际上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                    step.Key));
            return PreparedCall.Text(prompt, prompt.Length);
        }

        var record = state.Steps.Single(x => x.Kind == NoteDerivedStepKind.RecordSynthesis);
        if (record.Current.Status != "Succeeded" || record.Current.LogId is null)
            return PreparedCall.Failed(Issue("record_version_missing",
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
            return PreparedCall.Failed(Issue("record_version_missing",
                "完整记录的具体版本尚不可读，独立分析未发送", step.Key));
        }
        if (recordVersion.Kind != NoteDerivedArtifactKind.CompleteRecord ||
            recordVersion.GenerationTaskId != state.Request.TaskId ||
            recordVersion.SourceVersionId != state.Request.VersionId ||
            recordVersion.InputSha256 != state.InputSha256 ||
            recordVersion.ManifestSha256 != state.ManifestSha256 ||
            recordVersion.AiCallLogId != record.Current.LogId ||
            string.IsNullOrWhiteSpace(recordVersion.ContentMarkdown))
            return PreparedCall.Failed(Issue("record_version_invalid",
                "完整记录具体版本的身份、来源或正文校验失败，独立分析未发送", step.Key));
        var analysis = NoteDerivedPrompts.Analysis(state.Plan.Input, recordVersionId,
            recordVersion.ContentMarkdown, evidence.Items);
        if (analysis.Length > NoteDerivedLimits.MaxSynthesisInputChars)
            return PreparedCall.Failed(Issue("analysis_input_too_large",
                $"独立分析输入 {analysis.Length} 字符，超过 SDK 实际上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                step.Key));
        return PreparedCall.Text(analysis, analysis.Length);
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
        var purpose = step.Kind == NoteDerivedStepKind.RecordSynthesis
            ? NoteDerivedPrompts.RecordPurpose : NoteDerivedPrompts.AnalysisPurpose;
        return await calls.CallForOwnerAsync(owner, text,
            new AiVisionDestinationConsent(consent.ModelId, consent.ModelRevision, consent.Endpoint, consent.Confirmed),
            NoteDerivedPrompts.SourceObjectType, state.Request.TaskId.ToString("D"), purpose, ct);
    }

    private async Task<bool> ApplyResultAsync(Guid owner, Guid taskId, string stepKey, Guid requestId,
        AiTextCallResultDto result, Pim.Infrastructure.Data.Entities.AiRequestLogEntity? receipt,
        CancellationToken ct)
    {
        var state = await tasks.ReadTaskAsync(owner, taskId, ct);
        var step = state.Steps.Single(x => x.Key == stepKey);
        if (step.Current.RequestId != requestId)
            throw new InvalidDataException("Derived attempt changed while applying its result");

        if (result.Status == AiRequestStatus.Succeeded)
        {
            if (receipt is null || receipt.Status != "succeeded")
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "ai_receipt_missing",
                    "成功结果缺少可核验的真实回执", "Unknown", ct);
                return false;
            }
            if (string.IsNullOrWhiteSpace(result.ResponseText))
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "ai_output_empty",
                    "模型成功回执没有可用正文；未生成派生版本", "Failed", ct);
                return false;
            }
            if (step.Kind == NoteDerivedStepKind.ImageEvidence &&
                result.ResponseText.Length > NoteDerivedLimits.MaxEvidenceCharsPerStep)
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "vision_evidence_too_large",
                    $"视觉证据 {result.ResponseText.Length} 字符，超过单步上限 {NoteDerivedLimits.MaxEvidenceCharsPerStep}；未截断",
                    "Blocked", ct);
                return false;
            }
            if (step.Kind is not NoteDerivedStepKind.ImageEvidence &&
                result.ResponseText.Length > NoteDerivedLimits.MaxDerivedContentCharacters)
            {
                await StopWithIssueAsync(owner, taskId, stepKey, "derived_content_too_large",
                    $"模型输出 {result.ResponseText.Length} 字符，超过派生产物上限 {NoteDerivedLimits.MaxDerivedContentCharacters}；未截断",
                    "Blocked", ct);
                return false;
            }

            if (step.Kind == NoteDerivedStepKind.ImageEvidence)
            {
                var actual = await ReadActualImagesAsync(state, ct, receipt.Id);
                if (actual.Issues.Count > 0)
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, actual.Issues[0].Code,
                        actual.Issues[0].Message, "Failed", ct);
                    return false;
                }
                if (!BatchMatchesExactly(step, receipt.Id, actual.Images))
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, "vision_evidence_missing",
                        "视觉调用成功回执缺少完整的实际 AiCallImages；不按计划或清单伪造覆盖", "Failed", ct);
                    return false;
                }
            }
            else
            {
                var coverage = await BuildCoverageAsync(state, ct);
                if (!coverage.Coverage.Complete)
                {
                    await StopWithIssueAsync(owner, taskId, stepKey, "synthesis_coverage_incomplete",
                        "材料覆盖尚未齐备，模型输出只保留为检查点，不发布为完整版本", "Blocked", ct);
                    return false;
                }
                await AppendVersionAsync(state, step, receipt, result.ResponseText, coverage.Coverage, ct);
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
            return true;
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
            status, ct, result.LogId);
        return false;
    }

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
            return new(completed, issues);
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
        var logIds = state.Plan.Input.Transcripts.Select(x => x.LogId).ToArray();
        var receipts = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == state.Owner && logIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        foreach (var transcript in state.Plan.Input.Transcripts.OrderBy(x => x.Ordinal))
        {
            if (!bySegment.TryGetValue(transcript.AsrSegmentIndex, out var slice) ||
                slice.Current.Status != "Succeeded" || slice.Current.LogId != transcript.LogId ||
                slice.RecordingId != transcript.RecordingId ||
                slice.RecordingSegment != transcript.RecordingSegment ||
                slice.ChunkIndex != transcript.ChunkIndex ||
                slice.FileIndex != transcript.SourceFileIndex || slice.Path != transcript.SourcePath ||
                slice.Sha256 != transcript.SourceSha256 ||
                slice.SourceOffset != transcript.SourceOffsetSamples ||
                slice.Samples != transcript.Samples ||
                !receipts.TryGetValue(transcript.LogId, out var receipt) ||
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
            completed.Add(transcript.LogId);
        }
        var successful = asr.Steps.Where(x => x.Current.Status == "Succeeded").ToArray();
        if (successful.Length != state.Plan.Input.Transcripts.Count ||
            successful.Any(step => state.Plan.Input.Transcripts.All(x => x.LogId != step.Current.LogId)))
            issues.Add(Issue("asr_success_omitted", "ASR 中存在未纳入固定快照的成功片段"));
        return new(completed, issues);
    }

    private async Task<CoverageCheck> BuildCoverageAsync(NoteDerivedTaskState state, CancellationToken ct)
    {
        var transcripts = await ValidateTranscriptsAsync(state, ct);
        var issues = new List<NoteDerivedIssueDto>(state.Plan.InitialCoverage.Issues);
        issues.AddRange(transcripts.Issues);
        foreach (var step in state.Steps.Where(x => x.Current.Status is "Failed" or "Unknown" or "Blocked"))
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
        catch (System.Text.Json.JsonException) { return false; }

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
        var logIds = vision.Where(x => x.Current.Status == "Succeeded" && x.Current.LogId is not null)
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
        return issues.Count == 0 ? new(items, null) : new(items, issues[0]);
    }

    private async Task<Pim.Infrastructure.Data.Entities.AiRequestLogEntity?> FindReceiptAsync(
        Guid owner, Guid requestId, CancellationToken ct) =>
        await db.AiRequestLogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == owner && x.ClientRequestId == requestId, ct);

    private static bool ReceiptMatches(NoteDerivedStepState step,
        Pim.Infrastructure.Data.Entities.AiRequestLogEntity receipt, NoteDerivedTaskState state,
        out string error)
    {
        var purpose = step.Kind switch
        {
            NoteDerivedStepKind.ImageEvidence => NoteDerivedPrompts.VisionPurpose,
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
        string message, string status, CancellationToken ct, Guid? logId = null)
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
                foreach (var queued in current.Steps.Where(x => x.Current.Status == "Queued"))
                {
                    queued.Current.Status = "Cancelled";
                    queued.Current.ErrorCode = "upstream_not_succeeded";
                    queued.Current.ErrorMessage = "前序步骤未成功，未发送后续外部调用";
                }
            var issue = new NoteDerivedIssueDto(code, message, StepKey: stepKey);
            var issues = current.Issues.Append(issue)
                .DistinctBy(x => (x.Code, x.Message, x.SourcePath, x.BlockId, x.StepKey)).ToList();
            current.Issues = issues;
            current.Coverage = current.Coverage with { Complete = false, Issues = issues };
            if (status == "Blocked") current.StopRequested = true;
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
        int InputCharacters, NoteDerivedIssueDto? Issue)
    {
        internal static PreparedCall Vision(string prompt, IReadOnlyList<AiVisionImageRequest> images,
            int inputCharacters) => new(prompt, images, inputCharacters, null);
        internal static PreparedCall Text(string prompt, int inputCharacters) =>
            new(prompt, [], inputCharacters, null);
        internal static PreparedCall Failed(NoteDerivedIssueDto issue) => new(null, [], 0, issue);
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
}
