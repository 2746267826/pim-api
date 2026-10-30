using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notes;

public sealed class NoteDerivedService(
    NoteDerivedStore store,
    NoteDerivedJob job,
    NoteMaterialUploadStore materials,
    NoteAsrTaskStore asrTasks,
    PimDbContext db,
    ICurrentUserService user,
    ISecretProtector secrets,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");
    private bool BackgroundProcessingAvailable => !(bool.TryParse(configuration["DisableHangfire"], out var disabled) && disabled)
        && !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"));

    public NoteDerivedLimitsDto Limits => NoteDerivedLimits.Describe();

    public async Task<NoteDerivedTaskDto> StartAsync(StartNoteDerivedTaskRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.TaskId == Guid.Empty || request.VersionId == Guid.Empty)
            throw Invalid("派生任务或材料版本标识无效");
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "账号已改变，请重新核对材料和目标");
        var version = await materials.GetAsync(owner, request.VersionId, ct);
        if (version.Upload.Phase == "Uploading")
            throw Conflict("请先上传完整固定材料，再生成派生稿");
        var inputSha = NoteMaterialManifestValidator.Hash(version.InputJson);
        var manifestSha = NoteMaterialManifestValidator.Hash(version.ManifestJson);
        var fingerprint = Fingerprint(request, inputSha, manifestSha);

        try
        {
            var existing = await store.ReadTaskAsync(owner, request.TaskId, ct);
            if (existing.Fingerprint != fingerprint)
                throw Conflict("任务标识已经绑定另一份派生输入，原任务保留");
            return await DescribeAsync(existing, ct);
        }
        catch (DomainException error) when (error.ErrorCode == 40401)
        {
        }

        var hasImages = version.Upload.Files.Any(x => x.Kind is "Photo" or "InkPreview");
        await ValidateDestinationAsync(request.Consent, hasImages, ct);
        var snapshot = await BuildSnapshotAsync(owner, version, inputSha, manifestSha, request.AsrTaskId, ct);
        var plan = new NoteDerivedPlanner().Build(snapshot);
        var state = await store.CreateTaskAsync(owner, request, plan, fingerprint, ct);
        return await DescribeAsync(state, ct);
    }

    public async Task<IReadOnlyList<NoteDerivedTaskDto>> ListAsync(Guid? noteId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var states = await store.ListTasksAsync(owner, noteId, ct);
        var result = new List<NoteDerivedTaskDto>(states.Count);
        foreach (var state in states) result.Add(await DescribeAsync(state, ct));
        return result;
    }

    public async Task<NoteDerivedTaskDto> GetAsync(Guid taskId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        await job.ReconcileAsync(owner, taskId, ct);
        return await DescribeAsync(await store.ReadTaskAsync(owner, taskId, ct), ct);
    }

    public async Task<NoteDerivedStepsDto> StepsAsync(Guid taskId, int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (offset < 0 || limit is < 1 or > 20)
            throw Invalid("派生步骤分页范围无效，单页最多20步");
        var state = await store.ReadTaskAsync(owner, taskId, ct);
        return new(state.Steps.Count, offset, state.Steps.Skip(offset).Take(limit)
            .Select(NoteDerivedStore.DescribeStep).ToArray());
    }

    public async Task<NoteDerivedTaskDto> StopAsync(Guid taskId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var state = await store.UpdateTaskAsync(owner, taskId, current => current.StopRequested = true, ct);
        return await DescribeAsync(state, ct);
    }

    public async Task<NoteDerivedTaskDto> RetryAsync(Guid taskId, RetryNoteDerivedTaskRequest request,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "账号已改变，请重新核对");
        if (request.RetryId == Guid.Empty || request.Consent?.Confirmed != true ||
            !request.AcknowledgePossibleDuplicateCharge ||
            request.StepKeys is not { Count: > 0 and <= 20 } ||
            request.StepKeys.Distinct(StringComparer.Ordinal).Count() != request.StepKeys.Count ||
            request.StepKeys.Any(string.IsNullOrWhiteSpace))
            throw Invalid("请选择待恢复步骤，核对原目标，并确认重试可能重复计费");

        using var lease = store.TryRunLease(owner, taskId);
        if (lease is null)
            throw Conflict("派生任务正在执行，请先核对当前回执状态");
        await job.ReconcileLockedAsync(owner, taskId, ct);

        var before = await store.ReadTaskAsync(owner, taskId, ct);
        var fingerprint = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, Json));
        if (before.Retries.TryGetValue(request.RetryId, out var previous))
        {
            if (previous != fingerprint) throw Conflict("恢复标识已经绑定另一组步骤，原恢复记录保留");
            return await DescribeAsync(before, ct);
        }
        if (before.Blocked || before.Plan.Steps.Count == 0)
            throw Conflict("当前任务存在输入级阻断，不能按步骤重试");
        if (request.Consent != before.Request.Consent)
            throw Conflict("重试目标与固定任务不一致，请新建任务");
        await ValidateDestinationAsync(request.Consent, before.Plan.Input.Images.Count > 0, ct);

        var updated = await store.UpdateTaskAsync(owner, taskId, state =>
        {
            if (state.Retries.TryGetValue(request.RetryId, out var already))
            {
                if (already != fingerprint) throw Conflict("恢复标识已经绑定另一组步骤，原恢复记录保留");
                return;
            }
            if (state.Revision != request.ExpectedRevision)
                throw Conflict("任务进度已变化，请刷新后核对");
            foreach (var key in request.StepKeys)
            {
                var step = state.Steps.SingleOrDefault(x => x.Key == key)
                    ?? throw Conflict("包含当前任务不存在的步骤，请刷新后重试");
                if (step.Current.Status is "Succeeded" or "Running")
                    throw Conflict("不能重跑成功或执行中的步骤");
                if (step.Attempts.Count >= 10)
                    throw new DomainException(41301, "单步尝试次数已达10次，原结果保留");
            }
            foreach (var key in request.StepKeys)
            {
                var step = state.Steps.Single(x => x.Key == key);
                if (step.Current.Status != "Queued")
                    step.Attempts.Add(new());
                else
                {
                    step.Current.ErrorCode = null;
                    step.Current.ErrorMessage = null;
                    step.Current.ResultSha256 = null;
                }
            }
            state.Retries.Add(request.RetryId, fingerprint);
            state.StopRequested = false;
        }, ct);
        return await DescribeAsync(updated, ct);
    }

    public async Task<NoteAsrSourcesDto> SourcesAsync(int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await materials.ListAsrSourcesAsync(owner, offset, limit, ct, includeWithoutAudio: true);
    }

    public async Task<NoteDerivedDestinationsDto> DestinationsAsync(CancellationToken ct)
    {
        await RequireActiveOwnerAsync(ct);
        if (!await db.AiModelRuntime.AsNoTracking().AnyAsync(x => x.Enabled, ct))
            return new(0, []);
        var profiles = await db.AiModelProfiles.AsNoTracking()
            .Where(x => x.Enabled && x.Capabilities != 4 && (x.Capabilities & 1) != 0)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var items = profiles.Select(TryDescribeDestination).Where(x => x is not null)
            .Cast<NoteDerivedDestinationDto>().ToArray();
        return new(items.Length, items);
    }

    public async Task<IReadOnlyList<NoteDerivedArtifactDto>> ListArtifactsAsync(Guid noteId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (noteId == Guid.Empty) throw Invalid("笔记标识无效");
        var artifacts = await store.ListArtifactsAsync(owner, noteId, ct);
        var result = new List<NoteDerivedArtifactDto>(artifacts.Count);
        foreach (var artifact in artifacts)
            result.Add(await store.DescribeArtifactAsync(owner, artifact.ArtifactId, ct));
        return result;
    }

    public async Task<NoteDerivedArtifactDto> GetArtifactAsync(Guid artifactId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await store.DescribeArtifactAsync(owner, artifactId, ct);
    }

    public async Task<NoteDerivedVersionDto> GetVersionAsync(Guid artifactId, Guid versionId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await store.GetVersionAsync(owner, artifactId, versionId, ct);
    }

    public async Task<NoteDerivedMutationDto> SaveManualRevisionAsync(Guid artifactId,
        SaveNoteDerivedManualRevisionRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var mutation = await store.AppendManualAsync(owner, artifactId, request, ct);
        return mutation with
        {
            Artifact = await store.DescribeArtifactAsync(owner, mutation.Artifact.ArtifactId, ct)
        };
    }

    public async Task<NoteDerivedArtifactDto> SelectAsync(Guid artifactId,
        SelectNoteDerivedVersionRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await store.SelectHeadAsync(owner, artifactId, request, ct);
    }

    private async Task<NoteDerivedInputSnapshot> BuildSnapshotAsync(Guid owner, NoteMaterialVersionDto version,
        string inputSha, string manifestSha, Guid? asrTaskId, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(version.InputJson);
        var input = document.RootElement;
        var note = input.GetProperty("note");
        var noteTitle = note.GetProperty("title").GetString() ?? "未命名笔记";
        var textBlocks = new List<NoteDerivedTextInput>();
        var imageBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<NoteDerivedIssueDto>();
        var blockOrdinal = 0;
        foreach (var block in note.GetProperty("blocks").EnumerateArray())
        {
            var blockId = block.GetProperty("id").GetGuid();
            var kind = block.GetProperty("kind").GetString();
            if (kind == "markdown")
            {
                var text = block.GetProperty("text").GetString() ?? "";
                textBlocks.Add(new(blockOrdinal++, blockId, text));
                continue;
            }
            if (kind == "photo")
            {
                var assetId = block.GetProperty("assetId").GetGuid();
                var extension = block.GetProperty("mimeType").GetString() switch
                {
                    "image/jpeg" => "jpg", "image/png" => "png", "image/webp" => "webp", _ => "bin"
                };
                var path = $"assets/{assetId:D}.{extension}";
                imageBlocks[path] = blockId.ToString("D");
                continue;
            }
            if (kind == "ink")
            {
                var ink = block.GetProperty("ink");
                var path = $"assets/{blockId:D}-{NoteMaterialManifestValidator.Hash(ink.GetRawText())}.png";
                imageBlocks[path] = blockId.ToString("D");
            }
        }

        var markdownFiles = version.Upload.Files.Where(x => x.Kind == "Markdown").ToArray();
        var noteMarkdown = "";
        if (markdownFiles.Length != 1)
        {
            issues.Add(new("markdown_file_missing",
                $"固定材料必须恰有一个 Markdown 文件，实际为 {markdownFiles.Length} 个"));
        }
        else
        {
            try
            {
                using var markdownBytes = new MemoryStream();
                await materials.ReadFileAsync(owner, version.Upload.VersionId, markdownFiles[0].Index,
                    markdownBytes, ct);
                noteMarkdown = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(markdownBytes.ToArray());
            }
            catch (DecoderFallbackException)
            {
                issues.Add(new("markdown_utf8_invalid", "固定 document.md 不是有效 UTF-8",
                    markdownFiles[0].Path));
            }
            catch (DomainException error)
            {
                issues.Add(new("markdown_unreadable",
                    "无法读取固定 document.md 或实际摘要已变化：" + error.Message,
                    markdownFiles[0].Path));
            }
            catch (IOException error)
            {
                issues.Add(new("markdown_unreadable",
                    "读取固定 document.md 时发生 I/O 错误：" + error.Message,
                    markdownFiles[0].Path));
            }
        }

        var images = new List<NoteDerivedImageInput>();
        var ordinal = 0;
        foreach (var file in version.Upload.Files.Where(x => x.Kind is "Photo" or "InkPreview"))
        {
            imageBlocks.TryGetValue(file.Path, out var blockId);
            if (blockId is null)
                issues.Add(new("image_unmapped", "图片文件未对应固定原稿中的照片/手写块", file.Path));
            images.Add(new(ordinal++, file.Index, file.Path, file.Kind, file.Sha256, file.SizeBytes,
                ContentType(file.Path), blockId));
        }
        foreach (var path in imageBlocks.Keys.Where(path =>
                     version.Upload.Files.All(file => !string.Equals(file.Path, path, StringComparison.Ordinal))))
            issues.Add(new("image_missing", "固定原稿声明的图片不存在于材料清单", path,
                imageBlocks[path]));

        var recordings = input.GetProperty("recordings").GetArrayLength();
        var transcripts = new List<NoteDerivedTranscriptInput>();
        Guid? asrRevision = null;
        if (recordings == 0)
        {
            if (asrTaskId is not null)
                issues.Add(new("asr_unexpected", "零录音材料不应绑定 ASR 任务，请移除后重试"));
        }
        else if (asrTaskId is null)
        {
            issues.Add(new("asr_required", "材料包含录音，必须先完成同一固定版本的 ASR 任务"));
        }
        else
        {
            var asr = await asrTasks.ReadAsync(owner, asrTaskId.Value, ct);
            asrRevision = asr.Revision;
            if (asr.Request.VersionId != version.Upload.VersionId ||
                asr.InputSha256 != inputSha || asr.ManifestSha256 != manifestSha)
                issues.Add(new("asr_source_mismatch", "ASR 任务不属于当前固定材料版本或摘要不一致"));
            var phase = NoteAsrTaskStore.Describe(asr).Phase;
            if (phase != "Succeeded")
                issues.Add(new("asr_incomplete", $"ASR 尚未完整成功，当前状态为 {phase}"));
            List<NoteAsrStep> expectedSteps;
            try
            {
                expectedSteps = NoteAsrPlan.Build(version);
            }
            catch (DomainException error)
            {
                expectedSteps = [];
                issues.Add(new("asr_plan_invalid", "无法从固定材料重建完整 ASR 片段计划：" + error.Message));
            }
            var actualSteps = asr.Steps.OrderBy(x => x.Index).ToArray();
            if (actualSteps.Length != expectedSteps.Count)
                issues.Add(new("asr_segment_set_mismatch",
                    $"ASR 任务包含 {actualSteps.Length} 片，固定材料应有 {expectedSteps.Count} 片；禁止遗漏或复用其他片段"));
            for (var index = 0; index < Math.Min(actualSteps.Length, expectedSteps.Count); index++)
            {
                var expected = expectedSteps[index];
                var actual = actualSteps[index];
                if (actual.Index != expected.Index ||
                    actual.RecordingId != expected.RecordingId ||
                    actual.RecordingSegment != expected.RecordingSegment ||
                    actual.ChunkIndex != expected.ChunkIndex ||
                    actual.FileIndex != expected.FileIndex ||
                    !string.Equals(actual.Path, expected.Path, StringComparison.Ordinal) ||
                    !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) ||
                    actual.SourceOffset != expected.SourceOffset ||
                    actual.RecordingOffset != expected.RecordingOffset ||
                    actual.TimelineOffset != expected.TimelineOffset ||
                    actual.Samples != expected.Samples)
                    issues.Add(new("asr_segment_plan_mismatch",
                        "ASR 片段身份、来源文件、偏移或样本范围与固定材料重建计划不一致",
                        actual.Path, StepKey: $"asr:{actual.Index:D5}"));
            }
            var requestIds = actualSteps.Select(x => x.Current.RequestId).ToArray();
            var logs = await db.AiRequestLogs.AsNoTracking()
                .Where(x => x.UserId == owner && x.Purpose == "ai.asr-call" &&
                    x.SourceObjectType == "native-note-asr-task" &&
                    x.SourceObjectId == asrTaskId.Value.ToString() &&
                    x.ClientRequestId.HasValue && requestIds.Contains(x.ClientRequestId.Value))
                .ToDictionaryAsync(x => x.ClientRequestId!.Value, ct);
            var logIds = logs.Values.Select(x => x.Id).ToArray();
            var audioRows = await db.AiCallAudio.AsNoTracking()
                .Where(x => logIds.Contains(x.LogId))
                .ToListAsync(ct);
            var audioByLog = audioRows.GroupBy(x => x.LogId).ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var step in actualSteps)
            {
                var attempt = step.Current;
                if (attempt.Status != "Succeeded" || attempt.LogId is null)
                {
                    issues.Add(new("asr_segment_incomplete", "ASR 分片尚未成功完整",
                        step.Path, StepKey: $"asr:{step.Index:D5}"));
                    continue;
                }
                if (!logs.TryGetValue(attempt.RequestId, out var receipt) ||
                    receipt.Id != attempt.LogId || receipt.Status != "succeeded" ||
                    receipt.ResponseText is null ||
                    receipt.ModelProfileId != asr.Request.Consent.ModelId ||
                    receipt.ModelRevision != asr.Request.Consent.ModelRevision ||
                    !string.Equals(receipt.ModelEndpoint, asr.Request.Consent.Endpoint, StringComparison.Ordinal))
                {
                    issues.Add(new("asr_receipt_mismatch",
                        "ASR 成功片段缺少可核对结果，或回执的类型、模型、revision、地址与固定任务不一致",
                        step.Path, StepKey: $"asr:{step.Index:D5}"));
                    continue;
                }
                if (!audioByLog.TryGetValue(receipt.Id, out var storedAudio) || storedAudio.Length != 1)
                {
                    issues.Add(new("asr_audio_missing", "ASR 成功片段没有唯一可核对的原音频",
                        step.Path, StepKey: $"asr:{step.Index:D5}"));
                    continue;
                }
                var audio = storedAudio[0];
                var expectedName = $"segment-{step.Index + 1:D5}.wav";
                if (audio.Name != expectedName || audio.Format != "wav" ||
                    audio.SizeBytes != 44 + step.Samples * 2 || audio.ObjectKey is null)
                {
                    issues.Add(new("asr_audio_mismatch", "ASR 原音频名称、格式、样本大小或保存引用不一致",
                        step.Path, StepKey: $"asr:{step.Index:D5}"));
                    continue;
                }
                try
                {
                    var expectedBytes = await materials.ReadAudioSliceAsync(owner, version.Upload.VersionId,
                        step.FileIndex, step.SourceOffset, step.Samples, ct);
                    var expectedHash = Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant();
                    if (expectedBytes.Length != audio.SizeBytes ||
                        !string.Equals(expectedHash, audio.Sha256, StringComparison.Ordinal))
                    {
                        issues.Add(new("asr_audio_fingerprint_mismatch",
                            "ASR 保存的片段摘要与固定材料重新读取的真实片段不一致",
                            step.Path, StepKey: $"asr:{step.Index:D5}"));
                        continue;
                    }
                }
                catch (DomainException error)
                {
                    issues.Add(new("asr_audio_unreadable",
                        "无法从固定材料重新核对 ASR 片段：" + error.Message,
                        step.Path, StepKey: $"asr:{step.Index:D5}"));
                    continue;
                }
                transcripts.Add(new(transcripts.Count, step.Index, step.RecordingId, step.RecordingSegment,
                    step.ChunkIndex, step.FileIndex, step.Path, step.Sha256, step.SourceOffset, step.Samples,
                    receipt.ResponseText, receipt.Id));
            }
            if (asr.Steps.Count == 0)
                issues.Add(new("asr_segment_missing", "ASR 任务没有可核对片段"));
        }

        return new(owner, version.Upload.NoteId, version.Upload.VersionId, asrTaskId, asrRevision,
            inputSha, manifestSha, noteTitle, noteMarkdown, textBlocks, transcripts,
            images, issues);
    }

    private async Task ValidateDestinationAsync(NoteDerivedGenerationConsent? consent, bool hasImages,
        CancellationToken ct)
    {
        if (consent?.Confirmed != true)
            throw Invalid("请核对本次固定材料、派生目标与实际地址，并明确同意发送");
        var profile = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == consent.ModelId, ct);
        if (profile is null || !profile.Enabled || profile.Capabilities == 4 ||
            (profile.Capabilities & 1) == 0 || hasImages && (profile.Capabilities & 2) == 0 ||
            !await db.AiModelRuntime.AsNoTracking().AnyAsync(x => x.Enabled, ct))
            throw Conflict("派生模型未启用、能力不足，或 SDK 当前不可用");
        if (consent.ModelRevision != profile.Revision ||
            !string.Equals(consent.Endpoint, profile.BaseUrl, StringComparison.Ordinal))
            throw Conflict("派生目标配置已变化，请刷新并重新确认");
        if (!CredentialAvailable(profile.ApiKeySecret))
            throw Conflict("派生模型凭据当前不可用，请管理员重新保存");
    }

    private NoteDerivedDestinationDto? TryDescribeDestination(Pim.Infrastructure.Data.Entities.AiModelProfileEntity profile)
    {
        try
        {
            if (profile.Capabilities == 4 || (profile.Capabilities & 1) == 0 ||
                !CredentialAvailable(profile.ApiKeySecret)) return null;
            var capabilities = Enum.GetValues<AiModelCapability>()
                .Where(value => (profile.Capabilities & (1 << (int)value)) != 0)
                .Select(value => value.ToString()).ToArray();
            return new(profile.Id, profile.Revision, profile.Name, profile.Model, profile.BaseUrl,
                capabilities, BackgroundProcessingAvailable);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private async Task<NoteDerivedTaskDto> DescribeAsync(NoteDerivedTaskState state, CancellationToken ct)
    {
        var described = NoteDerivedStore.DescribeTask(state) with
        {
            BackgroundProcessingAvailable = BackgroundProcessingAvailable
        };
        var record = await store.GetArtifactAsync(state.Owner, state.CompleteRecordArtifactId, ct);
        var analysis = await store.GetArtifactAsync(state.Owner, state.IndependentAnalysisArtifactId, ct);
        return described with
        {
            Artifacts =
            [
                new(record.ArtifactId, record.Kind, record.HeadVersionId),
                new(analysis.ArtifactId, analysis.Kind, analysis.HeadVersionId)
            ]
        };
    }

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用，不能创建或查看派生稿任务");
        return owner;
    }

    private bool CredentialAvailable(string protectedValue)
    {
        try
        {
            return !string.IsNullOrEmpty(secrets.Unprotect(protectedValue));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static string Fingerprint(StartNoteDerivedTaskRequest request, string inputSha, string manifestSha) =>
        NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(new { request, inputSha, manifestSha }, Json));

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Conflict(string message) => new(40901, message);
}
