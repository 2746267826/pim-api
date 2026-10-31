using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notes;

public sealed class NoteAsrService(NoteAsrTaskStore tasks, NoteMaterialUploadStore materials,
    PimDbContext db, ICurrentUserService user, AiAsrPresetRegistry presets, IConfiguration configuration,
    ISecretProtector secrets, NoteAsrRoutingService routing)
{
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");
    private bool BackgroundProcessingAvailable => !(bool.TryParse(configuration["DisableHangfire"], out var disabled) && disabled)
        && !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"));
    private NoteAsrTaskDto Describe(NoteAsrState state) => NoteAsrTaskStore.Describe(state) with
    { BackgroundProcessingAvailable = BackgroundProcessingAvailable };
    public async Task<NoteAsrTaskDto> StartAsync(StartNoteAsrRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "账号已改变，请重新核对材料和目标");
        if (request.TaskId == Guid.Empty || request.VersionId == Guid.Empty) throw new DomainException(40001, "任务或版本标识无效");
        if ((request.ParentTaskId is null) != string.IsNullOrWhiteSpace(request.ReprocessReason) ||
            request.ReprocessReason is { Length: > 240 })
            throw new DomainException(40001, "重跑来源和原因必须同时提供，原因最多240字");
        // A lost HTTP response replays an already persisted intent even after configuration changes.
        try
        {
            var old = await tasks.ReadAsync(owner, request.TaskId, ct);
            var hash = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (old.Fingerprint != hash) throw new DomainException(40901, "任务标识已绑定另一份输入，原任务保留");
            await EnsureAttemptAsync(owner, old, ct);
            return Describe(await tasks.ReadAsync(owner, request.TaskId, ct));
        }
        catch (DomainException error) when (error.ErrorCode == 40401) { }
        var version = await materials.GetAsync(owner, request.VersionId, ct);
        var resolved = await ResolveRouteAsync(owner, request, ct);
        var samplesPerCall = resolved?.SamplesPerCall ?? NoteAsrPlan.SamplesPerCall;
        if (resolved is null) await ValidateDestinationAsync(request.Consent, request.Language, ct);
        var state = await tasks.CreateAsync(owner, request, version, NoteAsrPlan.Build(version, samplesPerCall),
            resolved?.Consent, resolved?.ExecutionMode, samplesPerCall, ct);
        await EnsureAttemptAsync(owner, state, ct);
        return Describe(await tasks.ReadAsync(owner, request.TaskId, ct));
    }
    public async Task<IReadOnlyList<NoteAsrTaskDto>> ListAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var result = new List<NoteAsrTaskDto>();
        foreach (var (_, id) in tasks.Enumerate(owner)) result.Add(Describe(await tasks.ReadAsync(owner, id, ct)));
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }
    public async Task<NoteAsrSourcesDto> SourcesAsync(int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await materials.ListAsrSourcesAsync(owner, offset, limit, ct);
    }
    public async Task<NoteAsrDestinationsDto> DestinationsAsync(CancellationToken ct)
    {
        await RequireActiveOwnerAsync(ct);
        if (!await db.AiModelRuntime.AsNoTracking().AnyAsync(x => x.Enabled, ct))
            return new(0, []);
        var profiles = await db.AiModelProfiles.AsNoTracking()
            .Where(x => x.Enabled && (x.Capabilities & AiModelCapabilityFlags.Transcription) != 0)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var destinations = profiles.Select(TryDescribe).Where(x => x is not null).Cast<NoteAsrDestinationDto>().ToArray();
        return new(destinations.Length, destinations);
    }
    public async Task<NoteAsrTaskDto> GetAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return Describe(await ReconcileAsync(owner, id, ct));
    }
    public async Task<NoteAsrSegmentsDto> SegmentsAsync(Guid id, int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (offset < 0 || limit is < 1 or > 20) throw new DomainException(40001, "分片分页范围无效，单页最多20片");
        var state = await ReconcileAsync(owner, id, ct);
        var page = state.Steps.Skip(offset).Take(limit).ToArray();
        var logIds = page.Select(x => x.Current.LogId).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var logs = await db.AiRequestLogs.AsNoTracking().Where(x => x.UserId == owner && logIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ResponseText }).ToDictionaryAsync(x => x.Id, ct);
        var evidence = await CurrentEvidenceAsync(owner, state, ct);
        return new(state.Steps.Count, offset, page.Select(x => new NoteAsrSegmentDto(x.Index, x.RecordingId, x.RecordingSegment,
            x.ChunkIndex, x.FileIndex, x.Path, x.Sha256, x.SourceOffset, x.RecordingOffset, x.TimelineOffset, x.Samples,
            evidence.TryGetValue(x.Index, out var proof) ? "Succeeded" : x.Current.Status,
            proof?.ResultText ?? (x.Current.LogId is { } log && logs.TryGetValue(log, out var row) ? row.ResponseText : null),
            FlattenAttempts(x.Attempts).Select(a => new NoteAsrAttemptDto(a.RequestId, a.LogId, a.Status, a.ErrorMessage,
                a.EvidenceId)).ToArray(), proof?.Id, proof?.ReceiptKind, proof?.ProviderHandle, proof?.CoverageJson)).ToArray());
    }
    public async Task<NoteAsrTaskDto> StopAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var updated = await tasks.UpdateAsync(owner, id, state => state.StopRequested = true, ct);
        if (updated.AttemptId is { } attemptId)
            await routing.StopAttemptAsync(attemptId, ct);
        return Describe(updated);
    }
    public async Task<NoteAsrTaskDto> RetryAsync(Guid id, RetryNoteAsrRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "账号已改变，请重新核对");
        if (request.RetryId == Guid.Empty || request.SegmentIndexes is not { Count: > 0 and <= 5_000 } ||
            request.SegmentIndexes.Distinct().Count() != request.SegmentIndexes.Count)
            throw new DomainException(40001, "请选择待恢复分片，核对原目标，并确认重试可能重复计费");
        var before = await ReconcileAsync(owner, id, ct);
        if (before.Route == NoteAsrRoute.Local && request.Consent is not null)
            throw new DomainException(40901, "本机重跑不涉及云端外发，不需要云端确认");
        if (before.Route != NoteAsrRoute.Local &&
            (request.Consent?.Confirmed != true || !request.AcknowledgePossibleDuplicateCharge))
            throw new DomainException(40001, "云端重试需确认原目标与可能重复计费");
        var fingerprint = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (before.Retries.TryGetValue(request.RetryId, out var previous))
        {
            if (previous != fingerprint) throw new DomainException(40901, "恢复标识已绑定另一组分片，原恢复记录保留");
            return Describe(before);
        }
        // Null-vs-null means the local replay matches its task; cloud replays must re-confirm the exact consent.
        if (!Equals(request.Consent, before.Request.Consent)) throw new DomainException(40901, "重试目标与固定任务不一致，请新建任务");
        if (before.Route != NoteAsrRoute.Local)
            await ValidateDestinationAsync(request.Consent, before.Request.Language, ct);
        _ = await materials.GetAsync(owner, before.Request.VersionId, ct);
        // Validate every retry precondition before any mutation: a rejected request must leave both
        // the task file and the routing attempt exactly as it found them.
        if (before.Route == NoteAsrRoute.Local)
        {
            if (before.AttemptId is not { } attemptId) throw new DomainException(40901, "本机任务缺少执行快照");
            var attempt = await db.NoteAsrAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct)
                ?? throw new DomainException(40901, "本机任务缺少执行快照");
            var hasActiveLease = await db.NoteAsrLeases.AsNoTracking().AnyAsync(l =>
                l.OwnerId == owner && l.AttemptId == attemptId && l.Status == "Active", ct);
            if (attempt.Route != NoteAsrRoute.Local || hasActiveLease ||
                attempt.State is not ("Failed" or "Unknown" or "Queued"))
                throw new DomainException(40901, "本机执行快照未处于可人工恢复状态，原记录保留");
        }
        var updated = await tasks.UpdateAsync(owner, id, state =>
        {
            if (state.Retries.TryGetValue(request.RetryId, out var existing))
            {
                if (existing != fingerprint) throw new DomainException(40901, "恢复标识已绑定另一组分片，原恢复记录保留");
                return;
            }
            if (state.Revision != request.ExpectedRevision) throw new DomainException(40901, "任务进度已变化，请刷新后核对");
            if (state.Retries.Count >= 50) throw new DomainException(41301, "本任务恢复次数已达50，原结果保留");
            foreach (var index in request.SegmentIndexes)
                if (index < 0 || index >= state.Steps.Count || state.Steps[index].Current.Status is "Succeeded" or "Running" ||
                    state.Steps[index].Attempts.Count >= 10) throw new DomainException(40901, "不能重跑成功/执行中分片，或已达单片10次上限");
            foreach (var index in request.SegmentIndexes)
                if (state.Steps[index].Current.Status != "Queued") state.Steps[index].Attempts.Add(new());
            state.Retries.Add(request.RetryId, fingerprint); state.StopRequested = false;
        }, ct);
        // The task file is committed first; only then requeue the durable attempt. A crash between
        // the two leaves Queued work that claim reconciles and the retry is replayable idempotently.
        if (before.Route == NoteAsrRoute.Local) await RequireLocalRetryAttemptAsync(owner, updated, ct);
        return Describe(updated);
    }

    private async Task RequireLocalRetryAttemptAsync(Guid owner, NoteAsrState state, CancellationToken ct)
    {
        if (state.AttemptId is not { } attemptId)
            throw new DomainException(40901, "本机任务缺少执行快照");
        var now = DateTimeOffset.UtcNow;
        var changed = await db.NoteAsrAttempts
            .Where(x => x.Id == attemptId && x.OwnerId == owner && x.Route == NoteAsrRoute.Local &&
                (x.State == "Failed" || x.State == "Unknown" || x.State == "Queued") &&
                !db.NoteAsrLeases.Any(l => l.OwnerId == owner && l.AttemptId == attemptId && l.Status == "Active"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, "Queued")
                .SetProperty(x => x.StopRequested, false)
                .SetProperty(x => x.LastErrorCode, (string?)null)
                .SetProperty(x => x.LastErrorMessage, (string?)null)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (changed == 0) throw new DomainException(40901, "本机执行快照未处于可人工恢复状态，原记录保留");
    }

    /// <summary>
    /// Resolves the route once for a new task. No route config/no explicit route remains the legacy
    /// dedicated cloud-ASR shape and deliberately does not mint a routing attempt.
    /// </summary>
    private async Task<ResolvedRoute?> ResolveRouteAsync(Guid owner, StartNoteAsrRequest request, CancellationToken ct)
    {
        var config = await db.NoteAsrRouteConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        if (config is null)
        {
            if (request.Route is not null)
                throw new DomainException(40901, "路由配置不存在，请刷新后重新确认");
            return null;
        }

        if (request.Route is { } requested &&
            (requested.ConfigRevision != config.Revision ||
             !string.Equals(requested.ConsentVersion, config.ConsentVersion, StringComparison.Ordinal)))
            throw new DomainException(40901, "路由配置已变化，请刷新后重新确认");

        var route = request.Route?.Route?.Trim() ?? config.DefaultRoute;
        if (!NoteAsrRoute.Known(route)) throw new DomainException(40001, "执行位置无效");
        var executionMode = request.Route?.ExecutionMode ?? NoteAsrExecutionMode.Realtime;
        if (!NoteAsrExecutionMode.Known(executionMode))
            throw new DomainException(40001, "执行方式无效");
        if (executionMode == NoteAsrExecutionMode.Batch && route != NoteAsrRoute.CloudMultimodal)
            throw new DomainException(40001, "Batch 仅支持已显式确认的 MiMo 多模态云端任务");
        if (executionMode == NoteAsrExecutionMode.Batch &&
            (request.Route is null || !request.Route.Confirmed || !request.Route.BatchScopeConfirmed))
            throw new DomainException(40001, "Batch 必须在创建 attempt 前确认整批分片范围与付费方式");
        if (route == NoteAsrRoute.Local)
        {
            if (executionMode != NoteAsrExecutionMode.Realtime)
                throw new DomainException(40001, "本机节点只支持实时执行方式");
            if (string.IsNullOrWhiteSpace(config.LocalModelId))
                throw new DomainException(40901, "本机执行位置尚未配置模型");
            if (request.Route is { Confirmed: false })
                throw new DomainException(40001, "请确认本次本机转写");
            return new(new NoteAsrRouteConsent(NoteAsrRoute.Local, config.Revision, config.ConsentVersion,
                true, executionMode, request.Route?.BatchScopeConfirmed ?? false),
                executionMode, NoteAsrPlan.SamplesPerCall);
        }

        if (request.Consent is not { Confirmed: true } || request.Route is { Confirmed: false })
            throw new DomainException(40001, "请明确确认云端音频外发、长等待与可能计费");
        if (request.Consent.ModelId == Guid.Empty)
            throw new DomainException(40901, "云端目标未选择，请刷新后重新确认");
        var descriptor = await ValidateDestinationAsync(request.Consent, request.Language, ct);
        if (route == NoteAsrRoute.CloudMultimodal && descriptor.Id != MimoMultimodalPreset.PresetId)
            throw new DomainException(40901, "多模态执行位置尚未配置 MiMo 多模态预设");
        if (route == NoteAsrRoute.CloudAsr && descriptor.Id == MimoMultimodalPreset.PresetId)
            throw new DomainException(40901, "专用云端 ASR 路由不能使用多模态预设");

        var samplesPerCall = route == NoteAsrRoute.CloudMultimodal
            ? MultimodalSamplesPerCall(descriptor)
            : NoteAsrPlan.SamplesPerCall;
        return new(new NoteAsrRouteConsent(route, config.Revision, config.ConsentVersion, true,
            executionMode, request.Route?.BatchScopeConfirmed ?? false), executionMode, samplesPerCall);
    }

    private static int MultimodalSamplesPerCall(AiAsrPresetDto descriptor)
    {
        // The provider limit is a byte budget; keep a bounded duration slice too so long audio remains
        // ordered and resumeable instead of relying on one oversized response.
        var bytesBounded = (descriptor.MaxAudioBytes - 44L) / 2L;
        if (bytesBounded <= 0) throw new DomainException(40901, "多模态预设音频预算不足");
        var durationBounded = (long)NoteAsrPlan.SampleRate * 10 * 60;
        return checked((int)Math.Min(bytesBounded, durationBounded));
    }

    private static IEnumerable<NoteAsrAttempt> FlattenAttempts(IEnumerable<NoteAsrAttempt> attempts)
    {
        foreach (var attempt in attempts)
        {
            yield return attempt;
            foreach (var child in FlattenAttempts(attempt.Children)) yield return child;
        }
    }

    private async Task EnsureAttemptAsync(Guid owner, NoteAsrState state, CancellationToken ct)
    {
        if (state.Route is null) return;
        var existing = await db.NoteAsrAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner && x.TaskId == state.Request.TaskId, ct);
        if (state.AttemptId is { } bound)
        {
            if (existing is null || existing.Id != bound)
                throw new DomainException(40901, "任务绑定的执行快照不存在，请核对记录");
        }
        else
        {
            if (existing is null)
            {
                var consent = new NoteAsrRouteConsent(state.Route, state.RouteConfigRevision,
                    state.RouteConsentVersion, true,
                    state.ExecutionMode ?? NoteAsrExecutionMode.Realtime,
                    state.Request.Route?.BatchScopeConfirmed ?? false);
                existing = await routing.CreateAttemptCoreAsync(owner, state.Request.TaskId,
                    state.Request.VersionId, state.Steps.Count, state.Request.Language, consent, ct);
            }
            await tasks.UpdateAsync(owner, state.Request.TaskId, current =>
            {
                current.AttemptId = existing.Id;
                current.ExecutionMode = existing.ExecutionMode ?? "realtime";
            }, ct);
        }

        if (existing.Route != state.Route || existing.VersionId != state.Request.VersionId ||
            existing.ExpectedSegments != state.Steps.Count ||
            existing.ExecutionMode != (state.ExecutionMode ?? NoteAsrExecutionMode.Realtime))
            throw new DomainException(40901, "任务与执行快照不一致，请核对记录");
    }

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用，不能创建或查看ASR任务");
        return owner;
    }
    private async Task<NoteAsrState> ReconcileAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var state = await tasks.ReadAsync(owner, id, ct);
        var evidence = await CurrentEvidenceAsync(owner, state, ct);
        if (evidence.Count > 0)
        {
            var observedAttempt = state.AttemptId;
            var observedVersion = state.Request.VersionId;
            var observedInput = state.InputSha256;
            state = await tasks.UpdateAsync(owner, id, current =>
            {
                if (current.AttemptId != observedAttempt ||
                    current.Request.VersionId != observedVersion ||
                    current.InputSha256 != observedInput) return;
                foreach (var (segmentIndex, proof) in evidence)
                {
                    if (segmentIndex < 0 || segmentIndex >= current.Steps.Count) continue;
                    current.Steps[segmentIndex].Current.EvidenceId = proof.Id;
                    current.Steps[segmentIndex].Current.Status = "Succeeded";
                    current.Steps[segmentIndex].Current.ErrorMessage = null;
                }
            }, ct);
        }
        state = await routing.RepairAcknowledgedTaskAsync(owner, state, ct);
        var waiting = state.Steps.Where(x => x.Current.Status is "Unknown" or "Running").ToArray();
        if (waiting.Length == 0) return state;
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var requests = waiting.Select(x => x.Current.RequestId).ToArray();
        var receipts = await db.AiRequestLogs.AsNoTracking().Where(x => x.UserId == owner &&
            x.Purpose == "ai.asr-call" && x.SourceObjectId == id.ToString() && requests.Contains(x.ClientRequestId!.Value) && x.Status != "running").ToListAsync(ct);
        if (receipts.Count == 0) return state;
        return await tasks.UpdateAsync(owner, id, current =>
        {
            foreach (var step in current.Steps.Where(x => x.Current.Status is "Unknown" or "Running"))
            {
                var receipt = receipts.SingleOrDefault(x => x.ClientRequestId == step.Current.RequestId);
                if (receipt is null) continue;
                var result = AiCallAudit.Result(receipt, true);
                step.Current.LogId = result.LogId; step.Current.Status = result.Status.ToString(); step.Current.ErrorMessage = result.ErrorMessage;
            }
        }, ct);
    }

    private async Task<Dictionary<int, NoteAsrSegmentEvidenceEntity>> CurrentEvidenceAsync(Guid owner,
        NoteAsrState state, CancellationToken ct)
    {
        if (state.AttemptId is not { } attemptId) return [];
        var fence = await db.NoteAsrAttempts.AsNoTracking()
            .Where(x => x.Id == attemptId && x.OwnerId == owner).Select(x => (long?)x.FencingToken).SingleOrDefaultAsync(ct);
        if (fence is null) return [];
        var rows = await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x => x.OwnerId == owner &&
                x.AttemptId == attemptId && x.FencingToken == fence.Value &&
                x.TaskId == state.Request.TaskId && x.VersionId == state.Request.VersionId)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return rows.GroupBy(x => x.SegmentIndex).ToDictionary(x => x.Key, x => x.Last());
    }
    private async Task<AiAsrPresetDto> ValidateDestinationAsync(AiAsrDestinationConsent? consent, string language, CancellationToken ct)
    {
        if (consent?.Confirmed != true) throw new DomainException(40001, "请明确同意将全部录音分片发送至所示ASR目标");
        var model = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == consent.ModelId, ct);
        if (model is null || !model.Enabled ||
            (model.Capabilities & AiModelCapabilityFlags.Transcription) == 0 ||
            !await db.AiModelRuntime.AnyAsync(x => x.Enabled, ct)) throw new DomainException(40901, "ASR配置或SDK当前不可用");
        var preset = presets.Resolve(model.AsrPresetId); var descriptor = preset.Descriptor;
        preset.ValidateModel(model.Model);
        if (!CredentialAvailable(model.ApiKeySecret)) throw new DomainException(40901, "ASR凭据当前不可用，请管理员重新保存");
        if (consent.ModelRevision != model.Revision || consent.PresetId != descriptor.Id || consent.PresetVersion != descriptor.Version ||
            consent.Endpoint != AiAsrPresetRegistry.OperationEndpoint(preset.NormalizeEndpoint(model.BaseUrl), descriptor))
            throw new DomainException(40901, "ASR目标配置已变化，请刷新并重新确认");
        if (language is null || !descriptor.Languages.Contains(language) || !descriptor.Formats.Contains("wav") ||
            descriptor.MaxAudioBytes < 44 + NoteAsrPlan.SamplesPerCall * 2) throw new DomainException(40001, "当前预设不支持本次语言或一分钟WAV分片");
        return descriptor;
    }
    private NoteAsrDestinationDto? TryDescribe(AiModelProfileEntity profile)
    {
        try
        {
            var preset = presets.Resolve(profile.AsrPresetId); var descriptor = preset.Descriptor;
            preset.ValidateModel(profile.Model);
            if (!CredentialAvailable(profile.ApiKeySecret) || !descriptor.Formats.Contains("wav") ||
                descriptor.MaxAudioBytes < 44 + NoteAsrPlan.SamplesPerCall * 2) return null;
            var endpoint = AiAsrPresetRegistry.OperationEndpoint(preset.NormalizeEndpoint(profile.BaseUrl), descriptor);
            return new(profile.Id, profile.Revision, profile.Name, profile.Model, endpoint, descriptor.Id,
                descriptor.Version, descriptor.Languages, BackgroundProcessingAvailable);
        }
        catch (Exception error) when (error is DomainException or UriFormatException or ArgumentException)
        { return null; }
    }
    private bool CredentialAvailable(string protectedValue)
    {
        try { return !string.IsNullOrEmpty(secrets.Unprotect(protectedValue)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return false; }
    }

    private sealed record ResolvedRoute(NoteAsrRouteConsent Consent, string ExecutionMode, int SamplesPerCall);
}
