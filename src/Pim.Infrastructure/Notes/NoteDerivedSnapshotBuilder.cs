using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

internal sealed record NoteDerivedSnapshotBuild(
    NoteDerivedInputSnapshot Snapshot,
    bool AwaitingAsr);

/// <summary>
/// Rebuilds the exact derived source snapshot from the immutable material version. The service uses
/// this before every start; the worker reuses it to materialize transcripts once an ASR task becomes
/// complete, without ever sending provider calls while dependencies are pending.
/// </summary>
public sealed class NoteDerivedSnapshotBuilder(
    PimDbContext db,
    NoteMaterialUploadStore materials,
    NoteAsrTaskStore asrTasks)
{
    internal async Task<NoteDerivedSnapshotBuild> BuildAsync(Guid owner,
        NoteMaterialVersionDto version, string inputSha, string manifestSha,
        Guid? asrTaskId, bool waitForAsr, bool allowIncompleteAsr, CancellationToken ct,
        bool allowAudioContext = false)
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
                    "image/jpeg" => "jpg",
                    "image/png" => "png",
                    "image/webp" => "webp",
                    _ => "bin"
                };
                imageBlocks[$"assets/{assetId:D}.{extension}"] = blockId.ToString("D");
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
                     version.Upload.Files.All(file =>
                         !string.Equals(file.Path, path, StringComparison.Ordinal))))
            issues.Add(new("image_missing", "固定原稿声明的图片不存在于材料清单", path,
                imageBlocks[path]));

        var recordings = input.GetProperty("recordings").GetArrayLength();
        var audio = new List<NoteDerivedAudioInput>();
        if (allowAudioContext && recordings > 0)
        {
            var audioBuild = await BuildAudioContextAsync(owner, version, input, ct);
            audio.AddRange(audioBuild.Items);
            issues.AddRange(audioBuild.Issues);
        }
        var transcripts = new List<NoteDerivedTranscriptInput>();
        Guid? asrRevision = null;
        var awaitingAsr = false;
        if (recordings == 0)
        {
            if (asrTaskId is not null)
                issues.Add(new("asr_unexpected", "零录音材料不应绑定 ASR 任务，请移除后重试"));
        }
        else if (asrTaskId is null)
        {
            if (!allowAudioContext || audio.Count == 0)
                issues.Add(new("asr_required", "材料包含录音，必须先完成同一固定版本的 ASR 任务"));
        }
        else
        {
            var asr = await asrTasks.ReadAsync(owner, asrTaskId.Value, ct);
            asrRevision = asr.Revision;
            var sourceMatches = asr.Request.VersionId == version.Upload.VersionId &&
                asr.InputSha256 == inputSha && asr.ManifestSha256 == manifestSha;
            if (!sourceMatches)
                issues.Add(new("asr_source_mismatch", "ASR 任务不属于当前固定材料版本或摘要不一致"));
            var phase = NoteAsrTaskStore.Describe(asr).Phase;
            var actualSteps = asr.Steps.OrderBy(x => x.Index).ToArray();
            if (!allowAudioContext && phase != "Succeeded" && waitForAsr && sourceMatches &&
                phase is "Queued" or "Running")
                awaitingAsr = true;
            else if (!allowAudioContext && phase != "Succeeded" && !allowIncompleteAsr)
                issues.Add(new("asr_unavailable",
                    $"ASR 当前未提供可用结果，状态为 {phase}；整理链路不会以空转写继续，请恢复 ASR 后重试"));

            if (!awaitingAsr && phase == "Succeeded" && sourceMatches)
            {
                List<NoteAsrStep> expectedSteps;
                try
                {
                    expectedSteps = NoteAsrPlan.Build(version,
                        asr.SamplesPerCall > 0 ? asr.SamplesPerCall : NoteAsrPlan.SamplesPerCall);
                }
                catch (DomainException error)
                {
                    expectedSteps = [];
                    issues.Add(new("asr_plan_invalid", "无法从固定材料重建完整 ASR 片段计划：" + error.Message));
                }
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

                await AddTranscriptsAsync();
            }

            async Task AddTranscriptsAsync()
            {
                var requestIds = actualSteps.Select(x => x.Current.RequestId).ToArray();
                var logs = await db.AiRequestLogs.AsNoTracking()
                    .Where(x => x.UserId == owner && x.Purpose == "ai.asr-call" &&
                        x.SourceObjectType == "native-note-asr-task" &&
                        x.SourceObjectId == asrTaskId.Value.ToString() &&
                        x.ClientRequestId.HasValue && requestIds.Contains(x.ClientRequestId.Value))
                    .ToDictionaryAsync(x => x.ClientRequestId!.Value, ct);
                var proofAttempt = asr.AttemptId is { } attemptId
                    ? await db.NoteAsrAttempts.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct)
                    : null;
                var evidenceBySegment = proofAttempt is null
                    ? []
                    : await NoteAsrAcceptedEvidence.LoadAsync(db, owner, proofAttempt, asr, ct);
                var logIds = logs.Values.Select(x => x.Id).ToArray();
                var audioRows = await db.AiCallAudio.AsNoTracking()
                    .Where(x => logIds.Contains(x.LogId))
                    .ToListAsync(ct);
                var audioByLog = audioRows.GroupBy(x => x.LogId)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                foreach (var step in actualSteps)
                {
                    var attempt = step.Current;
                    if (evidenceBySegment.TryGetValue(step.Index, out var proof))
                    {
                        if (proof.RecordingId != step.RecordingId ||
                            proof.RecordingSegment != step.RecordingSegment ||
                            proof.ChunkIndex != step.ChunkIndex ||
                            proof.SourceFileIndex != step.FileIndex ||
                            proof.SourceSha256 != step.Sha256 ||
                            proof.SourceOffsetSamples != step.SourceOffset ||
                            proof.Samples != step.Samples ||
                            string.IsNullOrWhiteSpace(proof.ResultText))
                        {
                            issues.Add(new("asr_evidence_mismatch",
                                "本地/Batch 成功证据与固定分片身份、来源或覆盖范围不一致",
                                step.Path, StepKey: $"asr:{step.Index:D5}"));
                            continue;
                        }
                        transcripts.Add(new(transcripts.Count, step.Index, step.RecordingId,
                            step.RecordingSegment, step.ChunkIndex, step.FileIndex, step.Path,
                            step.Sha256, step.SourceOffset, step.Samples, proof.ResultText, null,
                            proof.Id, proof.ReceiptKind, proof.ProviderHandle));
                        continue;
                    }
                    if (attempt.Status != "Succeeded" || attempt.LogId is null)
                    {
                        issues.Add(new("asr_segment_incomplete", "ASR 分片尚未成功完整",
                            step.Path, StepKey: $"asr:{step.Index:D5}"));
                        continue;
                    }
                    if (!logs.TryGetValue(attempt.RequestId, out var receipt) ||
                        receipt.Id != attempt.LogId || receipt.Status != "succeeded" ||
                        receipt.ResponseText is null ||
                        receipt.ModelProfileId != asr.Request.Consent?.ModelId ||
                        receipt.ModelRevision != asr.Request.Consent?.ModelRevision ||
                        !string.Equals(receipt.ModelEndpoint, asr.Request.Consent?.Endpoint,
                            StringComparison.Ordinal))
                    {
                        issues.Add(new("asr_receipt_mismatch",
                            "ASR 成功片段缺少可核对结果，或回执的类型、模型、revision、地址与固定任务不一致",
                            step.Path, StepKey: $"asr:{step.Index:D5}"));
                        continue;
                    }
                    if (!audioByLog.TryGetValue(receipt.Id, out var storedAudio) ||
                        storedAudio.Length != 1)
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
                        issues.Add(new("asr_audio_mismatch",
                            "ASR 原音频名称、格式、样本大小或保存引用不一致",
                            step.Path, StepKey: $"asr:{step.Index:D5}"));
                        continue;
                    }
                    try
                    {
                        var expectedBytes = await materials.ReadAudioSliceAsync(owner,
                            version.Upload.VersionId, step.FileIndex, step.SourceOffset,
                            step.Samples, ct);
                        var expectedHash = Convert.ToHexString(SHA256.HashData(expectedBytes))
                            .ToLowerInvariant();
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
                    transcripts.Add(new(transcripts.Count, step.Index, step.RecordingId,
                        step.RecordingSegment, step.ChunkIndex, step.FileIndex, step.Path,
                        step.Sha256, step.SourceOffset, step.Samples, receipt.ResponseText, receipt.Id));
                }
            }

            if (!allowAudioContext && asr.Steps.Count == 0)
                issues.Add(new("asr_segment_missing", "ASR 任务没有可核对片段"));
        }

        var snapshot = new NoteDerivedInputSnapshot(owner, version.Upload.NoteId,
            version.Upload.VersionId, asrTaskId,
            asrRevision, inputSha, manifestSha, noteTitle, noteMarkdown, textBlocks, transcripts,
            images, issues) { Audio = audio };
        return new(snapshot, awaitingAsr);
    }

    private async Task<AudioBuild> BuildAudioContextAsync(Guid owner,
        NoteMaterialVersionDto version, JsonElement input, CancellationToken ct)
    {
        var issues = new List<NoteDerivedIssueDto>();
        var items = new List<NoteDerivedAudioInput>();
        var files = version.Upload.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var identities = new HashSet<(Guid RecordingId, int Segment, int Chunk, long Offset)>();
        long totalBytes = 0;
        var ordinal = 0;
        var sliceLimitReached = false;
        var overBudget = false;

        foreach (var recording in input.GetProperty("recordings").EnumerateArray())
        {
            if (!Guid.TryParseExact(recording.GetProperty("id").GetString(), "D",
                    out var recordingId) || recordingId == Guid.Empty)
            {
                issues.Add(new("audio_recording_invalid", "固定材料包含无效录音标识"));
                continue;
            }
            foreach (var chunk in recording.GetProperty("chunks").EnumerateArray())
            {
                var segment = chunk.GetProperty("segment").GetInt32();
                var chunkIndex = chunk.GetProperty("index").GetInt32();
                var samples = chunk.GetProperty("samples").GetInt64();
                var format = NoteAudioFormats.Parse(chunk);
                var path = NoteAudioFormats.Path(recordingId, segment, chunkIndex, format);
                if (segment <= 0 || chunkIndex <= 0 || samples <= 0 ||
                    !files.TryGetValue(path, out var file) || file.Kind != "AudioChunk" ||
                    !NoteAudioFormats.HasValidDeclaredSize(format, file.SizeBytes, samples) ||
                    !NoteMaterialManifestValidator.IsDigest(file.Sha256))
                {
                    issues.Add(new("audio_context_invalid",
                        "固定录音分片身份、样本数或音频文件大小无效", path));
                    continue;
                }

                for (long offset = 0; offset < samples; offset += NoteAsrPlan.SamplesPerCall)
                {
                    if (items.Count >= NoteDerivedAudioContext.MaxSlices)
                    {
                        sliceLimitReached = true;
                        break;
                    }
                    var count = Math.Min((long)NoteAsrPlan.SamplesPerCall, samples - offset);
                    var size = NoteDerivedAudioContext.ByteSize(count);
                    if (!identities.Add((recordingId, segment, chunkIndex, offset)))
                    {
                        issues.Add(new("audio_context_duplicate",
                            "固定录音上下文包含重复片段", path));
                        continue;
                    }
                    items.Add(new(ordinal++, recordingId, segment, chunkIndex, file.Index, path,
                        file.Sha256, offset, count, size,
                        NoteDerivedAudioContext.Name(recordingId, segment, chunkIndex, offset, count)));
                    totalBytes = checked(totalBytes + size);
                    if (totalBytes > NoteDerivedAudioContext.MaxBytes)
                    {
                        overBudget = true;
                        break;
                    }
                }
                if (sliceLimitReached || overBudget) break;
            }
            if (sliceLimitReached || overBudget) break;
        }

        if (sliceLimitReached)
            issues.Add(new("audio_context_slice_limit",
                $"原始录音需要超过 {NoteDerivedAudioContext.MaxSlices} 个上下文片段；未截断、未发送"));
        if (overBudget)
            issues.Add(new("audio_context_too_large",
                $"原始录音上下文合计超过单次 {NoteDerivedAudioContext.MaxBytes} 字节上限；未截断、未发送"));
        if (items.Count == 0 && issues.Count == 0)
            issues.Add(new("audio_context_unavailable",
                "固定材料包含录音，但没有可读取的原始音频上下文；未发送音频"));

        // Only read the fixed files after the metadata budget is known to be admissible.
        if (issues.Count == 0)
        {
            foreach (var path in items.Select(x => x.Path).Distinct(StringComparer.Ordinal))
            {
                var file = files[path];
                try
                {
                    await materials.VerifyAudioFileAsync(owner, version.Upload.VersionId,
                        file.Index, ct);
                }
                catch (DomainException error)
                {
                    issues.Add(new("audio_context_unreadable",
                        "无法读取固定原始录音，未发送音频上下文：" + error.Message, path));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or
                    UnauthorizedAccessException)
                {
                    issues.Add(new("audio_context_unreadable",
                        "无法读取固定原始录音，未发送音频上下文", path));
                }
            }
        }
        return new(items, NoteDerivedCoverage.DistinctIssues(issues));
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private sealed record AudioBuild(IReadOnlyList<NoteDerivedAudioInput> Items,
        IReadOnlyList<NoteDerivedIssueDto> Issues);
}
