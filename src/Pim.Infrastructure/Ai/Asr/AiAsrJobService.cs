using System.ClientModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using static Pim.Infrastructure.Ai.AiCallAudit;

namespace Pim.Infrastructure.Ai.Asr;

/// <summary>
/// Durable ASR facade used by the asynchronous endpoint and Hangfire worker. The database row is
/// the queue and the worker only receives owner + client id, so no audio or job intent lives in a
/// process-local queue.
/// </summary>
public sealed class AiAsrJobService(
    PimDbContext db,
    ISecretProtector secrets,
    ICurrentUserService currentUser,
    AiTextCallGate gate,
    AiAsrPresetRegistry presets,
    AiAsrMaterialService materials,
    IAsrJobDispatcher dispatcher,
    IServiceScopeFactory scopeFactory,
    ILogger<AiAsrJobService> logger)
{
    // Measured from the #552 reproduction: 2,318 completion tokens for 507.924 seconds of audio.
    private const double EstimatedCompletionTokensPerAudioSecond = 2318d / 507.924d;
    private static readonly TimeSpan PreparationLease = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiAsrJobResultDto> SubmitAsync(AiAsrCallRequest request, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        var descriptor = ResolveRequestDescriptor(request);
        var requestFingerprint = RequestFingerprint(request, descriptor);
        var old = await FindAsync(owner, request.ClientRequestId, ct);
        if (old is not null)
        {
            if (old.InputHash != requestFingerprint)
                throw new DomainException(40901, "clientRequestId 已用于不同的 ASR 请求");
            return await DescribeAsync(old, true, ct);
        }

        // Replays remain readable while background dispatch is unavailable. Only a new durable
        // intent requires a worker and may be rejected with a retryable 503.
        if (!dispatcher.IsAvailable)
            throw new DomainException(50302, "异步ASR后台任务未配置，未保存本次请求");

        var prepared = await PrepareAsync(owner, request, descriptor, requestFingerprint, ct);
        await AiAsrJobRecovery.RecoverExpiredAsync(db, ct);

        var now = DateTimeOffset.UtcNow;
        var log = new AiRequestLogEntity
        {
            UserId = owner,
            ClientRequestId = request.ClientRequestId,
            InputHash = prepared.Fingerprint,
            Module = "system",
            Purpose = "ai.asr-job",
            SourceObjectType = "ai-model",
            SourceObjectId = prepared.Profile.Id.ToString(),
            Provider = prepared.Description.Id,
            Model = prepared.Profile.Model,
            ModelProfileId = prepared.Profile.Id,
            ModelProfileName = prepared.Profile.Name,
            ModelRevision = prepared.Profile.Revision,
            ModelEndpoint = prepared.Operation,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = prepared.ConfigurationError is null ? "preparing" : "blocked",
            StartedAt = now,
            FinishedAt = prepared.ConfigurationError is null ? null : now,
            ExecutionExpiresAt = prepared.ConfigurationError is null ? now.Add(PreparationLease) : null,
            AttemptNumber = 0,
            MaxAttempts = 1,
            ErrorCode = prepared.ConfigurationCode,
            ErrorMessage = prepared.ConfigurationError
        };
        var audioRow = AiAsrMaterialService.Describe(log.Id, prepared.Audio);
        var job = new AiAsrJobEntity
        {
            OwnerId = owner,
            ClientRequestId = request.ClientRequestId,
            LogId = log.Id,
            InputHash = prepared.Fingerprint,
            Status = prepared.ConfigurationError is null ? "preparing" : "blocked",
            ModelProfileId = prepared.Profile.Id,
            ModelRevision = prepared.Profile.Revision,
            PresetId = prepared.Description.Id,
            PresetVersion = prepared.Description.Version,
            Model = prepared.Profile.Model,
            Endpoint = prepared.Operation,
            Language = request.Language,
            PromptText = prepared.Prompt.Text,
            PromptLanguage = prepared.Prompt.Language,
            PromptSha256 = prepared.Prompt.Sha256,
            PromptRevision = prepared.Prompt.Revision,
            PromptTemplateSha256 = prepared.Prompt.TemplateSha256,
            PromptTemplateRevision = prepared.Prompt.TemplateRevision,
            MaxOutputTokens = prepared.Profile.MaxOutputTokens,
            TimeoutSeconds = prepared.Profile.TimeoutSeconds,
            AudioName = prepared.Audio.Name,
            AudioFormat = prepared.Audio.Format,
            AudioSizeBytes = prepared.Audio.Bytes.Length,
            AudioSha256 = prepared.Audio.Sha256,
            ConsentJson = JsonSerializer.Serialize(request.Consent, Json),
            EstimatedAudioSeconds = prepared.EstimatedAudioSeconds,
            EstimatedCompletionTokens = prepared.EstimatedCompletionTokens,
            OutputLimitExceeded = prepared.OutputLimitExceeded,
            AbortSource = prepared.ConfigurationError is null ? "none" : "configuration",
            OutcomeKnown = prepared.ConfigurationError is not null,
            ErrorCode = prepared.ConfigurationCode,
            ErrorMessage = prepared.ConfigurationError,
            NextAction = prepared.ConfigurationError is null ? null
                : prepared.ConfigurationCode == "output_limit_preflight"
                    ? "提高输出 token 上限、改用 Batch 或缩短音频后明确新建请求"
                    : "修正或重新选择ASR模型配置后明确新建请求",
            CreatedAt = now,
            ExecutionExpiresAt = prepared.ConfigurationError is null ? now.Add(PreparationLease) : null,
            FinishedAt = prepared.ConfigurationError is null ? null : now
        };
        log.RequestMessagesJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                kind = "audio-input",
                reference = $"pim-ai-audio:{audioRow.Id:D}",
                format = prepared.Audio.Format,
                prepared.Audio.Sha256,
                sizeBytes = prepared.Audio.Bytes.Length,
                language = request.Language,
                jobTransport = "durable-async"
            }
        }, Json);
        log.RequestPayloadJson = JobIntentJson(prepared, request.Language);
        log.MetadataJson = JobMetadata(job, providerAttempted: false, responseBodyComplete: false,
            requestPayloadCaptured: false, responseTooLarge: false, reportedSeconds: null);
        db.AiRequestLogs.Add(log);
        db.AiCallAudio.Add(audioRow);
        db.AiAsrJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(log).State = EntityState.Detached;
            db.Entry(audioRow).State = EntityState.Detached;
            db.Entry(job).State = EntityState.Detached;
            var winner = await FindAsync(owner, request.ClientRequestId, ct);
            if (winner is null)
            {
                var requestLogExists = await db.AiRequestLogs.AsNoTracking().AnyAsync(x =>
                    x.UserId == owner && x.ClientRequestId == request.ClientRequestId, ct);
                if (requestLogExists)
                    throw new DomainException(40901, "clientRequestId 已用于不同的 AI 请求");
                throw;
            }
            if (winner.InputHash != prepared.Fingerprint)
                throw new DomainException(40901, "clientRequestId 已用于不同的 ASR 请求");
            return await DescribeAsync(winner, true, ct);
        }

        if (prepared.ConfigurationError is not null) return await DescribeAsync(job, false, ct);

        try
        {
            await materials.StoreForOwnerAsync(owner, audioRow, prepared.Audio, ct);
        }
        catch (Exception error) when (error is IOException or DbUpdateException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Durable ASR audio persistence failed for {ClientRequestId}",
                request.ClientRequestId);
            return await FinishTerminalAsync(job, "failed", "audio_prepare_failed",
                "音频未能保存，本次未发送模型，请检查文件来源", "audio_storage",
                outcomeKnown: true, "检查文件来源后明确新建请求", null, false);
        }

        if (!await MarkQueuedAsync(owner, request.ClientRequestId, log.Id, ct))
        {
            var current = await FindAsync(owner, request.ClientRequestId, ct)
                ?? throw new DomainException(40901, "异步ASR任务在准备期间已变化");
            return await DescribeAsync(current, true, ct);
        }

        try
        {
            dispatcher.Enqueue(owner, request.ClientRequestId);
        }
        catch (Exception error)
        {
            // The row is already durable. The minutely recovery scan will enqueue it again.
            logger.LogWarning(error, "Immediate ASR job enqueue failed for {ClientRequestId}",
                request.ClientRequestId);
        }
        return await DescribeAsync(job, false, ct);
    }

    public async Task<AiAsrJobResultDto> GetAsync(Guid clientRequestId, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        await AiAsrJobRecovery.RecoverExpiredAsync(db, ct);
        var job = await FindAsync(owner, clientRequestId, ct);
        if (job is null) throw new DomainException(40401, "尚未找到本次异步ASR任务");
        return await DescribeAsync(job, true, ct);
    }

    public async Task<AiAsrJobResultDto> CancelAsync(Guid clientRequestId, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        var now = DateTimeOffset.UtcNow;
        var job = await db.AiAsrJobs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OwnerId == owner && x.ClientRequestId == clientRequestId, ct)
            ?? throw new DomainException(40401, "尚未找到本次异步ASR任务");
        if (Terminal(job.Status)) return await DescribeAsync(job, true, ct);
        if (job.Status == "cancelling") return await DescribeAsync(job, true, ct);
        if (job.Status is "preparing" or "queued")
        {
            if (db.Database.IsRelational())
            {
                var won = false;
                await ExecuteAtomicAsync(async () =>
                {
                    var updated = await db.AiAsrJobs.Where(x => x.Id == job.Id &&
                            x.OwnerId == owner && x.ClientRequestId == clientRequestId &&
                            x.Status == job.Status)
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Status, "cancelled")
                            .SetProperty(x => x.AbortSource, "user")
                            .SetProperty(x => x.OutcomeKnown, true)
                            .SetProperty(x => x.ErrorCode, "user_cancelled_before_send")
                            .SetProperty(x => x.ErrorMessage, "任务在发送服务商前已由用户取消")
                            .SetProperty(x => x.NextAction, "如需转写，请明确新建请求")
                            .SetProperty(x => x.CancelRequestedAt, now)
                            .SetProperty(x => x.FinishedAt, now), ct);
                    won = updated == 1;
                    if (won)
                    {
                        var logUpdated = await db.AiRequestLogs.Where(x => x.Id == job.LogId &&
                                x.UserId == owner && x.Status == job.Status)
                            .ExecuteUpdateAsync(update => update
                                .SetProperty(x => x.Status, "cancelled")
                                .SetProperty(x => x.ErrorCode, "user_cancelled_before_send")
                                .SetProperty(x => x.ErrorMessage, "任务在发送服务商前已由用户取消")
                                .SetProperty(x => x.FinishedAt, now), ct);
                        if (logUpdated != 1)
                            throw new InvalidOperationException(
                                "ASR cancellation lost its audit row; refusing a split terminal state");
                    }
                }, ct);
                if (!won) return await CancelAsync(clientRequestId, ct);
            }
            else
            {
                var priorStatus = job.Status;
                var trackedJob = db.AiAsrJobs.Local.SingleOrDefault(x => x.Id == job.Id) ?? job;
                if (!ReferenceEquals(trackedJob, job))
                    db.Entry(trackedJob).CurrentValues.SetValues(job);
                trackedJob.Status = "cancelled";
                trackedJob.AbortSource = "user";
                trackedJob.OutcomeKnown = true;
                trackedJob.ErrorCode = "user_cancelled_before_send";
                trackedJob.ErrorMessage = "任务在发送服务商前已由用户取消";
                trackedJob.NextAction = "如需转写，请明确新建请求";
                trackedJob.CancelRequestedAt = now;
                trackedJob.FinishedAt = now;
                var log = await db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == job.LogId, ct);
                if (log is not null && log.Status == priorStatus)
                {
                    log.Status = "cancelled";
                    log.ErrorCode = "user_cancelled_before_send";
                    log.ErrorMessage = trackedJob.ErrorMessage;
                    log.FinishedAt = now;
                }
                if (db.Entry(trackedJob).State == EntityState.Detached)
                    db.Entry(trackedJob).State = EntityState.Modified;
                await db.SaveChangesAsync(ct);
            }
            return await GetForOwnerAsync(owner, clientRequestId, ct);
        }
        if (job.Status == "running")
        {
            if (db.Database.IsRelational())
            {
                await ExecuteAtomicAsync(async () =>
                {
                    var updated = await db.AiAsrJobs.Where(x => x.Id == job.Id &&
                            x.OwnerId == owner && x.Status == "running")
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Status, "cancelling")
                            .SetProperty(x => x.CancelRequestedAt, now)
                            .SetProperty(x => x.ErrorCode, "user_cancel_requested")
                            .SetProperty(x => x.ErrorMessage, "已请求取消；正在通知执行进程")
                            .SetProperty(x => x.NextAction, "等待状态变为已取消；不要重复提交"), ct);
                    if (updated != 1) return;
                    var logUpdated = await db.AiRequestLogs.Where(x => x.Id == job.LogId &&
                            x.UserId == owner && x.Status == "running")
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Status, "cancelling")
                            .SetProperty(x => x.ErrorCode, "user_cancel_requested")
                            .SetProperty(x => x.ErrorMessage, "已请求取消；正在通知执行进程"), ct);
                    if (logUpdated != 1)
                        throw new InvalidOperationException(
                            "ASR cancellation lost its running audit row; refusing a split state");
                }, ct);
            }
            else
            {
                var log = await db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == job.LogId &&
                    x.UserId == owner && x.Status == "running", ct);
                if (log is null) return await GetForOwnerAsync(owner, clientRequestId, ct);
                var trackedJob = db.AiAsrJobs.Local.SingleOrDefault(x => x.Id == job.Id) ?? job;
                if (!ReferenceEquals(trackedJob, job))
                    db.Entry(trackedJob).CurrentValues.SetValues(job);
                trackedJob.Status = "cancelling";
                trackedJob.CancelRequestedAt = now;
                trackedJob.ErrorCode = "user_cancel_requested";
                trackedJob.ErrorMessage = "已请求取消；正在通知执行进程";
                trackedJob.NextAction = "等待状态变为已取消；不要重复提交";
                log.Status = "cancelling";
                log.ErrorCode = trackedJob.ErrorCode;
                log.ErrorMessage = trackedJob.ErrorMessage;
                if (db.Entry(trackedJob).State == EntityState.Detached)
                    db.Entry(trackedJob).State = EntityState.Modified;
                await db.SaveChangesAsync(ct);
            }
            return await GetForOwnerAsync(owner, clientRequestId, ct);
        }
        return await DescribeAsync(job, true, ct);
    }

    public async Task ProcessAsync(Guid owner, Guid clientRequestId, CancellationToken ct)
    {
        var job = await FindAsync(owner, clientRequestId, ct);
        if (job is null || job.Status != "queued") return;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
        {
            await ClaimAndFailAsync(owner, clientRequestId, "blocked", "account_inactive",
                "发起账号已停用，ASR未发送", "configuration", true,
                "启用账号后明确新建请求", ct);
            return;
        }

        var profile = await db.AiModelProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == job.ModelProfileId, ct);
        var runtime = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (profile is null || profile.Revision != job.ModelRevision || !profile.Enabled ||
            runtime?.Enabled != true || (profile.Capabilities & AiModelCapabilityFlags.Transcription) == 0)
        {
            await ClaimAndFailAsync(owner, clientRequestId, "blocked", "configuration_changed",
                "ASR模型配置在排队后已变化，未发送服务商", "configuration", true,
                "刷新配置后明确新建请求", ct);
            return;
        }
        var preset = presets.Resolve(job.PresetId);
        var description = preset.Descriptor;
        var endpoint = preset.NormalizeEndpoint(profile.BaseUrl);
        if (AiAsrPresetRegistry.OperationEndpoint(endpoint, description) != job.Endpoint ||
            description.Version != job.PresetVersion || profile.Model != job.Model)
        {
            await ClaimAndFailAsync(owner, clientRequestId, "blocked", "consent_target_changed",
                "ASR目标地址或预设已变化，未发送服务商", "configuration", true,
                "刷新并重新确认目标后明确新建请求", ct);
            return;
        }

        if (!gate.TryEnter(asr: true)) return;
        var claimed = false;
        try
        {
            var now = DateTimeOffset.UtcNow;
            claimed = await TryClaimAsync(owner, clientRequestId, now,
                now.AddSeconds(job.TimeoutSeconds + 120), ct);
            if (!claimed) return;

            var log = await db.AiRequestLogs.AsNoTracking().SingleAsync(x =>
                x.Id == job.LogId && x.UserId == owner, ct);
            var audioRow = await db.AiCallAudio.AsNoTracking().SingleAsync(x => x.LogId == job.LogId, ct);
            var (bytes, _) = await materials.ReadForOwnerAsync(owner, job.LogId, ct);
            if (bytes.Length != job.AudioSizeBytes ||
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != job.AudioSha256 ||
                audioRow.Format != job.AudioFormat)
            {
                await CompleteAsync(job, log, "blocked", "audio_changed",
                    "原始音频已改变，未发送服务商", "audio_storage", true,
                    "重新选择原始音频后明确新建请求", null, false, false, false, null,
                    CancellationToken.None);
                return;
            }

            string key;
            try
            {
                key = secrets.Unprotect(profile.ApiKeySecret);
                if (string.IsNullOrEmpty(key)) throw new CryptographicException("empty credential");
            }
            catch (Exception error) when (error is CryptographicException or FormatException)
            {
                await CompleteAsync(job, log, "blocked", "credential_unavailable",
                    "ASR凭据不可用，未发送服务商", "configuration", true,
                    "重新保存凭据后明确新建请求", null, false, false, false, null,
                    CancellationToken.None);
                return;
            }

            var audio = new AiAsrInput(job.AudioName, job.AudioFormat, bytes, job.AudioSha256);
            var prompt = new AiAsrPromptSnapshot(job.PromptText, job.PromptLanguage, job.PromptSha256,
                job.PromptRevision, job.PromptTemplateSha256, job.PromptTemplateRevision);
            var connection = new AiModelConnection(job.Endpoint, job.Model, key, job.TimeoutSeconds,
                job.MaxOutputTokens);
            using var wire = new AiTextWireCapture();
            wire.SetAsrAudit(body => preset.AuditRequest(body, connection, audio, audioRow.Id,
                    job.Language, prompt), RequestBudgetBytes(description.MaxAudioBytes));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds + 60));
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var monitorStop = new CancellationTokenSource();
            var monitor = MonitorCancellationAsync(owner, clientRequestId, execution, monitorStop.Token);
            string? parsedResponse = null;
            double? reportedSeconds = null;
            var providerPhase = false;
            try
            {
                providerPhase = true;
                var response = await preset.TranscribeAsync(connection, audio, job.Language,
                    execution.Token, prompt);
                job.ProviderAttempted = wire.ProviderAttempted || providerPhase;
                log.ResponseText = Safe(response.Text, key, out var redacted);
                parsedResponse = SafeJson(ValidJson(response.RawJson), key, out var rawRedacted);
                log.ResponseRawJson = parsedResponse;
                log.ContentRedacted = redacted || rawRedacted;
                log.FinishReason = response.FinishReason;
                log.PromptTokens = Count(response.InputTokens);
                log.CompletionTokens = Count(response.OutputTokens);
                log.TotalTokens = Count(response.TotalTokens);
                reportedSeconds = response.ReportedSeconds;
                var outputLimited = response.FinishReason is
                    MimoMultimodalResponseParser.TruncationFinishReason or "length" or "max_tokens";
                var succeeded = response.FinishReason == "stop" && response.HasTranscript;
                var status = succeeded ? "succeeded" : "incomplete";
                var code = succeeded ? null : outputLimited ? "output_limit_reached" : "incomplete_output";
                var message = succeeded ? null
                    : outputLimited
                        ? "ASR输出达到配置上限，转写可能被截断；请提高输出 token 上限、分段或改走 Batch"
                        : "ASR未正常完成或缺少转写字段，请核对回复与结束原因";
                var next = succeeded ? null
                    : outputLimited ? "提高输出 token 上限、分段或改走 Batch 后明确新建请求"
                    : "核对服务商与调用记录后明确新建请求";
                await CompleteAsync(job, log, status, code, message,
                    succeeded ? "none" : outputLimited ? "output_limit" : "provider_incomplete",
                    succeeded || outputLimited, next, wire.ResponseBody,
                    wire.ResponseTooLarge, wire.ProviderAttempted,
                    wire.ResponseBody is not null, reportedSeconds, CancellationToken.None);
                return;
            }
            catch (OperationCanceledException)
            {
                var current = await db.AiAsrJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id,
                    CancellationToken.None);
                if (current.Status == "cancelling")
                {
                    await CompleteAsync(job, log, "cancelled", "user_cancelled_after_send",
                        "用户已取消；若服务商已开始处理，结果可能仍被计费，必须核对调用记录",
                        "user_cancelled", false,
                        "保留原请求标识并核对服务商记录；不要自动重发",
                        wire.ResponseBody, wire.ResponseTooLarge, wire.ProviderAttempted,
                        wire.ResponseBody is not null, reportedSeconds, CancellationToken.None);
                }
                else if (deadline.IsCancellationRequested)
                {
                    await CompleteAsync(job, log, "timed_out", "worker_timeout_outcome_unknown",
                        "ASR后台执行超时，服务商结果未确认", "worker_timeout", false,
                        "核对服务商记录或改用 Batch；不要自动重发", wire.ResponseBody,
                        wire.ResponseTooLarge, wire.ProviderAttempted, wire.ResponseBody is not null,
                        reportedSeconds, CancellationToken.None);
                }
                else
                {
                    await CompleteAsync(job, log, "incomplete", "worker_shutdown_outcome_unknown",
                        "执行进程中断，服务商结果未确认", "worker_shutdown", false,
                        "重启后核对调用记录；任务不会自动重发", wire.ResponseBody,
                        wire.ResponseTooLarge, wire.ProviderAttempted, wire.ResponseBody is not null,
                        reportedSeconds, CancellationToken.None);
                }
                return;
            }
            catch (AiAsrProviderHttpException error)
            {
                if (IsTimeoutStatus(error.StatusCode))
                {
                    await CompleteAsync(job, log, "timed_out", "provider_timeout_outcome_unknown",
                        "ASR服务商请求超时未返回完整结果，结果未确认",
                        "provider_timeout", false,
                        "确认ASR模型超时设置，或改用 Batch/更短音频；不要自动重发",
                        wire.ResponseBody, wire.ResponseTooLarge, wire.ProviderAttempted,
                        wire.ResponseBody is not null, reportedSeconds, CancellationToken.None,
                        upstreamStatus: error.StatusCode);
                    return;
                }
                var known = error.StatusCode is >= 400 and < 500 and not 408 and not 429;
                await CompleteAsync(job, log, "failed", "upstream_http_" + error.StatusCode,
                    "ASR服务返回HTTP " + error.StatusCode + "，本次未自动重试",
                    "upstream_http", known,
                    UpstreamNextAction(error.StatusCode), wire.ResponseBody,
                    wire.ResponseTooLarge, wire.ProviderAttempted, wire.ResponseBody is not null,
                    reportedSeconds, CancellationToken.None, upstreamStatus: error.StatusCode);
                return;
            }
            catch (ClientResultException error)
            {
                if (IsTimeoutStatus(error.Status))
                {
                    await CompleteAsync(job, log, "timed_out", "provider_timeout_outcome_unknown",
                        "ASR服务商请求超时未返回完整结果，结果未确认",
                        "provider_timeout", false,
                        "确认ASR模型超时设置，或改用 Batch/更短音频；不要自动重发",
                        wire.ResponseBody, wire.ResponseTooLarge, wire.ProviderAttempted,
                        wire.ResponseBody is not null, reportedSeconds, CancellationToken.None,
                        upstreamStatus: error.Status);
                    return;
                }
                await CompleteAsync(job, log, "failed", "upstream_http_" + error.Status,
                    "ASR服务返回HTTP " + error.Status + "，本次未自动重试", "upstream_http",
                    error.Status is >= 400 and < 500, UpstreamNextAction(error.Status),
                    wire.ResponseBody, wire.ResponseTooLarge, wire.ProviderAttempted,
                    wire.ResponseBody is not null, reportedSeconds, CancellationToken.None,
                    upstreamStatus: error.Status);
                return;
            }
            catch (DomainException error)
            {
                if (error.ErrorCode == 50401)
                {
                    await CompleteAsync(job, log, "timed_out", "provider_timeout_outcome_unknown",
                        "ASR服务商请求超时，结果未确认", "provider_timeout", false,
                        "确认ASR模型超时设置，或改用 Batch/更短音频；不要自动重发",
                        wire.ResponseBody, wire.ResponseTooLarge, wire.ProviderAttempted,
                        wire.ResponseBody is not null, reportedSeconds, CancellationToken.None,
                        upstreamStatus: 504);
                }
                else
                {
                    var known = !wire.ProviderAttempted;
                    await CompleteAsync(job, log, "failed", "provider_" + error.ErrorCode,
                        Clip(error.Message, 2000), "provider_error", known,
                        known ? "修正请求或配置后明确新建请求" : "核对服务商记录后明确新建请求",
                        wire.ResponseBody, wire.ResponseTooLarge, wire.ProviderAttempted,
                        wire.ResponseBody is not null, reportedSeconds, CancellationToken.None);
                }
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Durable ASR provider call failed for {ClientRequestId}",
                    clientRequestId);
                await CompleteAsync(job, log, "failed", "provider_error",
                    wire.RequestRejected
                        ? "SDK请求与固定音频/模型/语言不一致，本次未发送"
                        : "ASR连接或响应异常，服务商结果未确认",
                    wire.RequestRejected ? "request_rejected" : "provider_error",
                    wire.RequestRejected, "核对调用记录后明确新建请求", wire.ResponseBody,
                    wire.ResponseTooLarge, wire.ProviderAttempted, wire.ResponseBody is not null,
                    reportedSeconds, CancellationToken.None);
                return;
            }
            finally
            {
                monitorStop.Cancel();
                try { await monitor; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            gate.Exit(asr: true);
        }

    }

    private AiAsrPresetDto ResolveRequestDescriptor(AiAsrCallRequest request)
    {
        if (request.ExpectedUserId != currentUser.UserId)
            throw new DomainException(40301, "会话已改变，音频未发送，请重新核对账号");
        if (request.ClientRequestId == Guid.Empty || request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请核对本次音频、ASR预设、模型和目标地址，并明确同意发送");
        var preset = presets.Resolve(request.Consent.PresetId);
        return preset.Descriptor;
    }

    private async Task<PreparedJob> PrepareAsync(Guid owner, AiAsrCallRequest request,
        AiAsrPresetDto descriptor, string fingerprint, CancellationToken ct)
    {
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "会话已改变，音频未发送，请重新核对账号");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "发起账号已停用，ASR未发送");
        if (request.ClientRequestId == Guid.Empty || request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请核对本次音频、ASR预设、模型和目标地址，并明确同意发送");
        var runtime = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var modelId = request.ModelId ?? runtime?.TranscriptionModelId;
        var profile = modelId is { } id
            ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            : null;
        if (profile is null) throw new DomainException(40001, "请选择带有预设的ASR模型");
        var preset = presets.Resolve(profile.AsrPresetId);
        var description = preset.Descriptor;
        if (description.Id != descriptor.Id || description.Version != descriptor.Version)
            throw new DomainException(40901, "ASR预设版本已变化，请刷新并重新核对");
        preset.ValidateModel(profile.Model);
        var endpoint = preset.NormalizeEndpoint(profile.BaseUrl);
        var operation = AiAsrPresetRegistry.OperationEndpoint(endpoint, description);
        if (request.Consent.ModelId != profile.Id ||
            request.Consent.ModelRevision != profile.Revision ||
            request.Consent.PresetId != description.Id ||
            request.Consent.PresetVersion != description.Version ||
            request.Consent.Endpoint != operation)
            throw new DomainException(40901, "ASR预设、模型或目标地址已变化，音频未发送，请刷新并重新核对");
        if (request.Language is null || !description.Languages.Contains(request.Language))
            throw new DomainException(40001, "语言不符合所选ASR预设");
        var expectedPrompt = AiAsrPromptTemplate.Normalize(profile.AsrPrompt).Snapshot(request.Language);
        var prompt = request.Prompt ?? expectedPrompt;
        if (prompt.Text != expectedPrompt.Text || prompt.Language != expectedPrompt.Language ||
            prompt.Sha256 != expectedPrompt.Sha256 || prompt.Revision != expectedPrompt.Revision ||
            prompt.TemplateSha256 != expectedPrompt.TemplateSha256 ||
            prompt.TemplateRevision != expectedPrompt.TemplateRevision)
            throw new DomainException(40901, "ASR提示词修订已变化，音频未发送，请刷新后重新核对");
        var audio = AiAsrMaterialService.Decode(request.Audio, description.Formats, description.MaxAudioBytes);
        string? configurationError = null;
        string? configurationCode = null;
        if (runtime?.Enabled != true || !profile.Enabled ||
            (profile.Capabilities & AiModelCapabilityFlags.Transcription) == 0)
        {
            configurationError = "SDK未启用，或该配置不是已启用的ASR模型";
            configurationCode = "configuration_unavailable";
        }
        var seconds = description.Id == MimoMultimodalPreset.PresetId
            ? MimoMultimodalResponseParser.EstimateAudioSeconds(audio) : null;
        var estimatedTokens = seconds is { } value
            ? checked((int)Math.Ceiling(value * EstimatedCompletionTokensPerAudioSecond)) : (int?)null;
        var exceeded = description.Id == MimoMultimodalPreset.PresetId &&
            estimatedTokens > profile.MaxOutputTokens;
        if (configurationError is null && exceeded)
        {
            configurationCode = "output_limit_preflight";
            configurationError =
                $"按音频时长估算约需 {estimatedTokens} 输出 token，超过当前配置 {profile.MaxOutputTokens}；请提高上限或改用 Batch，本次未发送服务商";
        }
        return new(profile, description, operation, audio, prompt, fingerprint,
            configurationError, configurationCode, seconds, estimatedTokens, exceeded);
    }

    private string RequestFingerprint(AiAsrCallRequest request, AiAsrPresetDto descriptor)
    {
        if (request.Language is null || !descriptor.Languages.Contains(request.Language))
            throw new DomainException(40001, "语言不符合所选ASR预设");
        var audio = AiAsrMaterialService.Decode(request.Audio, descriptor.Formats, descriptor.MaxAudioBytes);
        return Hash(JsonSerializer.Serialize(new
        {
            kind = "asr-job-v1",
            request.ExpectedUserId,
            request.ModelId,
            request.Language,
            request.Consent,
            request.Prompt,
            audio.Name,
            audio.Format,
            audio.Sha256,
            size = audio.Bytes.Length
        }, Json));
    }

    private async Task<bool> TryClaimAsync(Guid owner, Guid clientRequestId, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var won = false;
            await ExecuteAtomicAsync(async () =>
            {
                var updated = await db.AiAsrJobs.Where(x => x.OwnerId == owner &&
                        x.ClientRequestId == clientRequestId && x.Status == "queued")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, "running")
                        .SetProperty(x => x.StartedAt, now)
                        .SetProperty(x => x.ExecutionExpiresAt, expiresAt)
                        .SetProperty(x => x.AttemptNumber, x => x.AttemptNumber + 1)
                        .SetProperty(x => x.ErrorCode, (string?)null)
                        .SetProperty(x => x.ErrorMessage, (string?)null), ct);
                won = updated == 1;
                if (won)
                {
                    var logUpdated = await db.AiRequestLogs.Where(x => x.ClientRequestId == clientRequestId &&
                            x.UserId == owner && x.Purpose == "ai.asr-job" && x.Status == "queued")
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Status, "running")
                            .SetProperty(x => x.StartedAt, now)
                            .SetProperty(x => x.ExecutionExpiresAt, expiresAt)
                            .SetProperty(x => x.AttemptNumber, x => x.AttemptNumber + 1), ct);
                    if (logUpdated != 1)
                        throw new InvalidOperationException(
                            "ASR claim lost its queued audit row; refusing a split running state");
                }
            }, ct);
            return won;
        }
        var job = await db.AiAsrJobs.SingleOrDefaultAsync(x =>
            x.OwnerId == owner && x.ClientRequestId == clientRequestId, ct);
        if (job is null || job.Status != "queued") return false;
        var log = await db.AiRequestLogs.SingleAsync(x => x.ClientRequestId == clientRequestId &&
            x.UserId == owner && x.Purpose == "ai.asr-job", ct);
        if (log.Status != "queued") return false;
        job.Status = "running";
        job.StartedAt = now;
        job.ExecutionExpiresAt = expiresAt;
        job.AttemptNumber++;
        job.ErrorCode = null;
        job.ErrorMessage = null;
        log.Status = "running";
        log.StartedAt = now;
        log.ExecutionExpiresAt = expiresAt;
        log.AttemptNumber++;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> MarkQueuedAsync(Guid owner, Guid clientRequestId, Guid logId,
        CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var updated = 0;
            await ExecuteAtomicAsync(async () =>
            {
                updated = await db.AiAsrJobs.Where(x => x.OwnerId == owner &&
                        x.ClientRequestId == clientRequestId && x.LogId == logId &&
                        x.Status == "preparing")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, "queued")
                        .SetProperty(x => x.ExecutionExpiresAt, (DateTimeOffset?)null), ct);
                if (updated == 1)
                {
                    var logUpdated = await db.AiRequestLogs.Where(x => x.Id == logId && x.UserId == owner &&
                            x.Status == "preparing")
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Status, "queued")
                            .SetProperty(x => x.ExecutionExpiresAt, (DateTimeOffset?)null), ct);
                    if (logUpdated != 1)
                        throw new InvalidOperationException(
                            "ASR job queue transition lost its preparation audit row");
                }
            }, ct);
            return updated == 1;
        }

        var job = await db.AiAsrJobs.SingleOrDefaultAsync(x => x.OwnerId == owner &&
            x.ClientRequestId == clientRequestId && x.LogId == logId, ct);
        if (job is null || job.Status != "preparing") return false;
        job.Status = "queued";
        job.ExecutionExpiresAt = null;
        var log = await db.AiRequestLogs.SingleAsync(x => x.Id == logId && x.UserId == owner &&
            x.Status == "preparing", ct);
        log.Status = "queued";
        log.ExecutionExpiresAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private Task ExecuteAtomicAsync(Func<Task> operation, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await operation();
            await transaction.CommitAsync(ct);
        });

    private async Task ClaimAndFailAsync(Guid owner, Guid clientRequestId, string status,
        string code, string message, string abortSource, bool outcomeKnown, string nextAction,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            var job = await db.AiAsrJobs.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OwnerId == owner && x.ClientRequestId == clientRequestId, ct);
            if (job is null || job.Status != "queued") return;
            var won = false;
            await ExecuteAtomicAsync(async () =>
            {
                var updated = await db.AiAsrJobs.Where(x => x.Id == job.Id && x.Status == "queued")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, status)
                        .SetProperty(x => x.AbortSource, abortSource)
                        .SetProperty(x => x.OutcomeKnown, outcomeKnown)
                        .SetProperty(x => x.ErrorCode, code)
                        .SetProperty(x => x.ErrorMessage, message)
                        .SetProperty(x => x.NextAction, nextAction)
                        .SetProperty(x => x.FinishedAt, now)
                        .SetProperty(x => x.ExecutionExpiresAt, (DateTimeOffset?)null), ct);
                won = updated == 1;
                if (won)
                {
                    var logUpdated = await db.AiRequestLogs.Where(x => x.Id == job.LogId && x.Status == "queued")
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Status, status)
                            .SetProperty(x => x.ErrorCode, code)
                            .SetProperty(x => x.ErrorMessage, message)
                            .SetProperty(x => x.FinishedAt, now)
                            .SetProperty(x => x.DurationMs,
                                (long)(now - (job.StartedAt ?? job.CreatedAt)).TotalMilliseconds), ct);
                    if (logUpdated != 1)
                        throw new InvalidOperationException(
                            "ASR terminal transition lost its queued audit row");
                }
            }, ct);
            if (won) return;
            await ProcessAsync(owner, clientRequestId, ct);
            return;
        }
        var trackedJob = await db.AiAsrJobs.SingleOrDefaultAsync(x =>
            x.OwnerId == owner && x.ClientRequestId == clientRequestId, ct);
        if (trackedJob is null || trackedJob.Status != "queued") return;
        var log = await db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == trackedJob.LogId, ct);
        if (log is null || log.Status != "queued") return;
        trackedJob.Status = status;
        trackedJob.AbortSource = abortSource;
        trackedJob.OutcomeKnown = outcomeKnown;
        trackedJob.ErrorCode = code;
        trackedJob.ErrorMessage = message;
        trackedJob.NextAction = nextAction;
        trackedJob.FinishedAt = now;
        log.Status = status;
        log.ErrorCode = code;
        log.ErrorMessage = message;
        log.FinishedAt = now;
        log.DurationMs = (long)(now - log.StartedAt).TotalMilliseconds;
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteAsync(AiAsrJobEntity job, AiRequestLogEntity log, string status,
        string? errorCode, string? errorMessage, string abortSource, bool outcomeKnown,
        string? nextAction, string? responseBody, bool responseTooLarge,
        bool providerAttempted, bool responseBodyComplete, double? reportedSeconds,
        CancellationToken ct, int? upstreamStatus = null)
    {
        var now = DateTimeOffset.UtcNow;
        void Prepare(AiAsrJobEntity current)
        {
            job.GatewayStatus = current.GatewayStatus ?? job.GatewayStatus;
            job.GatewayName = current.GatewayName ?? job.GatewayName;
            job.GatewayEvidenceSource = current.GatewayEvidenceSource ?? job.GatewayEvidenceSource;
            job.GatewayObservedAt = current.GatewayObservedAt ?? job.GatewayObservedAt;
            var cancelArrivedLate = current.Status == "cancelling" && status == "succeeded";
            job.Status = status;
            job.AbortSource = cancelArrivedLate ? "user_cancel_late" : abortSource;
            job.OutcomeKnown = outcomeKnown;
            job.ProviderAttempted = providerAttempted;
            job.ResponseBodyComplete = responseBodyComplete && !responseTooLarge;
            job.UpstreamStatus = upstreamStatus;
            job.ErrorCode = errorCode;
            job.ErrorMessage = Clip(errorMessage, 2000);
            job.NextAction = cancelArrivedLate
                ? "取消请求到达时服务商已完成；结果已保留，请核对后使用"
                : nextAction;
            job.FinishedAt = now;
            job.ExecutionExpiresAt = null;
            log.Status = status;
            log.ErrorCode = errorCode;
            log.ErrorMessage = job.ErrorMessage;
            log.FinishedAt = now;
            log.DurationMs = (long)(now - log.StartedAt).TotalMilliseconds;
            log.OutputChars = log.ResponseText?.Length ?? 0;
            log.OutputHash = Hash(log.ResponseText ?? log.ResponseRawJson);
            if (responseBody is not null)
            {
                var wireJson = SafeJson(ValidJson(responseBody), null, out _);
                log.ResponseRawJson = AiAsrCallService.ComposeResponseAudit(
                    log.ResponseText is null ? null : log.ResponseRawJson, wireJson);
            }
            log.MetadataJson = JobMetadata(job, providerAttempted, log.ResponseRawJson is not "{}",
                log.RequestPayloadJson is not "{}", responseTooLarge, reportedSeconds);
        }

        if (db.Database.IsNpgsql())
        {
            await ExecuteAtomicAsync(async () =>
            {
                // Lock the queue row before deciding the terminal audit state. This closes the
                // read-then-update window with CancelAsync/RecoverExpiredAsync and makes a late
                // successful provider response retain its result with user_cancel_late evidence.
                var currentJob = await db.AiAsrJobs.FromSqlInterpolated($"""
                    SELECT * FROM ai_asr_jobs
                    WHERE id = {job.Id} AND owner_id = {job.OwnerId}
                    FOR UPDATE
                    """).AsNoTracking().SingleOrDefaultAsync(ct);
                if (currentJob is null || Terminal(currentJob.Status) ||
                    currentJob.Status is not ("running" or "cancelling"))
                    return;
                Prepare(currentJob);
                var updated = await db.AiAsrJobs.Where(x => x.Id == job.Id &&
                        x.OwnerId == job.OwnerId &&
                        x.Status == currentJob.Status)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, job.Status)
                        .SetProperty(x => x.AbortSource, job.AbortSource)
                        .SetProperty(x => x.OutcomeKnown, job.OutcomeKnown)
                        .SetProperty(x => x.ProviderAttempted, job.ProviderAttempted)
                        .SetProperty(x => x.ResponseBodyComplete, job.ResponseBodyComplete)
                        .SetProperty(x => x.UpstreamStatus, job.UpstreamStatus)
                        .SetProperty(x => x.ErrorCode, job.ErrorCode)
                        .SetProperty(x => x.ErrorMessage, job.ErrorMessage)
                        .SetProperty(x => x.NextAction, job.NextAction)
                        .SetProperty(x => x.FinishedAt, job.FinishedAt)
                        .SetProperty(x => x.ExecutionExpiresAt, (DateTimeOffset?)null), ct);
                if (updated == 0) return;
                var logUpdated = await db.AiRequestLogs.Where(x => x.Id == log.Id &&
                        x.UserId == job.OwnerId &&
                        x.Status == currentJob.Status)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, log.Status)
                        .SetProperty(x => x.ResponseText, log.ResponseText)
                        .SetProperty(x => x.ResponseRawJson, log.ResponseRawJson)
                        .SetProperty(x => x.ContentRedacted, log.ContentRedacted)
                        .SetProperty(x => x.FinishReason, log.FinishReason)
                        .SetProperty(x => x.PromptTokens, log.PromptTokens)
                        .SetProperty(x => x.CompletionTokens, log.CompletionTokens)
                        .SetProperty(x => x.TotalTokens, log.TotalTokens)
                        .SetProperty(x => x.ErrorCode, log.ErrorCode)
                        .SetProperty(x => x.ErrorMessage, log.ErrorMessage)
                        .SetProperty(x => x.FinishedAt, log.FinishedAt)
                        .SetProperty(x => x.DurationMs, log.DurationMs)
                        .SetProperty(x => x.OutputChars, log.OutputChars)
                        .SetProperty(x => x.OutputHash, log.OutputHash)
                        .SetProperty(x => x.MetadataJson, log.MetadataJson), ct);
                if (logUpdated != 1)
                    throw new InvalidOperationException(
                        "ASR job completion lost its running audit row; refusing a split terminal state");
            }, ct);
            return;
        }
        var currentJob = await db.AiAsrJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == job.Id, ct);
        if (currentJob is null || Terminal(currentJob.Status) ||
            currentJob.Status is not ("running" or "cancelling"))
            return;
        Prepare(currentJob);
        var trackedJob = db.AiAsrJobs.Local.FirstOrDefault(x => x.Id == job.Id) ?? job;
        var trackedLog = db.AiRequestLogs.Local.FirstOrDefault(x => x.Id == log.Id) ?? log;
        if (!ReferenceEquals(trackedJob, job))
            db.Entry(trackedJob).CurrentValues.SetValues(job);
        else
            db.Entry(job).State = EntityState.Modified;
        if (!ReferenceEquals(trackedLog, log))
            db.Entry(trackedLog).CurrentValues.SetValues(log);
        else
            db.Entry(log).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    private async Task<AiAsrJobResultDto> FinishTerminalAsync(AiAsrJobEntity job, string status,
        string code, string message, string abortSource, bool outcomeKnown, string nextAction,
        int? upstreamStatus, bool providerAttempted)
    {
        var now = DateTimeOffset.UtcNow;
        var current = await db.AiAsrJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        if (Terminal(current.Status)) return await DescribeAsync(current, true, CancellationToken.None);
        if (current.Status != "preparing")
            return await DescribeAsync(current, true, CancellationToken.None);
        job.Status = status; job.ErrorCode = code; job.ErrorMessage = message;
        job.AbortSource = abortSource; job.OutcomeKnown = outcomeKnown;
        job.NextAction = nextAction; job.FinishedAt = now; job.UpstreamStatus = upstreamStatus;
        job.ProviderAttempted = providerAttempted;
        job.ExecutionExpiresAt = null;
        job.ResponseBodyComplete = false;
        var log = await db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == job.LogId);
        log.Status = status; log.ErrorCode = code; log.ErrorMessage = message;
        log.FinishedAt = now; log.DurationMs = (long)(now - log.StartedAt).TotalMilliseconds;
        log.MetadataJson = JobMetadata(job, providerAttempted, false, false, false, null);
        if (db.Database.IsRelational())
        {
            await ExecuteAtomicAsync(async () =>
            {
                var updated = await db.AiAsrJobs.Where(x => x.Id == job.Id &&
                        x.OwnerId == job.OwnerId && x.Status == "preparing")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, job.Status)
                        .SetProperty(x => x.AbortSource, job.AbortSource)
                        .SetProperty(x => x.OutcomeKnown, job.OutcomeKnown)
                        .SetProperty(x => x.ProviderAttempted, job.ProviderAttempted)
                        .SetProperty(x => x.ResponseBodyComplete, job.ResponseBodyComplete)
                        .SetProperty(x => x.UpstreamStatus, job.UpstreamStatus)
                        .SetProperty(x => x.ErrorCode, job.ErrorCode)
                        .SetProperty(x => x.ErrorMessage, job.ErrorMessage)
                        .SetProperty(x => x.NextAction, job.NextAction)
                        .SetProperty(x => x.FinishedAt, job.FinishedAt)
                        .SetProperty(x => x.ExecutionExpiresAt, (DateTimeOffset?)null), CancellationToken.None);
                if (updated == 0) return;
                var logUpdated = await db.AiRequestLogs.Where(x => x.Id == log.Id &&
                        x.UserId == job.OwnerId && x.Status == "preparing")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, log.Status)
                        .SetProperty(x => x.ErrorCode, log.ErrorCode)
                        .SetProperty(x => x.ErrorMessage, log.ErrorMessage)
                        .SetProperty(x => x.FinishedAt, log.FinishedAt)
                        .SetProperty(x => x.DurationMs, log.DurationMs)
                        .SetProperty(x => x.MetadataJson, log.MetadataJson), CancellationToken.None);
                if (logUpdated != 1)
                    throw new InvalidOperationException(
                        "ASR preparation completion lost its audit row; refusing a split terminal state");
            }, CancellationToken.None);
        }
        else
        {
            if (db.Entry(job).State == EntityState.Detached) db.AiAsrJobs.Attach(job);
            db.Entry(job).State = EntityState.Modified;
            if (db.Entry(log).State == EntityState.Detached) db.AiRequestLogs.Attach(log);
            db.Entry(log).State = EntityState.Modified;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return await DescribeAsync(job, false, CancellationToken.None);
    }

    private async Task MonitorCancellationAsync(Guid owner, Guid clientRequestId,
        CancellationTokenSource execution, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(750, stop);
            await using var scope = scopeFactory.CreateAsyncScope();
            var monitorDb = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var status = await monitorDb.AiAsrJobs.AsNoTracking()
                .Where(x => x.OwnerId == owner && x.ClientRequestId == clientRequestId)
                .Select(x => x.Status).SingleOrDefaultAsync(stop);
            if (status == "cancelling")
            {
                execution.Cancel();
                return;
            }
            if (status is null || Terminal(status)) return;
        }
    }

    private Task<AiAsrJobEntity?> FindAsync(Guid owner, Guid id, CancellationToken ct) =>
        db.AiAsrJobs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OwnerId == owner && x.ClientRequestId == id, ct);

    private async Task<AiAsrJobResultDto> GetForOwnerAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var job = await FindAsync(owner, id, ct)
            ?? throw new DomainException(40401, "尚未找到本次异步ASR任务");
        return await DescribeAsync(job, true, ct);
    }

    private async Task<AiAsrJobResultDto> DescribeAsync(AiAsrJobEntity job, bool replayed,
        CancellationToken ct)
    {
        var response = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.Id == job.LogId && x.UserId == job.OwnerId)
            .Select(x => x.ResponseText).SingleOrDefaultAsync(ct);
        var observation = await db.AiAsrTransportObservations.AsNoTracking()
            .Where(x => x.OwnerId == job.OwnerId && x.LogId == job.LogId)
            .OrderByDescending(x => x.Source == "client_http_response")
            .ThenByDescending(x => x.ObservedAt)
            .FirstOrDefaultAsync(ct);
        return new(job.ClientRequestId, job.LogId, Status(job.Status), response,
            job.ErrorCode, job.ErrorMessage, job.CreatedAt, job.StartedAt, job.CancelRequestedAt,
            job.FinishedAt, replayed,
            new AiAsrJobDiagnosticsDto(job.AbortSource, job.OutcomeKnown,
                job.GatewayStatus ?? observation?.GatewayStatus,
                job.GatewayName ?? observation?.GatewayName,
                job.GatewayEvidenceSource ?? observation?.Source,
                observation?.TransportKind,
                job.GatewayObservedAt ?? observation?.ObservedAt,
                job.UpstreamStatus, job.ProviderAttempted, job.ResponseBodyComplete,
                job.MaxOutputTokens, job.EstimatedAudioSeconds, job.EstimatedCompletionTokens,
                job.OutputLimitExceeded, job.NextAction));
    }

    private static string JobIntentJson(PreparedJob prepared, string language) =>
        JsonSerializer.Serialize(new
        {
            recordFormat = "sdk-asr-job-v1",
            presetId = prepared.Description.Id,
            presetVersion = prepared.Description.Version,
            model = prepared.Profile.Model,
            endpoint = prepared.Operation,
            language,
            promptRevision = prepared.Prompt.Revision,
            promptSha256 = prepared.Prompt.Sha256,
            promptTemplateRevision = prepared.Prompt.TemplateRevision,
            promptTemplateSha256 = prepared.Prompt.TemplateSha256,
            maxOutputTokens = prepared.Profile.MaxOutputTokens,
            timeoutSeconds = prepared.Profile.TimeoutSeconds,
            audio = new
            {
                prepared.Audio.Name,
                prepared.Audio.Format,
                prepared.Audio.Sha256,
                sizeBytes = prepared.Audio.Bytes.Length
            },
            stream = false
        }, Json);

    private static string JobMetadata(AiAsrJobEntity job, bool providerAttempted,
        bool responseBodyComplete, bool requestPayloadCaptured, bool responseTooLarge,
        double? reportedSeconds) => JsonSerializer.Serialize(new
    {
        recordFormat = "sdk-asr-job-v1",
        asrPresetId = job.PresetId,
        asrPresetVersion = job.PresetVersion,
        language = job.Language,
        promptRevision = job.PromptRevision,
        promptSha256 = job.PromptSha256,
        promptTemplateRevision = job.PromptTemplateRevision,
        promptTemplateSha256 = job.PromptTemplateSha256,
        audioEncoding = "sha256-reference",
        maxOutputTokens = job.MaxOutputTokens,
        timeoutSeconds = job.TimeoutSeconds,
        estimatedAudioSeconds = job.EstimatedAudioSeconds,
        estimatedCompletionTokens = job.EstimatedCompletionTokens,
        outputLimitExceeded = job.OutputLimitExceeded,
        destinationConsent = ParseNode(job.ConsentJson),
        providerAttempted,
        requestBodyCaptured = requestPayloadCaptured,
        responseBodyComplete,
        reportedAudioSeconds = reportedSeconds,
        responseTooLarge,
        transportRetries = 0,
        abortSource = job.AbortSource,
        outcomeKnown = job.OutcomeKnown,
        gatewayStatus = job.GatewayStatus,
        gatewayName = job.GatewayName,
        gatewayEvidenceSource = job.GatewayEvidenceSource,
        transportObservationKind = job.GatewayEvidenceSource is null ? null : "async_poll",
        gatewayObservedAt = job.GatewayObservedAt,
        upstreamStatus = job.UpstreamStatus
    }, Json);

    private static JsonNode? ParseNode(string value)
    {
        try { return JsonNode.Parse(value); } catch (JsonException) { return null; }
    }

    private static AiAsrJobStatus Status(string status) => status switch
    {
        "preparing" => AiAsrJobStatus.Queued,
        "queued" => AiAsrJobStatus.Queued,
        "running" => AiAsrJobStatus.Running,
        "cancelling" => AiAsrJobStatus.Cancelling,
        "succeeded" => AiAsrJobStatus.Succeeded,
        "incomplete" => AiAsrJobStatus.Incomplete,
        "timed_out" => AiAsrJobStatus.TimedOut,
        "cancelled" => AiAsrJobStatus.Cancelled,
        "blocked" => AiAsrJobStatus.Blocked,
        _ => AiAsrJobStatus.Failed
    };

    private static bool Terminal(string status) =>
        status is "succeeded" or "incomplete" or "failed" or "cancelled" or "blocked" or "timed_out";

    private static string? UpstreamNextAction(int status) => status switch
    {
        401 or 403 => "核对ASR模型凭据与访问权限后明确新建请求",
        413 => "缩短音频或提高服务商限制后明确新建请求",
        429 => "等待服务商限流恢复后明确新建请求",
        >= 500 => "核对服务商记录并稍后明确新建请求，不要自动重发",
        _ => "核对服务商错误与调用记录后明确新建请求"
    };

    private static bool IsTimeoutStatus(int status) => status is 408 or 504 or 524;

    private static long RequestBudgetBytes(int maxDecodedBytes) =>
        checked(4L * ((maxDecodedBytes + 2L) / 3L) + 1024L * 1024);

    private static string? Clip(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private sealed record PreparedJob(AiModelProfileEntity Profile, AiAsrPresetDto Description,
        string Operation, AiAsrInput Audio, AiAsrPromptSnapshot Prompt, string Fingerprint,
        string? ConfigurationError, string? ConfigurationCode, double? EstimatedAudioSeconds,
        int? EstimatedCompletionTokens, bool OutputLimitExceeded);
}
