using System.ClientModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using static Pim.Infrastructure.Ai.AiCallAudit;

namespace Pim.Infrastructure.Ai.Asr;

public sealed class AiAsrCallService(PimDbContext db, ISecretProtector secrets, ICurrentUserService currentUser,
    AiTextCallGate gate, AiAsrPresetRegistry presets, AiAsrMaterialService materials,
    IRemoteAudioFetcher? remoteAudio = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<AiTextCallResultDto> CallAsync(AiAsrCallRequest request, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        return await CallForOwnerAsync(owner, request, null, ct);
    }

    public async Task<AiAsrUrlCallResultDto> CallUrlAsync(AiAsrUrlCallRequest request, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        return await CallUrlForOwnerAsync(owner, request, ct);
    }

    private async Task<AiAsrUrlCallResultDto> CallUrlForOwnerAsync(Guid owner,
        AiAsrUrlCallRequest request, CancellationToken ct)
    {
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "会话已改变，URL音频未读取，请重新核对账号");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "发起账号已停用，URL音频未读取");
        if (request.ClientRequestId == Guid.Empty || request.Audio is null ||
            request.Consent?.Confirmed != true || !request.Audio.SourceConfirmed ||
            !request.Audio.ProviderFetchConfirmed)
            throw new DomainException(40001,
                "请确认公开URL音频、第三方再次读取、ASR预设、模型和目标地址");
        if (!Uri.TryCreate(request.Audio.Url, UriKind.Absolute, out var sourceUrl) ||
            sourceUrl.OriginalString.Length > SecureRemoteAudioFetcher.MaxUrlChars ||
            !sourceUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            sourceUrl.UserInfo.Length != 0 || sourceUrl.Fragment.Length != 0)
            throw new DomainException(40001, "URL音频只允许不超过8192字符的公开HTTPS地址");
        var requestedName = request.Audio.Name;
        if (requestedName is null || string.IsNullOrWhiteSpace(requestedName) ||
            requestedName.Length > 200 || requestedName.Any(char.IsControl))
            throw new DomainException(40001, "URL音频名称无效");
        var expectedSha256 = NormalizeExpectedSha256(request.Audio.ExpectedSha256);

        var runtime = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var modelId = request.ModelId ?? runtime?.TranscriptionModelId;
        var profile = modelId is { } id
            ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            : null;
        if (profile is null) throw new DomainException(40001, "请选择带有预设的ASR模型");
        var preset = presets.Resolve(profile.AsrPresetId);
        var description = preset.Descriptor;
        if (description.Id != MimoMultimodalPreset.PresetId)
            throw new DomainException(40001, "URL音频模式仅支持MiMo多模态预设");
        preset.ValidateModel(profile.Model);
        var endpoint = preset.NormalizeEndpoint(profile.BaseUrl);
        var operation = AiAsrPresetRegistry.OperationEndpoint(endpoint, description);
        if (request.Consent.ModelId != profile.Id ||
            request.Consent.ModelRevision != profile.Revision ||
            request.Consent.PresetId != description.Id ||
            request.Consent.PresetVersion != description.Version ||
            request.Consent.Endpoint != operation)
            throw new DomainException(40901,
                "ASR预设、模型或目标地址已变化，URL音频未读取，请刷新并重新核对");
        if (request.Language is null || !description.Languages.Contains(request.Language))
            throw new DomainException(40001, "语言不符合所选ASR预设");
        if (request.Audio.Format is null ||
            !description.Formats.Contains(request.Audio.Format, StringComparer.Ordinal))
            throw new DomainException(40001, "URL音频格式不符合所选ASR预设");
        var safeName = "remote-audio." + request.Audio.Format;
        var expectedPrompt = AiAsrPromptTemplate.Normalize(profile.AsrPrompt).Snapshot(request.Language);
        var prompt = request.Prompt ?? expectedPrompt;
        if (prompt.Text != expectedPrompt.Text || prompt.Language != expectedPrompt.Language ||
            prompt.Sha256 != expectedPrompt.Sha256 || prompt.Revision != expectedPrompt.Revision ||
            prompt.TemplateSha256 != expectedPrompt.TemplateSha256 ||
            prompt.TemplateRevision != expectedPrompt.TemplateRevision)
            throw new DomainException(40901,
                "ASR提示词修订已变化，URL音频未读取，请刷新后重新核对");
        var fetcher = remoteAudio ?? new SecureRemoteAudioFetcher();

        var sourceHash = Hash(sourceUrl.OriginalString);
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            kind = "asr-url",
            request.ModelId,
            request.Language,
            request.Consent,
            prompt.Revision,
            promptSha256 = prompt.Sha256,
            name = safeName,
            format = request.Audio.Format,
            sourceHash,
            expectedSha256,
            sourceConfirmed = request.Audio.SourceConfirmed,
            providerFetchConfirmed = request.Audio.ProviderFetchConfirmed
        }, Json));

        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var old = await FindAsync(owner, request.ClientRequestId, ct);
        if (old is not null) return UrlReplay(old, fingerprint);

        string? key = null;
        string? configurationError = null;
        if (runtime?.Enabled != true || !profile.Enabled ||
            (profile.Capabilities & AiModelCapabilityFlags.Transcription) == 0)
        {
            configurationError = "SDK未启用，或该配置不是已启用的MiMo多模态ASR模型";
        }
        else
        {
            try
            {
                key = secrets.Unprotect(profile.ApiKeySecret);
                if (string.IsNullOrEmpty(key))
                    configurationError = "ASR凭据不可用，请重新保存";
            }
            catch (Exception error) when (error is CryptographicException or FormatException)
            {
                configurationError = "ASR凭据无法读取，请重新保存";
            }
        }

        var providerBudget = TimeSpan.FromSeconds(Math.Max(1, profile.TimeoutSeconds));
        var operationBudget = SecureRemoteAudioFetcher.DefaultOperationBudget + providerBudget;
        var leaseBudget = operationBudget + TimeSpan.FromSeconds(60);
        var now = DateTimeOffset.UtcNow;
        var row = new AiRequestLogEntity
        {
            UserId = owner,
            ClientRequestId = request.ClientRequestId,
            InputHash = fingerprint,
            Module = "system",
            Purpose = "ai.asr-url-call",
            SourceObjectType = "ai-model-public-url",
            SourceObjectId = profile.Id.ToString(),
            Provider = description.Id,
            Model = profile.Model,
            ModelProfileId = profile.Id,
            ModelProfileName = profile.Name,
            ModelRevision = profile.Revision,
            ModelEndpoint = operation,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = configurationError is null ? "running" : "blocked",
            StartedAt = now,
            FinishedAt = configurationError is null ? null : now,
            ExecutionExpiresAt = now + leaseBudget,
            AttemptNumber = 1,
            MaxAttempts = 1,
            ErrorCode = configurationError is null ? null : "configuration_unavailable",
            ErrorMessage = configurationError
        };
        row.RequestMessagesJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                kind = "public-url-audio-preflight-pending",
                name = safeName,
                request.Audio.Format,
                sourceReadConfirmed = request.Audio.SourceConfirmed,
                providerFetchConfirmed = request.Audio.ProviderFetchConfirmed
            }
        }, Json);
        row.RequestPayloadJson = JsonSerializer.Serialize(new
        {
            recordFormat = "sdk-asr-url-v1",
            presetId = description.Id,
            presetVersion = description.Version,
            model = profile.Model,
            endpoint = operation,
            language = request.Language,
            promptRevision = prompt.Revision,
            promptSha256 = prompt.Sha256,
            sourceTransport = "public-https-provider-fetch"
        }, Json);
        row.MetadataJson = UrlMetadata(null, false, null, false, false, null, false);
        db.AiRequestLogs.Add(row);
        try
        {
            // Durable owner/idempotency placeholder: no DNS or HTTP starts before this save wins.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await FindAsync(owner, request.ClientRequestId, ct);
            if (winner is null) throw;
            return UrlReplay(winner, fingerprint);
        }

        if (configurationError is not null) return UrlResult(row, false);
        if (!gate.TryEnter(asr: true))
        {
            row.Status = "blocked";
            row.ErrorCode = "busy";
            row.ErrorMessage = "ASR并发已达上限，请稍后明确新建URL调用";
            return await FinishUrlAsync(row);
        }

        AiAsrInput? audio = null;
        AiCallAudioEntity? storedAudio = null;
        var providerPhase = false;
        var callerCancelled = false;
        using var total = new CancellationTokenSource(operationBudget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(total.Token, ct);
        using var wire = new AiTextWireCapture();
        double? reportedSeconds = null;
        try
        {
            var download = await fetcher.FetchAsync(sourceUrl,
                new RemoteAudioExpectation(request.Audio.Format, expectedSha256), linked.Token);
            var fetched = AiAsrMaterialService.AcceptRemote(safeName, request.Audio.Format,
                sourceUrl, download, description.Formats);
            audio = fetched;
            storedAudio = AiAsrMaterialService.Describe(row.Id, fetched);
            row.RequestMessagesJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    kind = "public-url-audio-preflight",
                    origin = download.Provenance.Origin,
                    fetched.Format,
                    fetched.Sha256,
                    sizeBytes = fetched.Bytes.Length,
                    fetchedAt = download.Provenance.FetchedAt,
                    language = request.Language,
                    integrity = "url-preflight-not-provider-verified"
                }
            }, Json);
            db.AiCallAudio.Add(storedAudio);
            await db.SaveChangesAsync(linked.Token);
            await materials.StoreForOwnerAsync(owner, storedAudio, fetched, linked.Token);

            var connection = new AiModelConnection(endpoint, profile.Model, key!, profile.TimeoutSeconds);
            wire.SetAsrAudit(bytes => preset.AuditRequest(bytes, connection, fetched, storedAudio.Id,
                    request.Language, prompt), fetched.IsRemote ? 512 * 1024 : RequestBudgetBytes(description.MaxAudioBytes));
            providerPhase = true;
            var response = await preset.TranscribeAsync(connection, fetched, request.Language, linked.Token, prompt);
            row.ResponseText = SanitizeRemoteText(Safe(response.Text, key, out var redacted),
                audio, out var textUrlRedacted);
            row.ContentRedacted = redacted || textUrlRedacted;
            row.FinishReason = SafeUrlFinishReason(response.FinishReason);
            row.PromptTokens = Count(response.InputTokens);
            row.CompletionTokens = Count(response.OutputTokens);
            row.TotalTokens = Count(response.TotalTokens);
            reportedSeconds = response.ReportedSeconds;
            if (row.FinishReason == "stop" && response.HasTranscript)
                row.Status = "succeeded";
            else
            {
                row.Status = "incomplete";
                row.ErrorCode = "incomplete_output";
                row.ErrorMessage = "ASR未正常完成或缺少转写字段，请核对回复与结束原因";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            callerCancelled = true;
            row.Status = "cancelled";
            row.ErrorCode = providerPhase ? "provider_cancelled_unknown" : "url_preflight_cancelled";
            row.ErrorMessage = providerPhase
                ? "URL ASR调用方已取消，上游结果UNKNOWN，未自动重试"
                : "URL音频预检已取消，本次未调用provider";
        }
        catch (OperationCanceledException)
        {
            row.Status = "timed_out";
            row.ErrorCode = providerPhase ? "provider_timeout_unknown" : "url_preflight_timeout";
            row.ErrorMessage = providerPhase
                ? "URL ASR上游超时，结果UNKNOWN，未自动重试"
                : "URL音频预检超时，本次未调用provider";
        }
        catch (ClientResultException error)
        {
            row.Status = "failed";
            row.ErrorCode = "upstream_http_" + error.Status;
            row.ErrorMessage = "ASR服务返回HTTP " + error.Status + "，本次未自动重试";
        }
        catch (DomainException error)
        {
            var providerTimedOut = providerPhase && error.ErrorCode == 50401;
            row.Status = providerTimedOut ? "timed_out" : "failed";
            row.ErrorCode = providerTimedOut
                ? "provider_timeout_unknown"
                : providerPhase ? $"provider_{error.ErrorCode}" : $"url_preflight_{error.ErrorCode}";
            row.ErrorMessage = providerPhase
                ? "URL ASR上游请求或响应失败，结果UNKNOWN，本次未自动重试"
                : Clip(error.Message, 2000);
        }
        catch (Exception)
        {
            row.Status = "failed";
            row.ErrorCode = providerPhase ? "provider_error" : "url_preflight_error";
            row.ErrorMessage = providerPhase
                ? "URL ASR连接或响应异常，结果UNKNOWN，本次未自动重试"
                : "URL音频预检失败，本次未调用provider";
        }
        finally
        {
            try
            {
                // URL mode persists only allowlisted fields. Provider wire bodies may echo the
                // signed source URL/query, so they are never copied into durable request/response JSON.
                if (wire.RequestRejected)
                {
                    row.Status = "failed";
                    row.ErrorCode = "asr_request_mismatch";
                    row.ErrorMessage = "SDK请求与固定URL音频/模型/语言不一致，本次未发送";
                }
                if (wire.ResponseTooLarge)
                {
                    row.Status = "failed";
                    row.ErrorCode = "response_too_large";
                    row.ErrorMessage = "ASR响应超过4MiB，未保存为完整结果";
                }
                row.ErrorMessage = SanitizeRemoteText(row.ErrorMessage, audio, out _);
                row.ResponseRawJson = UrlResponseAudit(row);
                row.MetadataJson = UrlMetadata(audio, storedAudio?.ObjectKey is not null,
                    wire.ProviderAttempted || providerPhase, wire.RequestBody is not null,
                    wire.ResponseBody is not null, reportedSeconds, wire.ResponseTooLarge);
            }
            finally
            {
                gate.Exit(asr: true);
            }
        }
        var result = await FinishUrlAsync(row);
        if (callerCancelled) ct.ThrowIfCancellationRequested();
        return result;

        string UrlMetadata(AiAsrInput? fetched, bool audioStored, bool? attempted,
            bool requestCaptured, bool responseComplete, double? seconds, bool tooLarge) =>
            JsonSerializer.Serialize(new
            {
                recordFormat = "sdk-asr-url-v1",
                asrPresetId = description.Id,
                asrPresetVersion = description.Version,
                language = request.Language,
                promptRevision = prompt.Revision,
                promptSha256 = prompt.Sha256,
                promptTemplateRevision = prompt.TemplateRevision,
                promptTemplateSha256 = prompt.TemplateSha256,
                audioEncoding = "provider-public-url",
                audioStored,
                sourceOrigin = fetched?.RemoteOrigin,
                urlPreflightSha256 = fetched?.Sha256,
                urlPreflightVerified = fetched is not null,
                providerFetchVerified = false,
                urlIntegrityWarning = "url-preflight-not-provider-verified",
                providerAttempted = attempted,
                requestBodyCaptured = requestCaptured,
                responseBodyComplete = responseComplete,
                reportedAudioSeconds = seconds,
                responseTooLarge = tooLarge,
                transportRetries = 0,
                sourceConfirmed = request.Audio.SourceConfirmed,
                providerFetchConfirmed = request.Audio.ProviderFetchConfirmed,
                destinationConsent = request.Consent
            }, Json);
    }

    public async Task<AiAsrUrlCallResultDto> GetUrlAsync(Guid requestId, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var row = await FindAsync(owner, requestId, ct);
        if (row is null || row.Purpose != "ai.asr-url-call")
            throw new DomainException(40401, "尚未找到本次URL ASR记录");
        return UrlResult(row, true);
    }

    // Only the durable task runner supplies this identity, read from its persisted owner-bound intent.
    internal async Task<AiTextCallResultDto> CallForOwnerAsync(Guid owner, AiAsrCallRequest request, Guid? taskId, CancellationToken ct)
    {
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "会话已改变，音频未发送，请重新核对账号");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "发起账号已停用，ASR未发送");
        if (request.ClientRequestId == Guid.Empty || request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请核对本次音频、ASR预设、模型和目标地址，并明确同意发送");
        var runtime = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var modelId = request.ModelId ?? runtime?.TranscriptionModelId;
        var profile = modelId is { } id ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        var preset = presets.Resolve(profile?.AsrPresetId); var description = preset.Descriptor;
        if (profile is null) throw new DomainException(40001, "请选择带有预设的ASR模型");
        preset.ValidateModel(profile.Model);
        var endpoint = preset.NormalizeEndpoint(profile.BaseUrl);
        var operation = AiAsrPresetRegistry.OperationEndpoint(endpoint, description);
        if (request.Consent.ModelId != profile.Id || request.Consent.ModelRevision != profile.Revision ||
            request.Consent.PresetId != description.Id || request.Consent.PresetVersion != description.Version ||
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
        var fingerprint = Hash(JsonSerializer.Serialize(new { kind = "asr", request.ModelId, request.Language, request.Consent,
            prompt.Revision, promptSha256 = prompt.Sha256, audio.Name, audio.Format, audio.Sha256,
            size = audio.Bytes.Length }, Json));
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var old = await FindAsync(owner, request.ClientRequestId, ct);
        if (old is not null) return Replay(old, fingerprint);
        string? key = null; string? configurationError = null;
        if (runtime?.Enabled != true || !profile.Enabled ||
            (profile.Capabilities & AiModelCapabilityFlags.Transcription) == 0)
            configurationError = "SDK未启用，或该配置不是已启用的独立ASR模型";
        else
        {
            try { key = secrets.Unprotect(profile.ApiKeySecret); if (string.IsNullOrEmpty(key)) configurationError = "ASR凭据不可用，请重新保存"; }
            catch (Exception error) when (error is CryptographicException or FormatException) { configurationError = "ASR凭据无法读取，请重新保存"; }
        }
        var gateEntered = false;
        try
        {
            if (taskId is not null && configurationError is null)
            {
                if (!gate.TryEnter(asr: true))
                    throw new DomainException(50302, "ASR并发已达上限，当前分片保持排队，稍后自动继续");
                gateEntered = true;
            }
            var now = DateTimeOffset.UtcNow;
            var row = new AiRequestLogEntity { UserId = owner, ClientRequestId = request.ClientRequestId, InputHash = fingerprint,
                Module = taskId is null ? "system" : "notes", Purpose = "ai.asr-call",
                SourceObjectType = taskId is null ? "ai-model" : "native-note-asr-task", SourceObjectId = (taskId ?? profile.Id).ToString(),
                Provider = description.Id, Model = profile.Model, ModelProfileId = profile.Id, ModelProfileName = profile.Name,
                ModelRevision = profile.Revision, ModelEndpoint = operation, CorrelationId = Guid.NewGuid().ToString("N"),
                Status = configurationError is null ? "running" : "blocked", StartedAt = now, FinishedAt = configurationError is null ? null : now,
                ExecutionExpiresAt = now.AddSeconds(profile.TimeoutSeconds + 60), AttemptNumber = 1, MaxAttempts = 1,
                ErrorCode = configurationError is null ? null : "configuration_unavailable", ErrorMessage = configurationError };
            var storedAudio = AiAsrMaterialService.Describe(row.Id, audio);
            row.RequestMessagesJson = JsonSerializer.Serialize(new[] { new { kind = "audio-input", reference = $"pim-ai-audio:{storedAudio.Id:D}",
                audio.Format, audio.Sha256, sizeBytes = audio.Bytes.Length, language = request.Language } }, Json);
            row.RequestPayloadJson = JsonSerializer.Serialize(new { recordFormat = "sdk-asr-v1", presetId = description.Id, presetVersion = description.Version,
                model = profile.Model, endpoint = operation, language = request.Language,
                promptRevision = prompt.Revision, promptSha256 = prompt.Sha256,
                promptTemplateRevision = prompt.TemplateRevision, stream = false }, Json);
            row.MetadataJson = Metadata(false, configurationError is null ? null : false, false, false, null, false);
            db.AiRequestLogs.Add(row); db.AiCallAudio.Add(storedAudio);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached; db.Entry(storedAudio).State = EntityState.Detached;
                var winner = await FindAsync(owner, request.ClientRequestId, ct); if (winner is null) throw;
                return Replay(winner, fingerprint);
            }
            if (configurationError is not null) return Result(row, false);
            if (!gateEntered && !gate.TryEnter(asr: true))
            { row.Status = "blocked"; row.ErrorCode = "busy"; row.ErrorMessage = "ASR并发已达上限，请稍后明确新建调用"; row.MetadataJson = Metadata(false, false, false, false, null, false); return await FinishAsync(row); }
            gateEntered = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            using var wire = new AiTextWireCapture();
            var prepared = false;
            double? reportedSeconds = null;
            string? parsedResponse = null;
            try
            {
                await materials.StoreForOwnerAsync(owner, storedAudio, audio, linked.Token); prepared = true;
                var connection = new AiModelConnection(endpoint, profile.Model, key!, profile.TimeoutSeconds);
                wire.SetAsrAudit(bytes => preset.AuditRequest(bytes, connection, audio, storedAudio.Id,
                        request.Language, prompt),
                    RequestBudgetBytes(description.MaxAudioBytes));
                var response = await preset.TranscribeAsync(connection, audio, request.Language, linked.Token, prompt);
                row.ResponseText = Safe(response.Text, key, out var redacted);
                parsedResponse = SafeJson(ValidJson(response.RawJson), key, out var rawRedacted);
                row.ResponseRawJson = parsedResponse;
                row.ContentRedacted = redacted || rawRedacted; row.FinishReason = response.FinishReason;
                row.PromptTokens = Count(response.InputTokens); row.CompletionTokens = Count(response.OutputTokens); row.TotalTokens = Count(response.TotalTokens);
                reportedSeconds = response.ReportedSeconds;
                if (response.FinishReason == "stop" && response.HasTranscript) row.Status = "succeeded";
                else { row.Status = "incomplete"; row.ErrorCode = "incomplete_output"; row.ErrorMessage = "ASR未正常完成或缺少转写字段，请核对回复与结束原因"; }
            }
            catch (OperationCanceledException)
            { row.Status = ct.IsCancellationRequested ? "cancelled" : "timed_out"; row.ErrorCode = row.Status;
                row.ErrorMessage = prepared ? "ASR已取消或超时，上游结果未确认，请先核对记录" : "音频保存已取消或超时，本次未发送模型"; }
            catch (ClientResultException error)
            { row.Status = "failed"; row.ErrorCode = "upstream_http_" + error.Status; row.ErrorMessage = "ASR服务返回HTTP " + error.Status + "，本次未自动重试"; }
            catch (DomainException error)
            {
                row.Status = "failed";
                row.ErrorCode = prepared ? $"provider_{error.ErrorCode}" : "audio_prepare_failed";
                row.ErrorMessage = prepared ? Clip(error.Message, 2000) : "音频未能保存，本次未发送模型，请检查文件来源";
            }
            catch (Exception error)
            {
                row.Status = "failed";
                row.ErrorCode = prepared ? "provider_error" : "audio_prepare_failed";
                row.ErrorMessage = prepared
                    ? Clip("ASR连接或响应异常，请核对记录；本次未自动重试：" + error.Message, 2000)
                    : "音频未能保存，本次未发送模型，请检查文件来源";
            }
            finally
            {
                try
                {
                    if (wire.RequestBody is { } sent)
                    {
                        row.RequestPayloadJson = SafeJson(sent, key, out var redacted);
                        using var json = JsonDocument.Parse(sent); var messageRedacted = false;
                        if (json.RootElement.TryGetProperty("messages", out var messages)) row.RequestMessagesJson = SafeJson(messages.GetRawText(), key, out messageRedacted);
                        row.ContentRedacted = row.ContentRedacted == true || redacted || messageRedacted;
                    }
                    if (wire.ResponseBody is { } received || parsedResponse is not null)
                    {
                        string? wireJson = null;
                        if (wire.ResponseBody is { } responseBody)
                        {
                            wireJson = SafeJson(ValidJson(responseBody), key, out var responseRedacted);
                            row.ContentRedacted = row.ContentRedacted == true || responseRedacted;
                        }
                        row.ResponseRawJson = ComposeResponseAudit(parsedResponse, wireJson);
                    }
                    if (wire.RequestRejected) { row.Status = "failed"; row.ErrorCode = "asr_request_mismatch"; row.ErrorMessage = "SDK请求与固定音频/模型/语言不一致，本次未发送"; }
                    if (wire.ResponseTooLarge) { row.Status = "failed"; row.ErrorCode = "response_too_large"; row.ErrorMessage = "ASR响应超过4MiB，未保存为完整结果"; }
                    row.MetadataJson = Metadata(storedAudio.ObjectKey is not null, wire.ProviderAttempted, wire.RequestBody is not null,
                        wire.ResponseBody is not null, reportedSeconds, wire.ResponseTooLarge);
                }
                finally { gate.Exit(asr: true); gateEntered = false; }
            }
            return await FinishAsync(row);
        }
        finally { if (gateEntered) gate.Exit(asr: true); }

        string Metadata(bool audioStored, bool? attempted, bool requestCaptured, bool responseComplete, double? seconds, bool tooLarge) => JsonSerializer.Serialize(new {
            recordFormat = "sdk-asr-v1", asrPresetId = description.Id, asrPresetVersion = description.Version, language = request.Language,
            promptRevision = prompt.Revision, promptSha256 = prompt.Sha256,
            promptTemplateRevision = prompt.TemplateRevision, promptTemplateSha256 = prompt.TemplateSha256,
            audioEncoding = "sha256-reference", audioStored, providerAttempted = attempted,
            requestBodyCaptured = requestCaptured, responseBodyComplete = responseComplete, reportedAudioSeconds = seconds,
            responseTooLarge = tooLarge, transportRetries = 0, destinationConsent = request.Consent }, Json);
    }

    private static long RequestBudgetBytes(int maxDecodedBytes) =>
        checked(4L * ((maxDecodedBytes + 2L) / 3L) + 1024L * 1024);

    private static string ComposeResponseAudit(string? parsedJson, string? wireJson)
    {
        JsonNode? parsed = null;
        JsonNode? wire = null;
        try { parsed = parsedJson is null ? null : JsonNode.Parse(parsedJson); }
        catch (JsonException) { parsed = JsonValue.Create(parsedJson); }
        try { wire = wireJson is null ? null : JsonNode.Parse(wireJson); }
        catch (JsonException) { wire = JsonValue.Create(wireJson); }
        var same = parsed is not null && wire is not null && JsonNode.DeepEquals(parsed, wire);
        var upstreamIncluded = parsed is JsonObject obj && obj.ContainsKey("upstream");
        var sources = new JsonObject
        {
            ["parsed"] = parsedJson is null ? "none" : "preset-parser",
            ["raw"] = wireJson is null ? "none"
                : same ? "same-as-parsed"
                : upstreamIncluded ? "parsed.upstream"
                : "http-wire"
        };
        return JsonSerializer.Serialize(new
        {
            recordFormat = "asr-call-response-v1",
            parsed,
            raw = same || upstreamIncluded ? null : wire,
            sources
        }, Json);
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];
    public async Task<AiTextCallResultDto> GetAsync(Guid requestId, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录"); await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var row = await FindAsync(owner, requestId, ct);
        if (row is null || row.Purpose != "ai.asr-call") throw new DomainException(40401, "尚未找到本次ASR记录");
        return Result(row, true);
    }
    private Task<AiRequestLogEntity?> FindAsync(Guid owner, Guid id, CancellationToken ct) => db.AiRequestLogs.AsNoTracking()
        .SingleOrDefaultAsync(x => x.UserId == owner && x.ClientRequestId == id, ct);
    private static AiTextCallResultDto Replay(AiRequestLogEntity row, string fingerprint)
    { if (row.InputHash != fingerprint || row.Purpose != "ai.asr-call") throw new DomainException(40901, "请求标识已绑定另一份输入，原记录保留"); return Result(row, true); }
    private static AiAsrUrlCallResultDto UrlReplay(AiRequestLogEntity row, string fingerprint)
    {
        if (row.InputHash != fingerprint || row.Purpose != "ai.asr-url-call")
            throw new DomainException(40901, "请求标识已绑定另一份输入，原记录保留");
        return UrlResult(row, true);
    }
    private static AiAsrUrlCallResultDto UrlResult(AiRequestLogEntity row, bool replayed)
    {
        var result = Result(row, replayed);
        string? origin = null;
        string? sha256 = null;
        var warning = "url-preflight-not-provider-verified";
        try
        {
            using var json = JsonDocument.Parse(row.MetadataJson);
            var root = json.RootElement;
            if (root.TryGetProperty("sourceOrigin", out var originValue) &&
                originValue.ValueKind == JsonValueKind.String)
                origin = originValue.GetString();
            if (root.TryGetProperty("urlPreflightSha256", out var shaValue) &&
                shaValue.ValueKind == JsonValueKind.String)
                sha256 = shaValue.GetString();
            if (root.TryGetProperty("urlIntegrityWarning", out var warningValue) &&
                warningValue.ValueKind == JsonValueKind.String)
                warning = warningValue.GetString() ?? warning;
        }
        catch (JsonException) { }
        return new(result.LogId, result.Status, result.ResponseText, result.ErrorMessage,
            result.Replayed, origin, sha256, sha256 is not null, false, warning);
    }
    private async Task<AiAsrUrlCallResultDto> FinishUrlAsync(AiRequestLogEntity row)
    {
        var now = DateTimeOffset.UtcNow;
        row.FinishedAt = now;
        row.DurationMs = (long)(row.FinishedAt.Value - row.StartedAt).TotalMilliseconds;
        row.OutputChars = row.ResponseText?.Length ?? 0;
        row.OutputHash = Hash(row.ResponseText ?? row.ResponseRawJson);
        var activeStatus = db.Entry(row).OriginalValues.GetValue<string>(nameof(AiRequestLogEntity.Status));
        var activeExpiry = db.Entry(row).OriginalValues.GetValue<DateTimeOffset?>(
            nameof(AiRequestLogEntity.ExecutionExpiresAt));
        if (db.Database.IsRelational())
        {
            // CAS on the exact durable placeholder. Recovery may have changed running -> incomplete
            // while this request was waiting for the provider; a late response must not overwrite it.
            await db.AiRequestLogs
                .Where(x => x.Id == row.Id && x.UserId == row.UserId &&
                    x.ClientRequestId == row.ClientRequestId && x.InputHash == row.InputHash &&
                    x.Status == "running" && x.ExecutionExpiresAt > now)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, row.Status)
                    .SetProperty(x => x.ResponseText, row.ResponseText)
                    .SetProperty(x => x.ResponseRawJson, row.ResponseRawJson)
                    .SetProperty(x => x.ContentRedacted, row.ContentRedacted)
                    .SetProperty(x => x.FinishReason, row.FinishReason)
                    .SetProperty(x => x.PromptTokens, row.PromptTokens)
                    .SetProperty(x => x.CompletionTokens, row.CompletionTokens)
                    .SetProperty(x => x.TotalTokens, row.TotalTokens)
                    .SetProperty(x => x.ErrorCode, row.ErrorCode)
                    .SetProperty(x => x.ErrorMessage, row.ErrorMessage)
                    .SetProperty(x => x.FinishedAt, row.FinishedAt)
                    .SetProperty(x => x.DurationMs, row.DurationMs)
                    .SetProperty(x => x.OutputChars, row.OutputChars)
                    .SetProperty(x => x.OutputHash, row.OutputHash)
                    .SetProperty(x => x.MetadataJson, row.MetadataJson));
            db.Entry(row).State = EntityState.Detached;
            var current = await db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            return UrlResult(current, false);
        }
        if (activeStatus != "running" || activeExpiry is null || activeExpiry <= now)
        {
            db.Entry(row).State = EntityState.Detached;
            var current = await db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            return UrlResult(current, false);
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await db.SaveChangesAsync(budget.Token); }
        catch (Exception error) when (error is DbUpdateException or OperationCanceledException)
        {
            throw new DomainException(50301, "URL ASR结果记录未能确认保存；请先核对记录，不要自动重发");
        }
        return UrlResult(row, false);
    }
    private static string SanitizeRemoteText(string? value, AiAsrInput? audio, out bool redacted)
    {
        redacted = false;
        if (value is null || audio?.RemoteUrl is not { } remoteUrl) return value ?? "";
        var safe = value;
        foreach (var sensitive in RemoteSensitiveVariants(remoteUrl))
        {
            if (sensitive.Length == 0) continue;
            if (safe.Contains(sensitive, StringComparison.Ordinal))
            {
                safe = safe.Replace(sensitive, "[REDACTED_REMOTE_AUDIO_URL]", StringComparison.Ordinal);
                redacted = true;
            }
            var encoded = JsonEncodedText.Encode(sensitive).ToString();
            if (encoded != sensitive && safe.Contains(encoded, StringComparison.Ordinal))
            {
                safe = safe.Replace(encoded, "[REDACTED_REMOTE_AUDIO_URL]", StringComparison.Ordinal);
                redacted = true;
            }
        }
        return safe;
    }
    private string UrlResponseAudit(AiRequestLogEntity row) => JsonSerializer.Serialize(new
    {
        recordFormat = "sdk-asr-url-response-v1",
        text = row.ResponseText,
        finishReason = row.FinishReason,
        usage = new
        {
            promptTokens = row.PromptTokens,
            completionTokens = row.CompletionTokens,
            totalTokens = row.TotalTokens
        },
        status = row.Status,
        errorCode = row.ErrorCode,
        providerWireBodyPersisted = false
    }, Json);
    private static IReadOnlyList<string> RemoteSensitiveVariants(string remoteUrl)
    {
        var values = new List<string> { remoteUrl };
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return values;
        var withoutQuery = uri.GetLeftPart(UriPartial.Path);
        if (!string.Equals(withoutQuery, remoteUrl, StringComparison.Ordinal)) values.Add(withoutQuery);
        values.Add(uri.AbsolutePath);
        foreach (var segment in uri.Segments.Select(x => x.Trim('/')).Where(x => x.Length > 0))
            values.Add(segment);
        if (uri.Query.Length > 1)
        {
            values.Add(uri.Query);
            values.Add(uri.Query[1..]);
            foreach (var pair in uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                values.Add(pair);
                if (separator > 0) values.Add(pair[..separator]);
                var raw = separator < 0 ? pair : pair[(separator + 1)..];
                values.Add(raw);
                try { values.Add(Uri.UnescapeDataString(raw)); }
                catch (UriFormatException) { }
            }
        }
        var variants = new List<string>();
        foreach (var value in values.Where(value => value.Length > 0))
        {
            variants.Add(value);
            var json = JsonSerializer.Serialize(value).Trim('"');
            variants.Add(json);
            variants.Add(json.Replace("/", "\\/", StringComparison.Ordinal));
            variants.Add(Uri.EscapeDataString(value));
        }
        return variants.Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }
    private static string? NormalizeExpectedSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (!AiAsrUrlAudioRequest.IsSha256(normalized))
            throw new DomainException(40001, "URL音频期望SHA-256必须是64位十六进制");
        return normalized;
    }
    private static string SafeUrlFinishReason(string? value) => value switch
    {
        "stop" => "stop",
        MimoMultimodalResponseParser.EmptyFinishReason => MimoMultimodalResponseParser.EmptyFinishReason,
        MimoMultimodalResponseParser.RepetitionFinishReason => MimoMultimodalResponseParser.RepetitionFinishReason,
        MimoMultimodalResponseParser.TruncationFinishReason => MimoMultimodalResponseParser.TruncationFinishReason,
        MimoMultimodalResponseParser.MissingFinishReason => MimoMultimodalResponseParser.MissingFinishReason,
        _ => "incomplete"
    };
    private async Task<AiTextCallResultDto> FinishAsync(AiRequestLogEntity row)
    {
        row.FinishedAt = DateTimeOffset.UtcNow; row.DurationMs = (long)(row.FinishedAt.Value - row.StartedAt).TotalMilliseconds;
        row.OutputChars = row.ResponseText?.Length ?? 0; row.OutputHash = Hash(row.ResponseText ?? row.ResponseRawJson);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await db.SaveChangesAsync(budget.Token); }
        catch (Exception error) when (error is DbUpdateException or OperationCanceledException)
        { throw new DomainException(50301, "ASR结果记录未能确认保存；请先核对记录，不要自动重发"); }
        return Result(row, false);
    }
}
