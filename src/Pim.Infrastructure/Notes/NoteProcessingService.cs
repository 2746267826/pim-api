using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
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
    NoteProcessingTargetPolicyService targetPolicies,
    ILogger<NoteProcessingService> logger)
{
    internal const string FullVersionScope = "FullVersion";
    internal Func<CancellationToken, Task>? BeforePlanResolutionForTests { get; set; }
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
            // Automatic reconciliation never reads request.Consent; the standing authorization
            // snapshot is resolved and persisted inside the owner/processing lock.
            var request = new StartNoteProcessingRequest(version.Upload.NoteId, versionId, null!);
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
        if (!automatic && (request.Consent is null || !request.Consent.Confirmed ||
                           !request.Consent.PossibleChargeAcknowledged))
            throw new DomainException(40001,
                "发起处理前必须核对处理目标、材料范围、费用提示与授权 revision");
        var supplied = await materials.GetAsync(owner, request.VersionId, ct);
        if (supplied.Upload.NoteId != request.NoteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");

        var processingId = NoteProcessingIdentity.Processing(owner, request.VersionId);
        return await processing.WithLockAsync(owner, async lockedCt =>
        {
            // Settings and automatic dispatch share the owner orchestration lock, so a
            // concurrent revoke can never race ahead of a paid child task allocation.
            var preferences = await PreferencesAsync(owner, lockedCt);
            var autoEnabled = preferences.AutoProcessingEnabled;
            var version = await materials.GetAsync(owner, request.VersionId, lockedCt);
            if (automatic && (version.Upload.Phase != "Uploaded" || !version.Upload.IsCurrent))
                return null;
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
            state.AutoRequested = automatic;
            if (!automatic) state.ManualStarted = true;

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
            var asrState = hasAudio
                ? await ResolveAsrStateAsync(owner, version, state, lockedCt)
                : null;
            var derivedState = await ResolveDerivedStateAsync(owner, version, state, lockedCt);
            var asrCreated = asrState is not null;
            var derivedCreated = derivedState is not null;
            var allRequiredCreated = (!hasAudio || asrCreated) && derivedCreated;
            // A record with an authorization snapshot or start timestamp already owns its child
            // chains. Later target/authorization drift must not rewrite it back to a pending or
            // not-started explanation; execution-time consent checks still stop paid calls.
            // Out-of-band chains without either marker still use the validating path below.
            if (automatic && allRequiredCreated &&
                (state.Authorization is not null || state.StartedAt is not null))
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            if (automatic && !autoEnabled)
                return await SavePendingAsync(state, "auto_processing_disabled",
                    "自动笔记处理未开启；材料已完整，可在笔记处理页手动发起", lockedCt);
            if (automatic && !preferences.StandingAuthorizationValid)
                return await SavePendingAsync(state, "standing_authorization_required",
                    "账号默认处理目标已变化或持续授权无效；请在设置中核对当前目标、材料范围与可能费用后重新开启",
                    lockedCt);

            ResolvedProcessingPlan plan;
            try
            {
                if (BeforePlanResolutionForTests is { } barrier) await barrier(lockedCt);
                plan = await ResolvePlanAsync(owner, version, preferences, lockedCt);
            }
            catch (DomainException error)
            {
                state.PendingReason = error.Message;
                state.Issues =
                [
                    new("processing_target_unavailable", error.Message,
                        hasAudio ? NoteProcessingTaskKind.Asr : NoteProcessingTaskKind.Derived)
                ];
                _ = await processing.SaveLockedAsync(state, lockedCt);
                if (!automatic) throw;
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            }

            // The standing authorization was evaluated against the target policy read before
            // the plan resolved concrete models. Re-read it after plan creation so a concurrent
            // profile/route/preset change cannot start a paid chain under an older policy.
            var currentTargetPolicy = await targetPolicies.ResolveAsync(owner, lockedCt);
            if (currentTargetPolicy.Revision != preferences.CurrentDefaultTargetRevision)
            {
                var message =
                    "账号默认处理目标在生成处理计划时发生变化；请刷新处理计划后重新确认";
                if (!automatic)
                    throw new DomainException(40901, message);
                state.PendingReason = message;
                state.Issues =
                [
                    new("processing_target_changed", message,
                        hasAudio ? NoteProcessingTaskKind.Asr : NoteProcessingTaskKind.Derived)
                ];
                _ = await processing.SaveLockedAsync(state, lockedCt);
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            }

            var targetMismatch = TargetMismatch(plan, asrState, derivedState, hasAudio,
                owner, request.VersionId);
            if (targetMismatch is not null)
            {
                if (!automatic)
                    throw new DomainException(40901, targetMismatch);
                state.PendingReason = targetMismatch;
                state.Issues =
                [
                    new("processing_target_changed", targetMismatch,
                        hasAudio ? NoteProcessingTaskKind.Asr : NoteProcessingTaskKind.Derived)
                ];
                _ = await processing.SaveLockedAsync(state, lockedCt);
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            }

            NoteProcessingAuthorizationDto authorization;
            if (automatic)
            {
                authorization = Snapshot(plan, preferences.StandingAuthorizationRevision,
                    NoteProcessingAuthorizationSource.Standing);
            }
            else
            {
                ValidateManualConsent(request.Consent, plan.Plan, preferences,
                    state, asrCreated || derivedCreated);
                authorization = Snapshot(plan,
                    request.Consent?.StandingAuthorizationRevision,
                    NoteProcessingAuthorizationSource.Manual);
            }
            if (state.Authorization is not null && (asrCreated || derivedCreated) &&
                state.Authorization.AuthorizationRevision !=
                authorization.AuthorizationRevision)
            {
                if (!automatic)
                    throw new DomainException(40901,
                        "该材料版本已按另一份授权创建处理任务，请使用原任务状态或显式重跑");
                return await SavePendingAsync(state, "processing_authorization_changed",
                    "处理目标、材料范围或费用提示已变化；已有任务保留，本次不会自动追加新任务",
                    lockedCt);
            }
            if (state.StartedAt is not null && state.Authorization is not null &&
                state.Authorization.AuthorizationRevision != authorization.AuthorizationRevision)
                throw new DomainException(40901,
                    "该材料版本已按另一份授权创建处理任务，请使用原任务状态或显式重跑");
            state.Authorization = authorization;
            _ = await processing.SaveLockedAsync(state, lockedCt);

            if (asrState is not null)
            {
                try
                {
                    asrState = await asr.EnsureAttemptForOwnerAsync(owner,
                        asrState.Request.TaskId, request.VersionId, lockedCt);
                    state.AsrTaskId = asrState.Request.TaskId;
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

            if (allRequiredCreated)
            {
                state.PendingReason = null;
                state.Issues = [];
                state.StartedAt ??= DateTimeOffset.UtcNow;
                state.AutoStarted |= automatic;
                _ = await processing.SaveLockedAsync(state, lockedCt);
                return await BuildStatusAsync(owner, version, state, autoEnabled, lockedCt);
            }

            NoteProcessingTaskKind? startingTask = null;
            try
            {
                var asrRequestBefore = JsonSerializer.Serialize(state.AsrRequest);
                var derivedRequestBefore = JsonSerializer.Serialize(state.DerivedRequest);
                if (!hasAudio)
                    state.AsrRequest = null;
                else if (!asrCreated)
                    state.AsrRequest = Confirm(plan.AsrRequest
                        ?? throw new DomainException(40901, "ASR 处理计划缺少固定任务"));
                if (!derivedCreated)
                {
                    var actualAsrTaskId = asrState?.Request.TaskId
                        ?? NoteProcessingIdentity.Task(owner, request.VersionId,
                            NoteProcessingTaskKind.Asr);
                    state.DerivedRequest = new StartNoteDerivedTaskRequest(
                        NoteProcessingIdentity.Task(owner, request.VersionId,
                            NoteProcessingTaskKind.Derived),
                        owner, request.VersionId, hasAudio ? actualAsrTaskId : null,
                        plan.DerivedConsent with { Confirmed = true });
                }
                state.PendingReason = "处理任务正在持久化入队";
                state.Issues = [];
                // Repeated compensation scans must not rotate the record when the frozen
                // child requests are unchanged.
                if (asrRequestBefore != JsonSerializer.Serialize(state.AsrRequest) ||
                    derivedRequestBefore != JsonSerializer.Serialize(state.DerivedRequest))
                    _ = await processing.SaveLockedAsync(state, lockedCt);

                if (hasAudio && !asrCreated)
                {
                    startingTask = NoteProcessingTaskKind.Asr;
                    var startedAsr = await asr.StartForOwnerAsync(owner, state.AsrRequest!,
                        lockedCt);
                    state.AsrTaskId = startedAsr.TaskId;
                    state.AsrRequest = state.AsrRequest! with { TaskId = startedAsr.TaskId };
                    if (!derivedCreated && state.DerivedRequest is { } derivedRequest &&
                        derivedRequest.AsrTaskId != startedAsr.TaskId)
                        state.DerivedRequest = derivedRequest with
                        {
                            AsrTaskId = startedAsr.TaskId
                        };
                    _ = await processing.SaveLockedAsync(state, lockedCt);
                }
                if (!derivedCreated)
                {
                    startingTask = NoteProcessingTaskKind.Derived;
                    var derivedRequest = state.DerivedRequest!;
                    var startedDerived = await derived.StartForOwnerAsync(owner,
                        derivedRequest,
                        waitForAsr: hasAudio, allowIncompleteAsr: false, lockedCt);
                    state.DerivedTaskId = startedDerived.TaskId;
                    state.DerivedRequest = derivedRequest with
                    {
                        TaskId = startedDerived.TaskId
                    };
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

    private async Task<NoteAsrState?> ResolveAsrStateAsync(Guid owner,
        NoteMaterialVersionDto version, NoteProcessingRecord state, CancellationToken ct)
    {
        var taskId = state.AsrTaskId ?? NoteProcessingIdentity.Task(owner,
            version.Upload.VersionId, NoteProcessingTaskKind.Asr);
        var value = await TryReadAsrAsync(owner, taskId, ct)
            ?? await asrTasks.TryReadInitialForVersionAsync(owner, version.Upload.VersionId, ct);
        if (value is not null && value.Request.ParentTaskId is null)
            value = await asrTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct) ?? value;
        if (value is not null) state.AsrTaskId = value.Request.TaskId;
        return value;
    }

    private async Task<NoteDerivedTaskState?> ResolveDerivedStateAsync(Guid owner,
        NoteMaterialVersionDto version, NoteProcessingRecord state, CancellationToken ct)
    {
        var taskId = state.DerivedTaskId ?? NoteProcessingIdentity.Task(owner,
            version.Upload.VersionId, NoteProcessingTaskKind.Derived);
        var value = await TryReadDerivedAsync(owner, taskId, ct)
            ?? await derivedTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct);
        if (value is not null)
            value = await derivedTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct) ?? value;
        if (value is not null) state.DerivedTaskId = value.Request.TaskId;
        return value;
    }

    public async Task<NoteProcessingPlanDto> PlanAsync(Guid noteId, Guid versionId,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var version = await materials.GetAsync(owner, versionId, ct);
        if (version.Upload.NoteId != noteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
        var preferences = await PreferencesAsync(owner, ct);
        return RedactPlan((await ResolvePlanAsync(owner, version, preferences, ct)).Plan);
    }

    private async Task<ResolvedProcessingPlan> ResolvePlanAsync(Guid owner,
        NoteMaterialVersionDto version, NoteProcessingPreferencesDto preferences,
        CancellationToken ct)
    {
        var hasAudio = HasAudio(version);
        var asrRequest = hasAudio
            ? await asr.BuildDefaultRequestForProcessingAsync(owner, version,
                confirmed: false, ct)
            : null;
        var derivedConsent = await derived.ResolveDefaultConsentForProcessingAsync(
            owner, version, confirmed: false, ct);

        var modelIds = new[] { asrRequest?.Consent?.ModelId, derivedConsent.ModelId }
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var profiles = await db.AiModelProfiles.AsNoTracking()
            .Where(x => modelIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.Model })
            .ToDictionaryAsync(x => x.Id, ct);
        var routeConfig = asrRequest?.Route?.Route == NoteAsrRoute.Local
            ? await db.NoteAsrRouteConfigs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OwnerId == owner, ct)
            : null;

        var targets = new List<NoteProcessingTargetDto>(2);
        if (asrRequest is not null)
        {
            var consent = asrRequest.Consent;
            var route = asrRequest.Route?.Route;
            var local = route == NoteAsrRoute.Local;
            profiles.TryGetValue(consent?.ModelId ?? Guid.Empty, out var profile);
            targets.Add(new(NoteProcessingTaskKind.Asr, true, consent is not null,
                consent?.ModelId,
                consent?.ModelRevision ?? LocalModelRevision(routeConfig),
                profile?.Name ?? (local ? routeConfig?.LocalModelId : null),
                profile?.Model ?? (local ? routeConfig?.LocalModelId : null),
                consent?.Endpoint, route, asrRequest.Route?.ExecutionMode,
                consent?.PresetId, consent?.PresetVersion,
                asrRequest.Route?.ConfigRevision, asrRequest.Route?.ConsentVersion));
        }
        profiles.TryGetValue(derivedConsent.ModelId, out var derivedProfile);
        targets.Add(new(NoteProcessingTaskKind.Derived, true, true,
            derivedConsent.ModelId, derivedConsent.ModelRevision,
            derivedProfile?.Name, derivedProfile?.Model, derivedConsent.Endpoint,
            "cloud", NoteAsrExecutionMode.Realtime, null, null, null, null));

        var possibleCharge = targets.Any(target => target.Paid);
        var materialScopeText = hasAudio
            ? NoteProcessingAuthorizationPurpose.MaterialScopeText
            : "当前固定版本的全部材料（正文、图片与手写）";
        var revision = AuthorizationRevision(owner, version.Upload.VersionId, targets,
            possibleCharge, materialScopeText);
        var standingRevision = preferences.StandingAuthorizationValid
            ? preferences.StandingAuthorizationRevision
            : null;
        var plan = new NoteProcessingPlanDto(version.Upload.NoteId, version.Upload.VersionId,
            NoteProcessingAuthorizationPurpose.NativeNoteProcessing,
            NoteProcessingAuthorizationPurpose.PurposeText,
            NoteProcessingAuthorizationPurpose.MaterialScope, materialScopeText,
            NoteProcessingAuthorizationRules.CostNotice, possibleCharge, revision,
            standingRevision, preferences.StandingAuthorizationValid,
            hasAudio, targets);
        return new(asrRequest, derivedConsent, plan);
    }

    private static void ValidateManualConsent(NoteProcessingConsent? consent,
        NoteProcessingPlanDto plan, NoteProcessingPreferencesDto preferences,
        NoteProcessingRecord state, bool anyTaskCreated)
    {
        if (consent is null)
            throw new DomainException(40001,
                "发起处理前必须核对处理目标、材料范围、费用提示与授权 revision");
        if (!consent.Confirmed || !consent.PossibleChargeAcknowledged)
            throw new DomainException(40001,
                "请明确确认处理目标、材料范围，并知悉云端处理可能产生费用");
        if (!string.Equals(consent.Purpose, plan.Purpose, StringComparison.Ordinal) ||
            !string.Equals(consent.MaterialScope, plan.MaterialScope, StringComparison.Ordinal) ||
            !string.Equals(consent.CostNotice, plan.CostNotice, StringComparison.Ordinal) ||
            consent.AuthorizationRevision != plan.AuthorizationRevision)
            throw new DomainException(40901,
                "处理目标、材料范围或费用提示已变化，请刷新处理计划后重新确认");
        if (consent.StandingAuthorizationRevision is { } standing &&
            (!preferences.StandingAuthorizationValid ||
             standing != preferences.StandingAuthorizationRevision))
            throw new DomainException(40901, "持续授权已变化，请刷新后重新确认");
        if (state.Authorization is not null && anyTaskCreated &&
            state.Authorization.AuthorizationRevision != plan.AuthorizationRevision)
            throw new DomainException(40901,
                "该材料版本已按另一份授权创建处理任务，请使用原任务状态或显式重跑");
    }

    private static NoteProcessingAuthorizationDto Snapshot(ResolvedProcessingPlan plan,
        Guid? standingAuthorizationRevision, string source) =>
        new(plan.Plan.Purpose, plan.Plan.PurposeText, plan.Plan.MaterialScope,
            plan.Plan.MaterialScopeText, plan.Plan.CostNotice, plan.Plan.PossibleCharge,
            Confirmed: true, PossibleChargeAcknowledged: true,
            plan.Plan.AuthorizationRevision, standingAuthorizationRevision, source,
            plan.Plan.Targets);

    private static string? TargetMismatch(ResolvedProcessingPlan plan, NoteAsrState? asrState,
        NoteDerivedTaskState? derivedState, bool hasAudio, Guid owner, Guid versionId)
    {
        if (asrState is not null && plan.AsrRequest is not null &&
            !SameAsrTarget(plan.AsrRequest, asrState.Request))
            return "已有转写任务使用了不同的处理目标；原任务保留，本次不会追加新任务";
        if (derivedState is null) return null;
        Guid? plannedAsrTaskId = hasAudio
            ? asrState?.Request.TaskId ?? NoteProcessingIdentity.Task(owner, versionId,
                NoteProcessingTaskKind.Asr)
            : null;
        return derivedState.Request.VersionId == versionId &&
               derivedState.Request.AsrTaskId == plannedAsrTaskId &&
               Unconfirmed(derivedState.Request.Consent) == Unconfirmed(plan.DerivedConsent)
            ? null
            : "已有整理任务使用了不同的处理目标；原任务保留，本次不会追加新任务";
    }

    private static bool SameAsrTarget(StartNoteAsrRequest planned,
        StartNoteAsrRequest existing) =>
        planned.VersionId == existing.VersionId &&
        planned.ExpectedUserId == existing.ExpectedUserId &&
        string.Equals(planned.Language, existing.Language, StringComparison.Ordinal) &&
        Unconfirmed(planned.Consent) == Unconfirmed(existing.Consent) &&
        Unconfirmed(planned.Route) == Unconfirmed(existing.Route);

    private static AiAsrDestinationConsent? Unconfirmed(AiAsrDestinationConsent? consent) =>
        consent is null ? null : consent with { Confirmed = false };

    private static NoteAsrRouteConsent? Unconfirmed(NoteAsrRouteConsent? route) =>
        route is null ? null : route with { Confirmed = false };

    private static NoteDerivedGenerationConsent Unconfirmed(
        NoteDerivedGenerationConsent consent) =>
        consent with { Confirmed = false };

    private static StartNoteAsrRequest Confirm(StartNoteAsrRequest request) =>
        request with
        {
            Consent = request.Consent is null
                ? null
                : request.Consent with { Confirmed = true },
            Route = request.Route is null
                ? null
                : request.Route with { Confirmed = true }
        };

    private static Guid? LocalModelRevision(
        Pim.Infrastructure.Data.Entities.NoteAsrRouteConfigEntity? config) =>
        Guid.TryParse(config?.LocalModelRevision, out var revision) ? revision : null;

    private static Guid AuthorizationRevision(Guid owner, Guid versionId,
        IReadOnlyList<NoteProcessingTargetDto> targets, bool possibleCharge,
        string materialScopeText)
    {
        var payload = JsonSerializer.Serialize(new
        {
            owner,
            versionId,
            purpose = NoteProcessingAuthorizationPurpose.NativeNoteProcessing,
            purposeText = NoteProcessingAuthorizationPurpose.PurposeText,
            materialScope = NoteProcessingAuthorizationPurpose.MaterialScope,
            materialScopeText,
            costNotice = NoteProcessingAuthorizationRules.CostNotice,
            possibleCharge,
            targets
        });
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload));
        var identity = bytes.AsSpan(0, 16).ToArray();
        identity[6] = (byte)((identity[6] & 0x0f) | 0x50);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new Guid(identity);
    }

    private sealed record ResolvedProcessingPlan(
        StartNoteAsrRequest? AsrRequest,
        NoteDerivedGenerationConsent DerivedConsent,
        NoteProcessingPlanDto Plan);

    private async Task<NoteProcessingStatusDto> GetForOwnerAsync(Guid owner, Guid noteId,
        Guid versionId, CancellationToken ct)
    {
        await RequireActiveOwnerAsync(owner, ct);
        var version = await materials.GetAsync(owner, versionId, ct);
        if (version.Upload.NoteId != noteId)
            throw new DomainException(40401, "材料版本不存在或不属于当前笔记");
        var autoEnabled = (await PreferencesAsync(owner, ct)).AutoProcessingEnabled;
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
        var asrTaskId = state?.AsrTaskId ?? NoteProcessingIdentity.Task(owner,
            version.Upload.VersionId, NoteProcessingTaskKind.Asr);
        var derivedTaskId = state?.DerivedTaskId ?? NoteProcessingIdentity.Task(owner,
            version.Upload.VersionId, NoteProcessingTaskKind.Derived);
        var asrState = hasAudio ? await TryReadAsrAsync(owner, asrTaskId, ct) : null;
        var derivedState = await TryReadDerivedAsync(owner, derivedTaskId, ct);
        if (hasAudio && asrState is null)
        {
            asrState = await asrTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct);
            if (asrState is not null) asrTaskId = asrState.Request.TaskId;
        }
        if (derivedState is null)
        {
            derivedState = await derivedTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct);
            if (derivedState is not null) derivedTaskId = derivedState.Request.TaskId;
        }
        if (asrState is not null && asrState.Request.ParentTaskId is null)
        {
            var canonicalAsr = await asrTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct);
            if (canonicalAsr is not null)
            {
                asrState = canonicalAsr;
                asrTaskId = canonicalAsr.Request.TaskId;
            }
        }
        if (derivedState is not null)
        {
            var canonicalDerived = await derivedTasks.TryReadInitialForVersionAsync(owner,
                version.Upload.VersionId, ct);
            if (canonicalDerived is not null)
            {
                derivedState = canonicalDerived;
                derivedTaskId = canonicalDerived.Request.TaskId;
            }
        }
        var asrChain = await DescribeAsrAsync(asrTaskId, asrState, hasAudio, version, state, ct);
        var derivedChain = await DescribeDerivedAsync(derivedTaskId, derivedState, hasAudio,
            asrState, version, state, ct);
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
            artifacts, issues, created, updated,
            state?.Authorization is null ? null : RedactAuthorization(state.Authorization));
    }

    private NoteProcessingPlanDto RedactPlan(NoteProcessingPlanDto plan) =>
        plan with { Targets = RedactTargets(plan.Targets) };

    private NoteProcessingAuthorizationDto RedactAuthorization(
        NoteProcessingAuthorizationDto authorization) =>
        authorization with { Targets = RedactTargets(authorization.Targets) };

    private IReadOnlyList<NoteProcessingTargetDto> RedactTargets(
        IReadOnlyList<NoteProcessingTargetDto> targets) =>
        string.Equals(user.Role, AdminBootstrap.AdminRole, StringComparison.Ordinal)
            ? targets
            : targets.Select(target => target with { Endpoint = null }).ToArray();

    private async Task<NoteProcessingChainDto> DescribeAsrAsync(Guid taskId,
        NoteAsrState? task, bool required, NoteMaterialVersionDto version,
        NoteProcessingRecord? processing, CancellationToken ct)
    {
        await Task.CompletedTask;
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

    private async Task<NoteProcessingChainDto> DescribeDerivedAsync(Guid taskId,
        NoteDerivedTaskState? task, bool hasAudio, NoteAsrState? asrTask,
        NoteMaterialVersionDto version, NoteProcessingRecord? processing,
        CancellationToken ct)
    {
        await Task.CompletedTask;
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
        // A stored pending explanation must never override chains that already exist.
        // Otherwise a later target/consent drift scan would report a running or completed
        // material as "not started".
        if (allRequiredCreated) return null;
        if (!string.IsNullOrWhiteSpace(state?.PendingReason))
            return state.PendingReason;
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

    private async Task<NoteProcessingPreferencesDto> PreferencesAsync(Guid owner,
        CancellationToken ct)
    {
        var config = await db.NoteProcessingPreferences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        var targetPolicy = await targetPolicies.ResolveAsync(owner, ct);
        return config is null
            ? new NoteProcessingPreferencesDto(Guid.Empty, false, null,
                NoteProcessingAuthorizationPurpose.NativeNoteProcessing,
                NoteProcessingAuthorizationPurpose.PurposeText,
                NoteProcessingAuthorizationPurpose.MaterialScope,
                NoteProcessingAuthorizationPurpose.MaterialScopeText,
                NoteProcessingAuthorizationRules.CostNotice, false, false,
                targetPolicy.Revision, null, targetPolicy.Descriptions)
            : NoteProcessingPreferencesService.Describe(config, targetPolicy);
    }

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
