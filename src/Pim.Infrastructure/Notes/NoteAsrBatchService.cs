using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Durable MiMo Batch orchestration. Submission is explicit, owner-bound and never retried blindly;
/// provider handles and per-item reconciliation survive process restarts.
/// </summary>
public sealed class NoteAsrBatchService(PimDbContext db, ICurrentUserService user, NoteAsrTaskStore tasks,
    NoteMaterialUploadStore materials, INoteAsrEvidenceSink evidence, IMimoBatchHandleSink sink,
    IMimoBatchClientFactory clients, ISecretProtector secrets, IOptions<NoteAsrBatchOptions> options,
    NoteAsrService? noteAsr = null, AiAsrPresetRegistry? presets = null)
{
    private static readonly string[] SupportedModels = ["mimo-v2.6-pro", "mimo-v2.6-flash"];
    private static readonly TimeSpan PersistenceBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultImportLease = TimeSpan.FromMinutes(2);

    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");

    public async Task<NoteAsrBatchJobDto> StartAsync(StartNoteAsrBatchRequest request, Stream jsonl, long length,
        string filename, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        RequireAudioCapability();
        if (request.AttemptId is not { } attemptId)
            throw new DomainException(40001, "请从本账号的云端多模态 attempt 发起");
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == attemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "Batch attempt 不存在");
        return await StartCoreAsync(request, jsonl, length, filename,
            ExpectedCustomIds(attempt.ExpectedSegments), ct);
    }

    private async Task<NoteAsrBatchJobDto> StartCoreAsync(StartNoteAsrBatchRequest request, Stream jsonl,
        long length, string filename, string[] expectedCustomIds, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        RequireAudioCapability();
        if (request.AttemptId is not { } attemptId || !request.BatchScopeConfirmed ||
            string.IsNullOrWhiteSpace(request.ConsentRef) || request.ConsentRef.Length > 128)
            throw new DomainException(40001, "请从本账号的云端多模态 attempt 发起，并对整批范围明确确认");
        if (request.ConsentRef.Any(char.IsControl))
            throw new DomainException(40001, "Batch 同意引用包含无效字符");
        if (!string.Equals(request.CompletionWindow, MimoBatchDefaults.CompletionWindow, StringComparison.Ordinal))
            throw new DomainException(40001, "Batch completion_window 当前仅允许官方明确的 24h");
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 200 || filename.Any(char.IsControl) ||
            filename.IndexOfAny(['/', '\\']) >= 0)
            throw new DomainException(40001, "Batch 文件名无效");
        var maxBytes = options.Value.MaxJsonlBytes <= 0
            ? 0
            : Math.Min(options.Value.MaxJsonlBytes, MimoBatchDefaults.MaxJsonlBytes);
        if (jsonl is null || length <= 0 || maxBytes <= 0 || length > maxBytes)
            throw new DomainException(41301, "Batch 输入必须非空且不超过配置与供应商共同上限");

        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == attemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "Batch attempt 不存在");
        if (attempt.TaskId != request.TaskId)
            throw new DomainException(40901, "Batch 任务与 attempt 绑定不一致，请重新核对原任务");
        if (attempt.Route != NoteAsrRoute.CloudMultimodal ||
            attempt.ExecutionMode != NoteAsrExecutionMode.Batch ||
            attempt.ModelId != request.ModelId || attempt.ModelRevision is null ||
            attempt.PresetId != MimoMultimodalPreset.PresetId ||
            attempt.ConsentVersion != request.ConsentRef || attempt.StopRequested)
            throw new DomainException(40901, "Batch attempt、模型、同意或执行状态不匹配");
        var frozenState = await tasks.ReadAsync(owner, request.TaskId, ct);
        if (frozenState.Request.TaskId != request.TaskId)
            throw new DomainException(40901, "Batch 任务快照与请求不一致");
        if (expectedCustomIds.Length is < 1 || expectedCustomIds.Length > attempt.ExpectedSegments)
            throw new DomainException(40901, "Batch 提交范围与 attempt 快照不一致");
        if (attempt.ExpectedSegments is < 1 or > 5_000)
            throw new DomainException(40901, "Batch attempt、模型、同意或执行状态不匹配");
        if (attempt.FencingToken is < 0 or > int.MaxValue)
            throw new DomainException(40901, "Batch attempt fencing token 超出当前 provider 契约范围");
        var frozenPrompt = ValidateFrozenPrompt(attempt);

        // A concurrent/replayed Start reads the durable intent before enforcing Queued.
        // The unique owner/task row makes this an idempotent return, not a second provider call.
        var existingJob = await db.NoteAsrBatchJobs.AsNoTracking()
            .Where(x => x.OwnerId == owner && x.TaskId == attempt.TaskId && x.AttemptId == attempt.Id)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (existingJob is not null)
            return Describe(existingJob);
        if (attempt.State != "Queued")
            throw new DomainException(40901, "Batch attempt 已离开待提交状态");

        var (profile, baseUri, apiKey) = await AuthorizedProfileAsync(request.ModelId, attempt.ModelRevision.Value, ct);
        var state = await tasks.ReadAsync(owner, attempt.TaskId, ct);
        var material = await materials.GetAsync(owner, attempt.VersionId, ct);
        var inherited = expectedCustomIds.Length < state.Steps.Count;
        ValidateTrustedPlan(owner, attempt, state, material, profile, request.ConsentRef, inherited);

        await using var input = await NoteAsrBatchJsonlReader.ReadAsync(jsonl, length, owner, attempt.VersionId,
            state.Steps, state.SamplesPerCall, state.Request.Language, profile.Model, frozenPrompt.Text, materials,
            checked((int)maxBytes), expectedCustomIds, ct);
        if (input.CustomIds.Length > attempt.ExpectedSegments)
            throw new DomainException(40901, "Batch 分片范围与 attempt 快照不一致");
        var intent = new MimoBatchIntent(owner, attempt.VersionId.ToString("D"), attempt.TaskId.ToString("D"),
            checked((int)attempt.FencingToken), "", profile.Id.ToString("D"),
            profile.Revision.ToString("D"),
            baseUri.AbsoluteUri, profile.Model, request.CompletionWindow, request.ConsentRef);
        return await SubmitFrozenAsync(owner, attempt, state, material, profile, baseUri, apiKey, intent,
            input, filename, request, job =>
            {
                job.TaskId = request.TaskId;
                job.AttemptId = attempt.Id;
            }, ct);
    }

    /// <summary>
    /// Generates a complete immutable child task through the real Start pipeline, then submits the
    /// exact trusted audio slices. No client-supplied JSONL/audio or guessed consent is accepted.
    /// </summary>
    public async Task<NoteAsrBatchSubmissionResultDto> StartFromTaskAsync(
        StartNoteAsrBatchFromTaskRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        RequireAudioCapability();
        if (request is null || request.SourceTaskId == Guid.Empty || request.ClientTaskId == Guid.Empty ||
            request.ExpectedUserId != owner)
            throw new DomainException(40001, "Batch 子任务或账号标识无效");
        ValidateSubmissionConsent(request.Route, request.Target, request.Reason);
        var source = await tasks.ReadAsync(owner, request.SourceTaskId, ct);
        if (source.Revision != request.ExpectedSourceRevision)
            throw new DomainException(40901, "原任务已变化，请刷新后重新确认");
        if (request.ClientTaskId == request.SourceTaskId)
        {
            // Initial submission: the caller already started this task in batch mode through the typed
            // ASR pipeline, so submit that exact task/attempt instead of cloning a child and wasting slots.
            var prepared = await PreparedExistingBatchTaskAsync(owner, source, request.Route, request.Target, ct);
            return await SubmitGeneratedAsync(owner, prepared, request.Route.ExpectedConsentVersion,
                request.Target.CompletionWindow, "initial", request.SourceTaskId,
                Enumerable.Range(0, prepared.State.Steps.Count).ToArray(), request.Reason, ct);
        }
        if (!await ChildTaskExistsAsync(owner, request.ClientTaskId, ct))
        {
            // A distinct full reprocess may only copy a finished source. Running or queued work must
            // never be duplicated into a second paid submission. An existing child replay is exempt.
            var sourceView = NoteAsrTaskStore.Describe(source);
            if (sourceView.Phase is not ("Succeeded" or "NeedsAttention" or "Stopped"))
                throw new DomainException(40901,
                    "只能从已完成、失败或已停止的源任务创建新的 Batch 子任务；排队或运行中的任务不能复制");
        }
        var child = await EnsureChildTaskAsync(owner, source, request.ClientTaskId, request.Route, request.Target,
            request.Reason, preservePlan: false, ct);
        // The attempt freezes the route-config consent revision. The provider destination reference is
        // validated separately in EnsureChildTaskAsync and must not be conflated with it.
        return await SubmitGeneratedAsync(owner, child, request.Route.ExpectedConsentVersion,
            request.Target.CompletionWindow, "full", request.SourceTaskId,
            Enumerable.Range(0, child.State.Steps.Count).ToArray(),
            request.Reason, ct);
    }

    /// <summary>
    /// Submits only failed/missing segments to a distinct child attempt. Successful original
    /// evidence is provenanced into the child attempt and never uploaded again.
    /// </summary>
    public async Task<NoteAsrBatchSubmissionResultDto> RetryFailedItemsAsync(
        RetryFailedNoteAsrBatchRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        RequireAudioCapability();
        if (request is null || request.SourceJobId == Guid.Empty || request.ClientTaskId == Guid.Empty ||
            request.ExpectedUserId != owner)
            throw new DomainException(40001, "Batch 重试任务或账号标识无效");
        var sourceJob = await db.NoteAsrBatchJobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.SourceJobId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "原 Batch 任务不存在");
        if (string.IsNullOrWhiteSpace(sourceJob.BatchId))
            throw new DomainException(40901, "原 Batch 没有已核验的 provider id，禁止自动重试");
        if (sourceJob.OutputImportedAt is null ||
            sourceJob.Status is not ("Failed" or "Expired" or "Cancelled" or "Completed"))
            throw new DomainException(40901, "原 Batch 尚未完成失败项导入，不能重试");
        if (sourceJob.Status == "Completed" && sourceJob.FailedItems is 0 && sourceJob.LastErrorCode is null)
            throw new DomainException(40901, "原 Batch 已完整成功，禁止重复付费提交");
        var sourceAttempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == sourceJob.AttemptId && x.OwnerId == owner && x.TaskId == sourceJob.TaskId, ct)
            ?? throw new DomainException(40901, "原 Batch attempt 快照不存在");
        if (sourceAttempt.Model != sourceJob.Model || sourceAttempt.ModelRevision != sourceJob.ProfileRevision ||
            sourceAttempt.ConsentVersion != sourceJob.ConsentRef)
            throw new DomainException(40901, "原 Batch attempt 与 provider 快照不一致");
        var sourceTask = await tasks.ReadAsync(owner, sourceJob.TaskId, ct);
        var expected = JsonSerializer.Deserialize<string[]>(sourceJob.ExpectedCustomIdsJson) ?? [];
        if (expected.Length is < 1 || expected.Length > sourceAttempt.ExpectedSegments ||
            expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
            throw new DomainException(40901, "原 Batch 固定分片标识不完整");
        var succeeded = await db.NoteAsrSegmentEvidence.AsNoTracking()
            .Where(x => x.OwnerId == owner && x.AttemptId == sourceAttempt.Id &&
                        x.FencingToken == sourceAttempt.FencingToken && x.ReceiptKind == "batch")
            .Select(x => x.SegmentIndex).Distinct().ToListAsync(ct);
        var retryIndexes = Enumerable.Range(0, sourceAttempt.ExpectedSegments)
            .Where(x => !succeeded.Contains(x)).ToArray();
        if (retryIndexes.Length == 0)
            throw new DomainException(40901, "原 Batch 没有失败或缺失的分片，禁止重复付费提交");
        var child = await EnsureChildTaskAsync(owner, sourceTask, request.ClientTaskId, request.Route,
            request.Target, request.Reason, preservePlan: true, ct);
        return await SubmitGeneratedAsync(owner, child, request.Route.ExpectedConsentVersion,
            request.Target.CompletionWindow, "failed_missing", sourceJob.TaskId, retryIndexes,
            request.Reason, ct);
    }

    private sealed record PreparedChild(NoteAsrState State, NoteAsrAttemptEntity Attempt);

    private async Task<bool> ChildTaskExistsAsync(Guid owner, Guid clientTaskId, CancellationToken ct)
    {
        try
        {
            await tasks.ReadAsync(owner, clientTaskId, ct);
            return true;
        }
        catch (DomainException error) when (error.ErrorCode == 40401)
        {
            return false;
        }
    }

    /// <summary>
    /// Accepts only a task that the trusted ASR pipeline already froze as a queued batch attempt.
    /// A ready task is never submitted twice, and a realtime or terminal source is not eligible.
    /// </summary>
    private async Task<PreparedChild> PreparedExistingBatchTaskAsync(Guid owner, NoteAsrState source,
        NoteAsrReprocessRoute route, NoteAsrBatchTarget target, CancellationToken ct)
    {
        if (source.Route != NoteAsrRoute.CloudMultimodal || source.ExecutionMode != NoteAsrExecutionMode.Batch)
            throw new DomainException(40901, "初次 Batch 提交要求源任务已按多模态 Batch 方式创建");
        if (source.AttemptId is not { } attemptId)
            throw new DomainException(40901, "初次 Batch 提交缺少已冻结的执行快照，请重新创建任务");
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == attemptId && x.OwnerId == owner && x.TaskId == source.Request.TaskId, ct)
            ?? throw new DomainException(40901, "初次 Batch 提交缺少已冻结的执行快照，请重新创建任务");
        // A durable job for this exact task/attempt is a replay: return it before any frozen-config
        // comparison so drift after a lost response can never trigger a second submission.
        var existingJob = await db.NoteAsrBatchJobs.AsNoTracking()
            .Where(x => x.OwnerId == owner && x.TaskId == attempt.TaskId && x.AttemptId == attempt.Id)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (existingJob is not null) return new(source, attempt);
        if (attempt.Route != NoteAsrRoute.CloudMultimodal ||
            attempt.ExecutionMode != NoteAsrExecutionMode.Batch ||
            attempt.ConfigRevision != route.ExpectedConfigRevision ||
            !string.Equals(attempt.ConsentVersion, route.ExpectedConsentVersion, StringComparison.Ordinal) ||
            attempt.ModelId != target.ModelId || attempt.ModelRevision != target.ModelRevision)
            throw new DomainException(40901, "确认的 Batch 目标与已冻结的执行快照不一致，请刷新后重新确认");
        if (attempt.State != "Queued" || attempt.StopRequested)
            throw new DomainException(40901, "初次 Batch 提交只接受尚未发送的待执行任务");
        return new(source, attempt);
    }

    private static void ValidateSubmissionConsent(NoteAsrReprocessRoute? route, NoteAsrBatchTarget? target,
        NoteAsrReprocessReason? reason)
    {
        if (route is null || target is null || reason is null || route.Route != NoteAsrRoute.CloudMultimodal ||
            route.ExecutionMode != NoteAsrExecutionMode.Batch || !route.Confirmed ||
            !route.BatchScopeConfirmed || !target.Confirmed || !target.BatchScopeConfirmed ||
            string.IsNullOrWhiteSpace(route.ExpectedConsentVersion) || route.ExpectedConsentVersion.Length > 64 ||
            string.IsNullOrWhiteSpace(target.ConsentRef) || target.ConsentRef.Length > 128 ||
            target.ConsentRef.Any(char.IsControl) ||
            !string.Equals(target.CompletionWindow, MimoBatchDefaults.CompletionWindow, StringComparison.Ordinal))
            throw new DomainException(40001, "Batch 子任务必须显式确认多模态、整批范围和24小时窗口");
    }

    private async Task<PreparedChild> EnsureChildTaskAsync(Guid owner, NoteAsrState source, Guid clientTaskId,
        NoteAsrReprocessRoute route, NoteAsrBatchTarget target, NoteAsrReprocessReason reason,
        bool preservePlan, CancellationToken ct)
    {
        ValidateSubmissionConsent(route, target, reason);
        var managedNoteAsr = noteAsr ?? throw new DomainException(40901, "Batch 服务未配置真实任务创建流水线");
        var model = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == target.ModelId, ct)
            ?? throw new DomainException(40901, "Batch 模型不存在");
        if (model.Revision != target.ModelRevision || model.AsrPresetId != MimoMultimodalPreset.PresetId ||
            !model.Enabled || (model.Capabilities & AiModelCapabilityFlags.Transcription) == 0 ||
            !SupportedModels.Contains(model.Model, StringComparer.Ordinal))
            throw new DomainException(40901, "Batch 模型、配置修订或多模态能力不匹配，请刷新重新确认");
        var managedPresets = presets ?? throw new DomainException(40901, "Batch 预设注册不可用");
        var preset = managedPresets.Resolve(model.AsrPresetId);
        var descriptor = preset.Descriptor;
        var endpoint = AiAsrPresetRegistry.OperationEndpoint(preset.NormalizeEndpoint(model.BaseUrl), descriptor);
        var configured = await db.AiModelProfiles.AsNoTracking().CountAsync(x => x.Id == model.Id &&
            x.Revision == model.Revision && x.Enabled, ct);
        if (configured != 1) throw new DomainException(40901, "Batch 模型配置在创建期间发生变化");
        var consent = new AiAsrDestinationConsent(model.Id, model.Revision, endpoint, descriptor.Id,
            descriptor.Version, true);
        var start = new StartNoteAsrRequest(clientTaskId, owner, source.Request.VersionId, consent,
            source.Request.Language, new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal,
                route.ExpectedConfigRevision, route.ExpectedConsentVersion, true, NoteAsrExecutionMode.Batch,
                route.BatchScopeConfirmed), source.Request.TaskId, ReasonText(reason));
        try
        {
            var existing = await tasks.ReadAsync(owner, clientTaskId, ct);
            if (existing.Fingerprint != NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(start,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))))
                throw new DomainException(40901, "Batch 客户端任务标识已绑定另一输入");
            await managedNoteAsr.StartAsync(start, ct);
        }
        catch (DomainException error) when (error.ErrorCode == 40401)
        {
            await managedNoteAsr.StartAsync(start, ct);
        }
        var state = await tasks.ReadAsync(owner, clientTaskId, ct);
        if (state.ParentTaskId != source.Request.TaskId || state.Request.ParentTaskId != source.Request.TaskId ||
            state.Route != NoteAsrRoute.CloudMultimodal || state.ExecutionMode != NoteAsrExecutionMode.Batch ||
            state.Request.VersionId != source.Request.VersionId || state.InputSha256 != source.InputSha256 ||
            state.ManifestSha256 != source.ManifestSha256 ||
            preservePlan && state.Steps.Count != source.Steps.Count)
            throw new DomainException(40901, "Batch 子任务未保留原任务固定材料、分片或父链");
        if (preservePlan)
            for (var index = 0; index < source.Steps.Count; index++)
                if (!SameStep(source.Steps[index], state.Steps[index]))
                    throw new DomainException(40901, "Batch 重试子任务固定分片与原任务不一致");
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.OwnerId == owner && x.TaskId == clientTaskId && x.FencingToken == 0, ct)
            ?? throw new DomainException(40901, "Batch 子任务 attempt 尚未冻结");
        return new(state, attempt);
    }

    private async Task<NoteAsrBatchSubmissionResultDto> SubmitGeneratedAsync(Guid owner, PreparedChild child,
        string consentRef, string completionWindow, string scope, Guid parentTaskId, int[] retryIndexes,
        NoteAsrReprocessReason reason, CancellationToken ct)
    {
        var attempt = child.Attempt;
        await ImportInheritedEvidenceAsync(owner, attempt, child.State.Steps.Count, retryIndexes, ct);
        await using var generated = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(owner, attempt.VersionId,
            child.State.Steps, child.State.SamplesPerCall, child.State.Request.Language, attempt.Model,
            ValidateFrozenPrompt(attempt).Text, materials,
            checked((int)Math.Min(options.Value.MaxJsonlBytes, MimoBatchDefaults.MaxJsonlBytes)),
            ExpectedCustomIds(child.State.Steps.Count), retryIndexes, ct);
        var request = new StartNoteAsrBatchRequest(child.State.Request.TaskId, attempt.Id, attempt.ModelId!.Value,
            consentRef, true, completionWindow);
        using var budget = new CancellationTokenSource(PersistenceBudget);
        var job = await StartCoreAsync(request, generated.Content, generated.Length,
            $"pim-{scope}.jsonl", generated.CustomIds, budget.Token);
        var completed = await tasks.ReadAsync(owner, child.State.Request.TaskId, ct);
        return new(job, NoteAsrTaskStore.Describe(completed), parentTaskId, scope);
    }

    private async Task ImportInheritedEvidenceAsync(Guid owner, NoteAsrAttemptEntity child, int totalSegments,
        int[] retryIndexes,
        CancellationToken ct)
    {
        if (retryIndexes.Length == totalSegments) return;
        var childState = await tasks.ReadAsync(owner, child.TaskId, ct);
        var parentId = childState.Request.ParentTaskId
            ?? throw new DomainException(40901, "Batch 子任务缺少可信父链，不能继承成功证据");
        var parent = await tasks.ReadAsync(owner, parentId, ct);
        var parentAttempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.OwnerId == owner && x.TaskId == parentId && x.Id == parent.AttemptId, ct)
            ?? throw new DomainException(40901, "原 Batch attempt 不存在");
        if (parent.Request.VersionId != child.VersionId || parent.InputSha256 != child.InputSha256)
            throw new DomainException(40901, "原任务与子任务材料不一致，不能继承证据");
        if (parent.Steps.Count != totalSegments || retryIndexes.Any(x => x < 0 || x >= totalSegments))
            throw new DomainException(40901, "原任务与子任务固定分片计划不一致");
        var existing = await db.NoteAsrSegmentEvidence.AsNoTracking()
            .Where(x => x.OwnerId == owner && x.AttemptId == child.Id && x.FencingToken == child.FencingToken)
            .Select(x => x.SegmentIndex).Distinct().ToListAsync(ct);
        var retry = retryIndexes.ToHashSet();
        var rows = await db.NoteAsrSegmentEvidence.AsNoTracking()
            .Where(x => x.OwnerId == owner && x.AttemptId == parentAttempt.Id &&
                        x.FencingToken == parentAttempt.FencingToken && x.ReceiptKind == "batch")
            .OrderBy(x => x.SegmentIndex).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (retry.Contains(row.SegmentIndex) || existing.Contains(row.SegmentIndex)) continue;
            if (childState.Steps[row.SegmentIndex].Sha256 != row.SourceSha256 ||
                childState.Steps[row.SegmentIndex].Samples != row.Samples)
                throw new DomainException(40901, "原成功证据与子任务分片身份不一致");
            var provenance = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, inherited = true, parentTaskId = parentId,
                parentAttemptId = parentAttempt.Id, parentEvidenceId = row.Id,
                parentFencingToken = parentAttempt.FencingToken
            });
            await evidence.WriteForPersistedOwnerAsync(owner, child.Id, row.SegmentIndex, "batch",
                $"inherit:{row.Id:D}", row.ResultText, provenance, true, ct);
        }
    }

    private static void EnsureRetryJobHasInheritedPlan(List<NoteAsrBatchJobEntity> jobs, Guid owner,
        NoteAsrAttemptEntity attempt, NoteAsrState state)
    {
        if (jobs.Count == 0) return;
        var job = jobs.SingleOrDefault() ?? throw new DomainException(40901, "Batch attempt 已绑定多个任务");
        var ids = JsonSerializer.Deserialize<string[]>(job.ExpectedCustomIdsJson) ?? [];
        if (ids.Length == state.Steps.Count) return;
        if (state.Request.ParentTaskId is null ||
            jobs.Any(x => x.OwnerId != owner || x.AttemptId != attempt.Id))
            throw new DomainException(40901, "Batch 子集任务缺少可信父链");
    }

    private static string[] ExpectedCustomIds(int count) =>
        Enumerable.Range(0, count).Select(NoteAsrBatchJsonlReader.CustomId).ToArray();

    /// <summary>
    /// Audio Batch is NOT-VERIFIED upstream. The single capability gate runs before any submission
    /// mutation so a disabled flag cannot leave an orphan child task/attempt or a paid provider call.
    /// </summary>
    private void RequireAudioCapability()
    {
        if (!options.Value.AllowUnverifiedAudioBatch)
            throw new DomainException(40901, "MiMo Batch 音频能力尚未验证；管理员显式开启后仍属 NOT-VERIFIED");
    }

    private static string ReasonText(NoteAsrReprocessReason reason)
    {
        var kind = reason.Kind?.Trim().ToLowerInvariant();
        if (!NoteAsrReprocessReasonKind.Known(kind) || reason.Text?.Length > 200)
            throw new DomainException(40001, "Batch 重处理原因只能是 manual 或 quality，说明最多200字");
        var text = $"batch-retry: {kind} {reason.Text}".Trim();
        return text.Length <= 240 ? text : text[..240];
    }

    private static bool SameStep(NoteAsrStep left, NoteAsrStep right) =>
        left.Index == right.Index && left.RecordingId == right.RecordingId &&
        left.RecordingSegment == right.RecordingSegment && left.ChunkIndex == right.ChunkIndex &&
        left.FileIndex == right.FileIndex && left.Path == right.Path && left.Sha256 == right.Sha256 &&
        left.SourceOffset == right.SourceOffset && left.Samples == right.Samples;

    private async Task<NoteAsrBatchJobDto> SubmitFrozenAsync(Guid owner, NoteAsrAttemptEntity attempt,
        NoteAsrState state, NoteMaterialVersionDto material, AiModelProfileEntity profile, Uri baseUri,
        string apiKey, MimoBatchIntent intent, NoteAsrBatchJsonl input, string filename,
        StartNoteAsrBatchRequest request,
        Action<NoteAsrBatchJobEntity> bind, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Batch submit must own its durable placeholder transaction");

        NoteAsrBatchJobEntity NewJob()
        {
            var job = new NoteAsrBatchJobEntity
            {
                OwnerId = owner,
                Model = profile.Model, BaseUrl = baseUri.AbsoluteUri, ProfileRevision = profile.Revision,
                CredentialSecret = secrets.Protect(apiKey), ConsentRef = request.ConsentRef,
                CompletionWindow = request.CompletionWindow,
                ExpectedCustomIdsJson = JsonSerializer.Serialize(input.CustomIds),
                TotalItems = input.CustomIds.Length
            };
            bind(job);
            job.IntentJson = JsonSerializer.Serialize(intent with { ProviderHandle = job.Id.ToString("D") });
            return job;
        }

        // The retrying execution strategy wraps only the DB-only freeze unit. It may replay this
        // delegate after a transient/unknown commit, so every attempt rebuilds the tracked entity
        // and relies on the owner/task unique key for exactly-once submission.
        var frozen = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var candidate = NewJob();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var claimedAt = DateTimeOffset.UtcNow;
            var won = await db.NoteAsrAttempts
                .Where(x => x.Id == attempt.Id && x.OwnerId == owner && x.TaskId == request.TaskId &&
                            x.VersionId == attempt.VersionId && x.InputSha256 == attempt.InputSha256 &&
                            x.Route == NoteAsrRoute.CloudMultimodal &&
                            x.ExecutionMode == NoteAsrExecutionMode.Batch &&
                            x.ModelId == request.ModelId && x.ModelRevision == attempt.ModelRevision &&
                            x.ConsentVersion == request.ConsentRef && x.FencingToken == attempt.FencingToken &&
                            x.State == "Queued" && !x.StopRequested)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.State, "Creating")
                    .SetProperty(x => x.UpdatedAt, claimedAt), ct);
            if (won == 0)
            {
                await tx.RollbackAsync(ct);
                var winner = await db.NoteAsrBatchJobs.AsNoTracking()
                    .Where(x => x.OwnerId == owner && x.TaskId == attempt.TaskId && x.AttemptId == attempt.Id)
                    .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
                if (winner is not null)
                    return new FrozenSubmission(winner, false);
                throw new DomainException(40901, "Batch attempt 已被并发操作占用，请刷新原记录");
            }

            db.NoteAsrBatchJobs.Add(candidate);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new FrozenSubmission(candidate, true);
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.Entry(candidate).State = EntityState.Detached;
                var winner = await db.NoteAsrBatchJobs.AsNoTracking()
                    .Where(x => x.OwnerId == owner && x.TaskId == attempt.TaskId && x.AttemptId == attempt.Id)
                    .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(CancellationToken.None);
                if (winner is not null)
                    return new FrozenSubmission(winner, false);
                throw;
            }
        });

        var job = frozen.Job;
        if (!frozen.Created) return Describe(job);

        try
        {
            using var uploadHttp = clients.Create(baseUri, apiKey);
            var upload = await uploadHttp.Client.UploadJsonlAsync(input.Content, input.Length, filename, ct);
            job.InputFileId = upload.FileId;
            job.InputExpireAt = upload.ExpireAt;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveBoundedAsync();
        }
        catch (OperationCanceledException)
        {
            await PersistUnknownAsync(job, "batch_upload_unknown",
                "输入文件上传期间请求取消，结果未确认；不得自动重复提交");
            return Describe(job);
        }
        catch (Exception error) when (IsDefiniteRejection(error))
        {
            await PersistFailedAsync(job, "batch_upload_rejected",
                "供应商明确拒绝输入文件；本次未创建 Batch，请核对配置或文件后新建 attempt");
            return Describe(job);
        }
        catch (Exception error) when (IsUncertainTransport(error))
        {
            await PersistUnknownAsync(job, "batch_upload_unknown",
                "输入文件上传结果未确认；不得自动重复提交");
            return Describe(job);
        }

        try
        {
            using var createHttp = clients.Create(baseUri, apiKey);
            var outcome = await createHttp.Client.CreateBatchAsync(job.InputFileId!,
                MimoBatchDefaults.ChatCompletionsEndpoint, $"pim-{job.Id:N}", request.CompletionWindow,
                containsAudio: true, ct);
            try
            {
                await sink.SaveAsync(outcome, intent, CancellationToken.None);
            }
            catch (Exception saveError) when (saveError is not OutOfMemoryException)
            {
                // A verified provider handle must win over a transport/request cancellation.
                if (outcome is MimoBatchCreateOutcome.Known known)
                    await PersistKnownAsync(job, known.Batch);
                else
                    await PersistUnknownAsync(job, "batch_create_unknown",
                        "Batch 创建结果未确认；不得自动重发");
            }
            using var readBudget = new CancellationTokenSource(PersistenceBudget);
            return await GetAsync(job.Id, readBudget.Token);
        }
        catch (OperationCanceledException)
        {
            await PersistUnknownAsync(job, "batch_create_unknown",
                "Batch 创建期间请求取消，结果未确认；不得自动重发");
            return Describe(job);
        }
        catch (Exception error) when (IsDefiniteRejection(error))
        {
            await PersistFailedAsync(job, "batch_create_rejected",
                "供应商明确拒绝创建 Batch；本次未取得 batch id，请核对后新建 attempt");
            return Describe(job);
        }
        catch (Exception error) when (IsUncertainTransport(error))
        {
            await PersistUnknownAsync(job, "batch_create_unknown",
                "Batch 创建结果未确认；不得自动重发，请平台侧按 attempt 人工核对");
            return Describe(job);
        }
    }

    private sealed record FrozenSubmission(NoteAsrBatchJobEntity Job, bool Created);

    public async Task<IReadOnlyList<NoteAsrBatchJobDto>> ListAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return (await db.NoteAsrBatchJobs.AsNoTracking().Where(x => x.OwnerId == owner)
            .OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct)).Select(Describe).ToArray();
    }

    public async Task<NoteAsrBatchJobDto> GetAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        // Read the persisted row: a verified handle may have been written through
        // an independent sink context while this context still tracks "Creating".
        var job = await db.NoteAsrBatchJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "Batch 任务不存在");
        return Describe(job);
    }

    /// <summary>Polling is explicit and idempotent; status and both file handles come from one bounded GET.</summary>
    public async Task<NoteAsrBatchJobDto> PollAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await PollCoreAsync(owner, id, ct);
    }

    /// <summary>
    /// Trusted background entry: the owner comes from the persisted job row, never from HTTP state.
    /// Only known batch ids are polled; this method never uploads, creates, or lists batches.
    /// </summary>
    internal async Task<NoteAsrBatchJobDto> PollForOwnerAsync(Guid owner, Guid id, CancellationToken ct)
    {
        if (owner == Guid.Empty)
            throw new DomainException(40001, "Batch 后台轮询缺少持久化 owner");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用");
        return await PollCoreAsync(owner, id, ct);
    }

    private async Task<NoteAsrBatchJobDto> PollCoreAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var job = await OwnedAsync(owner, id, ct);
        // Pick up terminal/delete transitions committed by another request before applying a stale GET.
        await db.Entry(job).ReloadAsync(ct);
        if (job.Status == "Deleted")
            return Describe(job);
        if (string.IsNullOrWhiteSpace(job.BatchId))
        {
            if (job.Status == "Creating")
            {
                job.Status = "Unknown";
                job.StatusRaw = null;
                job.LastErrorCode = "batch_create_unknown";
                job.LastErrorMessage = "持久化任务没有已核验的 batch id；禁止自动重新提交";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveBoundedAsync();
            }
            return Describe(job);
        }

        var apiKey = ReadStoredCredential(job);
        using var http = clients.Create(new Uri(job.BaseUrl), apiKey);
        var batch = await http.Client.GetBatchAsync(job.BatchId, ct);
        ApplyBatch(job, batch);
        job.LastPolledAt = DateTimeOffset.UtcNow;
        job.UpdatedAt = job.LastPolledAt.Value;
        // Persist status before importing: the evidence sink may share this DbContext,
        // and its SaveChanges must not compete with a pending job-row update.
        if (!await SaveBoundedAsync() && !TerminalState(job) && job.Status != "Deleted")
        {
            // Another writer won the revision race; its row was reloaded. Re-apply once
            // and let the optimistic token decide again (never rewind a terminal row).
            ApplyBatch(job, batch);
            job.LastPolledAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = job.LastPolledAt.Value;
            await SaveBoundedAsync();
        }
        try
        {
            await ImportTerminalIfReadyAsync(job, apiKey, ct);
        }
        catch (OperationCanceledException)
        {
            // Keep the terminal status and both file handles even when the caller disconnects.
            await SaveBoundedAsync();
            throw;
        }
        await SaveBoundedAsync();
        return Describe(job);
    }

    public async Task<NoteAsrBatchJobDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var job = await OwnedAsync(owner, id, ct);
        await db.Entry(job).ReloadAsync(ct);
        if (job.Status == "Deleted")
            throw new DomainException(40901, "Batch 已删除，取消不再适用");
        if (string.IsNullOrWhiteSpace(job.BatchId))
            throw new DomainException(40901, "没有可核对的 batch id；请在平台侧人工核对，不应重发");
        if (TerminalState(job))
            throw new DomainException(40901, "Batch 已终态，取消不再适用");
        var apiKey = ReadStoredCredential(job);
        using var http = clients.Create(new Uri(job.BaseUrl), apiKey);
        var batch = await http.Client.CancelAsync(job.BatchId, ct);
        ApplyBatch(job, batch);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        // Persist the CAS status before importing: the evidence sink may share this
        // DbContext and must not flush the job row outside the revision guard.
        if (!await SaveBoundedAsync() && !TerminalState(job) && job.Status != "Deleted")
        {
            ApplyBatch(job, batch);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveBoundedAsync();
        }
        try
        {
            await ImportTerminalIfReadyAsync(job, apiKey, ct);
        }
        catch (OperationCanceledException)
        {
            await SaveBoundedAsync();
            throw;
        }
        await SaveBoundedAsync();
        return Describe(job);
    }

    public async Task DeleteTerminalAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var job = await OwnedAsync(owner, id, ct);
        await db.Entry(job).ReloadAsync(ct);
        if (job.Status == "Deleted")
            return;
        if (string.IsNullOrWhiteSpace(job.BatchId) ||
            !TerminalState(job))
            throw new DomainException(40901, "仅终态 Batch 资源可删除；取消不是 DELETE，也不证明音频已删除");
        var apiKey = ReadStoredCredential(job);
        using var http = clients.Create(new Uri(job.BaseUrl), apiKey);
        var current = new MimoBatchRef(job.BatchId!, job.StatusRaw ?? job.Status,
            MimoBatchStatus.Normalize(job.StatusRaw ?? job.Status), job.BatchExpiresAt,
            job.TotalItems, job.CompletedItems, job.FailedItems, job.InputFileId, job.OutputFileId, job.ErrorFileId);
        await http.Client.DeleteAsync(current, ct);
        job.Status = "Deleted";
        job.StatusRaw = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        if (!await SaveBoundedAsync() && job.Status != "Deleted")
        {
            job.Status = "Deleted";
            job.StatusRaw = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveBoundedAsync();
        }
    }

    private async Task ImportAsync(NoteAsrBatchJobEntity job, string apiKey, CancellationToken ct)
    {
        var expected = JsonSerializer.Deserialize<string[]>(job.ExpectedCustomIdsJson) ?? [];
        using var http = clients.Create(new Uri(job.BaseUrl), apiKey);
        string? output = null;
        string? errors = null;
        if (job.OutputFileId is not null)
            output = await http.Client.DownloadOutputAsync(job.OutputFileId, ct);
        if (job.ErrorFileId is not null)
            errors = await http.Client.DownloadErrorAsync(job.ErrorFileId, ct);
        var reconciliation = MimoBatchResultParser.Reconcile(expected, output, errors);
        if (job.AttemptId is not { } attemptId || string.IsNullOrWhiteSpace(job.BatchId))
            throw new DomainException(40901, "Batch 结果缺少持久化 attempt 或 provider handle");
        foreach (var item in reconciliation.Items.Where(x => x.Disposition == MimoBatchItemDisposition.AsrSucceeded))
        {
            var segmentIndex = SegmentIndex(item.CustomId);
            if (segmentIndex < 0 || string.IsNullOrWhiteSpace(item.Content))
                throw new DomainException(40901, "Batch 成功条目未绑定固定分片或缺少转写文本");
            await evidence.WriteForPersistedOwnerAsync(job.OwnerId, attemptId, segmentIndex, "batch", job.BatchId,
                item.Content, null, updateAttemptCounters: true, ct);
        }
        await evidence.FinalizeBatchForPersistedOwnerAsync(job.OwnerId, attemptId,
            MimoBatchStatus.Normalize(job.StatusRaw) == MimoBatchStatusKind.Completed, ct);
        // Evidence writes own their retry transaction and clear this shared context. Reattach the
        // persisted job before recording import completion so the poll cannot return a stale row.
        if (db.Entry(job).State == EntityState.Detached)
            db.Attach(job);
        job.ResultJson = JsonSerializer.Serialize(reconciliation);
        job.OutputImportedAt = DateTimeOffset.UtcNow;
        job.LastErrorCode = reconciliation.HasFailures ? "partial_or_failed_items" : null;
        job.LastErrorMessage = reconciliation.HasFailures
            ? "Batch 结果已保存；仅失败或缺失条目应显式新建 attempt 重试，成功条目不得重跑"
            : null;
    }

    private static int SegmentIndex(string customId)
    {
        const string prefix = "seg-";
        return customId.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(customId.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var index) &&
               index >= 0 && customId == NoteAsrBatchJsonlReader.CustomId(index)
            ? index
            : -1;
    }

    private async Task ImportTerminalIfReadyAsync(NoteAsrBatchJobEntity job, string apiKey, CancellationToken ct)
    {
        var providerStatus = MimoBatchStatus.Normalize(job.StatusRaw);
        if (!MimoBatchStatus.IsTerminal(providerStatus) || job.OutputImportedAt is not null)
            return;
        if (job.OutputFileId is null && job.ErrorFileId is null)
        {
            job.LastErrorCode = "result_files_missing";
            job.LastErrorMessage = "Batch 已终态但未返回成功或错误文件句柄；保留状态等待人工核对";
            return;
        }
        // Only the caller holding the current revision may import. This prevents two terminal
        // pollers from racing the same task checkpoint; a crash leaves a claim that a later poll
        // can retry because the guard is only the current revision plus OutputImportedAt.
        var claimRevision = Guid.NewGuid();
        var importLeaseCutoff = DateTimeOffset.UtcNow - ResultImportLease;
        var claimed = await db.NoteAsrBatchJobs
            .Where(x => x.Id == job.Id && x.OwnerId == job.OwnerId &&
                        x.OutputImportedAt == null && x.Revision == job.Revision &&
                        (x.LastErrorCode != "result_import_in_progress" ||
                         x.UpdatedAt < importLeaseCutoff))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.LastErrorCode, "result_import_in_progress")
                .SetProperty(x => x.LastErrorMessage, "Batch 终态文件正在安全导入")
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.Revision, claimRevision), ct);
        if (claimed == 0)
            return;
        await db.Entry(job).ReloadAsync(ct);
        if (job.OutputImportedAt is not null ||
            !MimoBatchStatus.IsTerminal(MimoBatchStatus.Normalize(job.StatusRaw)))
            return;
        try
        {
            await ImportAsync(job, apiKey, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is MimoBatchException or HttpRequestException or IOException or
                                      UnauthorizedAccessException or JsonException or DomainException or
                                      DbUpdateConcurrencyException)
        {
            job.LastErrorCode = "result_import_failed";
            job.LastErrorMessage = "Batch 已终态但结果文件尚未安全入库；轮询会重试下载，不得重复创建 Batch";
        }
    }

    private async Task<(AiModelProfileEntity Profile, Uri BaseUri, string ApiKey)> AuthorizedProfileAsync(
        Guid profileId, Guid revision, CancellationToken ct)
    {
        var profile = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId, ct)
            ?? throw new DomainException(40901, "Batch 模型配置不存在");
        var runtimeEnabled = await db.AiModelRuntime.AsNoTracking()
            .AnyAsync(x => x.Id == AiModelRuntimeEntity.SingletonId && x.Enabled, ct);
        if (profile.Revision != revision || !runtimeEnabled)
            throw new DomainException(40901, "Batch 模型配置修订已变化，请新建 attempt 并重新确认");
        if (!profile.Enabled || (profile.Capabilities & AiModelCapabilityFlags.Transcription) == 0)
            throw new DomainException(40901, "Batch 模型配置不可用或未开启语音识别能力");
        if (profile.AsrPresetId != MimoMultimodalPreset.PresetId ||
            !SupportedModels.Contains(profile.Model, StringComparer.Ordinal))
            throw new DomainException(40901, "Batch 需要使用已声明的 MiMo 多模态模型");
        var baseUri = BatchOrigin(profile.AsrBatchBaseUrl);
        var apiKey = ReadCredential(profile.ApiKeySecret);
        return (profile, baseUri, apiKey);
    }

    private static void ValidateTrustedPlan(Guid owner, NoteAsrAttemptEntity attempt, NoteAsrState state,
        NoteMaterialVersionDto material, AiModelProfileEntity profile, string consentRef, bool inherited = false)
    {
        if (state.Owner != owner || state.Request.TaskId != attempt.TaskId ||
            state.Request.VersionId != attempt.VersionId || state.NoteId != attempt.NoteId ||
            state.AttemptId != attempt.Id || state.Route != NoteAsrRoute.CloudMultimodal ||
            state.RouteConfigRevision != attempt.ConfigRevision ||
            state.RouteConsentVersion != attempt.ConsentVersion ||
            state.ExecutionMode != attempt.ExecutionMode || state.StopRequested ||
            state.Request.Route is not
            {
                Route: NoteAsrRoute.CloudMultimodal,
                Confirmed: true,
                BatchScopeConfirmed: true,
                ExecutionMode: NoteAsrExecutionMode.Batch
            } requestRoute ||
            requestRoute.ConfigRevision != attempt.ConfigRevision ||
            requestRoute.ConsentVersion != consentRef ||
            state.Request.Language != attempt.Language ||
            state.Steps.Count != attempt.ExpectedSegments ||
            state.InputSha256 != attempt.InputSha256 ||
            !FixedEquals(state.InputSha256, NoteMaterialManifestValidator.Hash(material.InputJson)) ||
            !FixedEquals(state.ManifestSha256, NoteMaterialManifestValidator.Hash(material.ManifestJson)))
            throw new DomainException(40901, "Batch 任务、材料、版本或固定分片计划已变化，请重新创建 attempt");
        if (attempt.Model != profile.Model || attempt.ModelRevision != profile.Revision ||
            state.Request.Consent.ModelId != attempt.ModelId ||
            state.Request.Consent.ModelRevision != attempt.ModelRevision ||
            state.Request.Consent.PresetId != MimoMultimodalPreset.PresetId ||
            state.Request.Consent.PresetVersion != "1" || !state.Request.Consent.Confirmed ||
            state.RouteConsentVersion != consentRef)
            throw new DomainException(40901, "Batch 模型、预设、版本或同意快照不匹配");
        if (state.SamplesPerCall <= 0)
            throw new DomainException(40901, "Batch 固定分片计划缺少可信的单片时长，请新建 attempt");
        for (var index = 0; index < state.Steps.Count; index++)
        {
            var step = state.Steps[index];
            if (step.Index != index || step.Samples <= 0 || step.Samples > state.SamplesPerCall ||
                !(inherited && step.Current.Status == "Succeeded") && step.Current.Status != "Queued")
                throw new DomainException(40901, "Batch 固定分片计划不是完整的待执行快照");
        }
    }

    private static AiAsrPromptSnapshot ValidateFrozenPrompt(NoteAsrAttemptEntity attempt)
    {
        if (string.IsNullOrWhiteSpace(attempt.PromptTemplate) ||
            string.IsNullOrWhiteSpace(attempt.PromptTemplateSha256) ||
            string.IsNullOrWhiteSpace(attempt.PromptTemplateRevision) ||
            string.IsNullOrWhiteSpace(attempt.PromptRendered) ||
            string.IsNullOrWhiteSpace(attempt.PromptSha256) ||
            string.IsNullOrWhiteSpace(attempt.PromptRevision))
            throw new DomainException(40901, "Batch 冻结提示词快照缺失，请新建 attempt");

        AiAsrPromptTemplate template;
        AiAsrPromptSnapshot rendered;
        try
        {
            template = AiAsrPromptTemplate.Normalize(attempt.PromptTemplate);
            rendered = template.Snapshot(attempt.Language);
        }
        catch (DomainException)
        {
            throw new DomainException(40901, "Batch 冻结提示词快照无效，请新建 attempt");
        }
        if (!string.Equals(template.Text, attempt.PromptTemplate, StringComparison.Ordinal) ||
            !FixedEquals(template.Sha256, attempt.PromptTemplateSha256) ||
            !FixedEquals(template.Revision, attempt.PromptTemplateRevision) ||
            !string.Equals(rendered.Text, attempt.PromptRendered, StringComparison.Ordinal) ||
            !FixedEquals(rendered.Sha256, attempt.PromptSha256) ||
            !FixedEquals(rendered.Revision, attempt.PromptRevision))
            throw new DomainException(40901, "Batch 冻结提示词模板或渲染哈希/修订不匹配");
        return rendered;
    }

    private static Uri BatchOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !uri.Host.Contains("batch-api-", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(40901, "Batch origin 不是 MiMo batch-api 专用 HTTPS 地址");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private static bool TerminalState(NoteAsrBatchJobEntity job) =>
        job.Status is "Deleted" or "Completed" or "Failed" or "Expired" or "Cancelled" ||
        MimoBatchStatus.IsTerminal(MimoBatchStatus.Normalize(job.StatusRaw));

    private static int? Max(int? left, int? right) =>
        right is null ? left : left is null ? right : Math.Max(left.Value, right.Value);

    private static void ApplyBatch(NoteAsrBatchJobEntity job, MimoBatchRef batch)
    {
        // A deleted job is never resurrected and a late non-terminal response never
        // rewinds a terminal state; only missing handles and counters may be filled.
        if (job.Status == "Deleted")
            return;
        job.InputFileId = batch.InputFileId ?? job.InputFileId;
        job.OutputFileId = batch.OutputFileId ?? job.OutputFileId;
        job.ErrorFileId = batch.ErrorFileId ?? job.ErrorFileId;
        job.CompletedItems = Max(job.CompletedItems, batch.Completed);
        job.FailedItems = Max(job.FailedItems, batch.Failed);
        if (TerminalState(job))
            return;
        job.StatusRaw = batch.StatusRaw;
        job.Status = batch.Status.ToString();
        job.BatchExpiresAt = batch.ExpiresAt ?? job.BatchExpiresAt;
        if (batch.Status == MimoBatchStatusKind.Unknown)
        {
            job.LastErrorCode = "batch_status_unknown";
            job.LastErrorMessage = "供应商返回未识别状态；保持 UNKNOWN，不视为成功且不自动重发";
        }
        else if (job.LastErrorCode == "batch_status_unknown")
        {
            job.LastErrorCode = null;
            job.LastErrorMessage = null;
        }
    }

    private async Task PersistUnknownAsync(NoteAsrBatchJobEntity job, string code, string message)
    {
        ApplyUnknown(job, code, message);
        if (await SaveBoundedAsync() || job.BatchId is not null || TerminalState(job))
            return;
        ApplyUnknown(job, code, message);
        await SaveBoundedAsync();

        static void ApplyUnknown(NoteAsrBatchJobEntity job, string code, string message)
        {
            job.Status = "Unknown";
            job.StatusRaw = null;
            job.LastErrorCode = code;
            job.LastErrorMessage = message;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task PersistFailedAsync(NoteAsrBatchJobEntity job, string code, string message)
    {
        ApplyFailed(job, code, message);
        if (await SaveBoundedAsync() || job.BatchId is not null || TerminalState(job))
            return;
        ApplyFailed(job, code, message);
        await SaveBoundedAsync();

        static void ApplyFailed(NoteAsrBatchJobEntity job, string code, string message)
        {
            job.Status = "Failed";
            job.StatusRaw = null;
            job.LastErrorCode = code;
            job.LastErrorMessage = message;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task PersistKnownAsync(NoteAsrBatchJobEntity job, MimoBatchRef batch)
    {
        EnsureSameVerifiedHandle(job, batch);
        ApplyKnown(job, batch);
        if (await SaveBoundedAsync())
            return;
        EnsureSameVerifiedHandle(job, batch);
        if (job.BatchId is not null || job.Status == "Deleted")
            return;
        // Conflict reloaded the row and no immutable handle exists yet: fill only the
        // verified handle, never rewind a terminal state or overwrite a different one.
        ApplyKnown(job, batch);
        await SaveBoundedAsync();

        static void ApplyKnown(NoteAsrBatchJobEntity job, MimoBatchRef batch)
        {
            if (job.BatchId is not null &&
                !string.Equals(job.BatchId, batch.BatchId, StringComparison.Ordinal))
                return;
            var terminal = job.Status is "Deleted" or "Completed" or "Failed" or "Expired" or "Cancelled" ||
                           MimoBatchStatus.IsTerminal(MimoBatchStatus.Normalize(job.StatusRaw));
            job.BatchId = batch.BatchId;
            job.InputFileId = batch.InputFileId ?? job.InputFileId;
            job.OutputFileId = batch.OutputFileId ?? job.OutputFileId;
            job.ErrorFileId = batch.ErrorFileId ?? job.ErrorFileId;
            job.BatchExpiresAt = batch.ExpiresAt ?? job.BatchExpiresAt;
            job.CompletedItems = batch.Completed ?? job.CompletedItems;
            job.FailedItems = batch.Failed ?? job.FailedItems;
            job.LastErrorCode = null;
            job.LastErrorMessage = null;
            if (!terminal)
            {
                job.StatusRaw = batch.StatusRaw;
                job.Status = batch.Status.ToString();
            }
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static void EnsureSameVerifiedHandle(NoteAsrBatchJobEntity job, MimoBatchRef batch)
    {
        if (job.BatchId is not null &&
            !string.Equals(job.BatchId, batch.BatchId, StringComparison.Ordinal))
            throw new DomainException(40901, "Batch 已绑定另一个不可变 provider handle；禁止覆盖");
    }

    private async Task<bool> SaveBoundedAsync()
    {
        foreach (var entry in db.ChangeTracker.Entries<NoteAsrBatchJobEntity>()
                     .Where(x => x.State == EntityState.Modified))
            NoteAsrBatchRevision.Bump(entry.Entity);
        using var budget = new CancellationTokenSource(PersistenceBudget);
        try
        {
            await db.SaveChangesAsync(budget.Token);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Reload the winning row so callers can never persist a stale status over it.
            foreach (var entry in db.ChangeTracker.Entries<NoteAsrBatchJobEntity>()
                         .Where(x => x.State == EntityState.Modified))
                await entry.ReloadAsync(budget.Token);
            return false;
        }
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsDefiniteRejection(Exception error)
    {
        if (error is not MimoBatchException batch)
            return error is ArgumentException or UriFormatException or InvalidOperationException;
        if (batch.ErrorCode is 40302 or 50204 ||
            batch.ErrorCode is >= 42203 and <= 42210)
            return true;
        if (batch.ErrorCode != 50205) return false;
        var status = HttpStatus(batch.Message);
        return status is >= 400 and < 500 && status is not (408 or 409 or 425 or 429);
    }

    private static bool IsUncertainTransport(Exception error) =>
        error is HttpRequestException or IOException or JsonException or FormatException or InvalidDataException ||
        error is MimoBatchException { ErrorCode: 50205 } && !IsDefiniteRejection(error);

    private static int? HttpStatus(string message)
    {
        const string marker = "返回HTTP ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        return start + 3 <= message.Length && int.TryParse(message.AsSpan(start, 3), out var status)
            ? status
            : null;
    }

    private async Task<NoteAsrBatchJobEntity> OwnedAsync(Guid owner, Guid id, CancellationToken ct) =>
        await db.NoteAsrBatchJobs.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
        ?? throw new DomainException(40401, "Batch 任务不存在");

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用");
        return owner;
    }

    private string ReadStoredCredential(NoteAsrBatchJobEntity job)
    {
        if (string.IsNullOrWhiteSpace(job.CredentialSecret))
            throw new DomainException(40901, "Batch 凭据快照不可用；请人工核对，不会改用当前模型凭据");
        return ReadCredential(job.CredentialSecret);
    }

    private string ReadCredential(string protectedValue)
    {
        try
        {
            var value = secrets.Unprotect(protectedValue);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException) { }
        throw new DomainException(40901, "Batch 凭据当前不可用");
    }

    private static NoteAsrBatchJobDto Describe(NoteAsrBatchJobEntity x) => new(x.Id, x.TaskId, x.AttemptId,
        x.Status, x.StatusRaw, x.BatchId, x.InputFileId, x.OutputFileId, x.ErrorFileId, x.InputExpireAt,
        x.BatchExpiresAt, x.Model, x.BaseUrl, x.ConsentRef, x.CompletionWindow, x.TotalItems, x.CompletedItems,
        x.FailedItems, x.ResultJson, x.OutputImportedAt is not null, x.LastErrorCode, x.LastErrorMessage,
        x.LastPolledAt, x.CreatedAt, x.UpdatedAt);
}

/// <summary>Bumps the per-row optimistic concurrency token before every Batch job save.</summary>
internal static class NoteAsrBatchRevision
{
    public static void Bump(NoteAsrBatchJobEntity job) => job.Revision = Guid.NewGuid();
}
