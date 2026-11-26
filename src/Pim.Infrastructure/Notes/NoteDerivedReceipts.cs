using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

internal sealed record NoteDerivedReceiptContext(
    IReadOnlyDictionary<Guid, AiRequestLogEntity> LogsById,
    IReadOnlyDictionary<Guid, IReadOnlyList<AiRequestLogEntity>> LogsByRequestId,
    IReadOnlyDictionary<string, NoteDerivedVersionDto> VersionsByStep,
    IReadOnlyDictionary<Guid, AiCallAudioEntity> AudioByLogId,
    NoteAsrState? Asr,
    NoteAsrAttemptEntity? Attempt,
    AiModelProfileEntity? AsrModel,
    IReadOnlyDictionary<int, NoteAsrSegmentEvidenceEntity> AcceptedEvidence);

internal static class NoteDerivedReceipts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static NoteDerivedTaskReceiptDto Build(
        NoteDerivedTaskState state,
        IReadOnlyList<NoteDerivedActualImage> actualImages,
        IReadOnlySet<Guid> completedTranscriptReceiptIds,
        NoteDerivedReceiptContext context,
        IReadOnlyList<NoteDerivedIssueDto> issues)
    {
        var allIssues = NoteDerivedCoverage.DistinctIssues(issues);
        var verifications = new Dictionary<string, StepVerification>(StringComparer.Ordinal);
        var evidence = new List<NoteDerivedEvidence>();
        var audioBatched = state.Steps.Any(x =>
            x.Kind == NoteDerivedStepKind.AudioBatchEvidence);

        foreach (var step in state.Steps.Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var verification = VerifyVisionStep(state, step, context, actualImages, allIssues);
            verifications[step.Key] = verification;
            if (verification.Binding is { Verified: true, AiCallLogId: { } logId } &&
                context.LogsById.TryGetValue(logId, out var log) &&
                !string.IsNullOrWhiteSpace(log.ResponseText))
                evidence.Add(new(step.Key, logId, log.ResponseText, step.Images));
        }

        foreach (var step in state.Steps.Where(x =>
                     x.Kind == NoteDerivedStepKind.AudioBatchEvidence)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var verification = VerifyAudioBatchStep(state, step, context, allIssues);
            verifications[step.Key] = verification;
            if (verification.Binding is { Verified: true, AiCallLogId: { } logId } &&
                context.LogsById.TryGetValue(logId, out var log) &&
                !string.IsNullOrWhiteSpace(log.ResponseText))
                evidence.Add(new(step.Key, logId, log.ResponseText, []));
        }

        var visionComplete = state.Steps
            .Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence)
            .All(x => verifications.TryGetValue(x.Key, out var verified) &&
                verified.Binding is { Verified: true });
        if (!visionComplete)
            AddIssue(allIssues, "vision_evidence_incomplete",
                "视觉来源没有全部形成可核验的实际输入、输出与图片绑定；完整记录不得标记完成");
        var audioComplete = state.Steps
            .Where(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence)
            .All(x => verifications.TryGetValue(x.Key, out var verified) &&
                verified.Binding is { Verified: true });
        if (!audioComplete)
            AddIssue(allIssues, "audio_batch_evidence_incomplete",
                "录音批次没有全部形成可核验的实际输入、输出与审计绑定；完整记录不得标记完成");

        var record = state.Steps.SingleOrDefault(x => x.Kind == NoteDerivedStepKind.RecordSynthesis);
        if (record is not null)
        {
            var prompt = NoteDerivedPrompts.Record(state.Plan.Input, evidence, audioBatched);
            verifications[record.Key] = VerifyTextStep(state, record, context, allIssues, prompt, null);
        }

        var analysis = state.Steps.SingleOrDefault(x => x.Kind == NoteDerivedStepKind.AnalysisSynthesis);
        if (analysis is not null)
        {
            context.VersionsByStep.TryGetValue(NoteDerivedPrompts.RecordKey, out var recordVersion);
            string? prompt = null;
            if (recordVersion is null || string.IsNullOrWhiteSpace(recordVersion.ContentMarkdown))
                AddIssue(allIssues, "analysis_record_missing",
                    "独立分析缺少可读取的固定完整记录版本，无法核验实际组装输入",
                    stepKey: analysis.Key);
            else
                prompt = NoteDerivedPrompts.Analysis(state.Plan.Input, recordVersion.VersionId,
                    recordVersion.ContentMarkdown, evidence, audioBatched);
            verifications[analysis.Key] = VerifyTextStep(state, analysis, context, allIssues, prompt,
                recordVersion?.VersionId);
        }

        var transcriptVerifications = new Dictionary<int, StepVerification>();
        foreach (var transcript in state.Plan.Input.Transcripts.OrderBy(x => x.Ordinal))
            transcriptVerifications[transcript.Ordinal] =
                VerifyTranscript(state, transcript, context, allIssues);

        var recordBinding = record is null ? null :
            verifications.GetValueOrDefault(record.Key)?.Binding;
        var analysisBinding = analysis is null ? null :
            verifications.GetValueOrDefault(analysis.Key)?.Binding;
        var recordIssues = record is null
            ? new List<NoteDerivedIssueDto>()
            : IssuesForStep(allIssues, record.Key);
        var analysisIssues = analysis is null
            ? new List<NoteDerivedIssueDto>()
            : IssuesForStep(allIssues, analysis.Key);

        var sources = new List<NoteDerivedSourceReceiptDto>();
        foreach (var block in state.Plan.Input.TextBlocks.OrderBy(x => x.Ordinal))
        {
            var sourceIssues = IssuesForSource(allIssues, block.BlockId.ToString("D"), "document.md",
                    "markdown_")
                .Concat(recordIssues)
                .ToList();
            var bindings = Bindings(recordBinding);
            sources.Add(new(
                $"text:{block.BlockId:D}",
                NoteDerivedSourceKind.TextBlock,
                block.Ordinal,
                block.BlockId,
                null,
                null,
                Digest(block.Text),
                Encoding.UTF8.GetByteCount(block.Text),
                block.Text.Length,
                null,
                SourceStatus(sourceIssues, bindings),
                bindings,
                Distinct(sourceIssues)));
        }

        foreach (var transcript in state.Plan.Input.Transcripts.OrderBy(x => x.Ordinal))
        {
            var stepKey = $"asr:{transcript.Ordinal + 1:D5}";
            var verification = transcriptVerifications[transcript.Ordinal];
            var sourceIssues = IssuesForSource(allIssues, null, transcript.SourcePath, "asr_")
                .Concat(verification.Issues)
                .Concat(recordIssues)
                .ToList();
            var bindings = Bindings(verification.Binding, recordBinding);
            sources.Add(new(
                $"asr:{transcript.Ordinal:D5}",
                NoteDerivedSourceKind.TranscriptSegment,
                transcript.Ordinal,
                null,
                transcript.SourcePath,
                transcript.SourceSha256,
                Digest(transcript.Text),
                44 + transcript.Samples * 2,
                transcript.Text.Length,
                transcript.ReceiptKind,
                SourceStatus(sourceIssues, bindings),
                bindings,
                Distinct(sourceIssues)));
        }

        var batches = new List<NoteDerivedBatchReceiptDto>();
        foreach (var step in state.Steps.Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var verification = verifications[step.Key];
            var rows = verification.Binding?.AiCallLogId is { } logId
                ? actualImages.Where(x => x.LogId == logId).OrderBy(x => x.Index).ToArray()
                : [];
            var binding = verification.Binding;
            batches.Add(new(
                step.Key,
                StepStatus(step, verification),
                step.Images.OrderBy(x => x.Ordinal).Select(ImageSourceId).ToArray(),
                step.Images.Count,
                rows.Length,
                binding,
                Distinct(verification.Issues)));

            foreach (var image in step.Images.OrderBy(x => x.Ordinal))
            {
                var imageIssues = IssuesForSource(allIssues, null, image.Path, "image_")
                    .Concat(verification.Issues)
                    .ToList();
                var imageBindings = Bindings(binding);
                sources.Add(new(
                    ImageSourceId(image),
                    image.Kind == "Photo" ? NoteDerivedSourceKind.Photo : NoteDerivedSourceKind.InkPreview,
                    image.Ordinal,
                    image.BlockId is { } blockText &&
                    Guid.TryParseExact(blockText, "D", out var blockId) ? blockId : null,
                    image.Path,
                    image.Sha256,
                    null,
                    image.SizeBytes,
                    null,
                    null,
                    SourceStatus(imageIssues, imageBindings),
                    imageBindings,
                    Distinct(imageIssues)));
            }
        }

        foreach (var step in state.Steps.Where(x =>
                     x.Kind == NoteDerivedStepKind.AudioBatchEvidence)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var verification = verifications[step.Key];
            var binding = verification.Binding;
            batches.Add(new(
                step.Key,
                StepStatus(step, verification),
                step.Audio.OrderBy(x => x.Ordinal).Select(AudioSourceId).ToArray(),
                0,
                0,
                binding,
                Distinct(verification.Issues)));

            foreach (var audio in step.Audio.OrderBy(x => x.Ordinal))
            {
                var audioIssues = IssuesForSource(allIssues, null, audio.Path, "audio_")
                    .Concat(verification.Issues)
                    .ToList();
                var audioBindings = Bindings(binding);
                sources.Add(new(
                    AudioSourceId(audio),
                    NoteDerivedSourceKind.AudioSegment,
                    audio.Ordinal,
                    null,
                    audio.Path,
                    audio.SourceSha256,
                    null,
                    audio.SizeBytes,
                    null,
                    null,
                    SourceStatus(audioIssues, audioBindings),
                    audioBindings,
                    Distinct(audioIssues)));
            }
        }

        var steps = state.Steps.OrderBy(x => x.Kind).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(step =>
            {
                var verification = verifications.GetValueOrDefault(step.Key) ??
                    Pending(step, "step_receipt_missing", "步骤尚未形成可核验的回执");
                return new NoteDerivedStepReceiptDto(
                    step.Key,
                    step.Kind,
                    StepStatus(step, verification),
                    step.Current.RequestId == Guid.Empty ? null : step.Current.RequestId,
                    step.Current.LogId,
                    verification.Binding?.EvidenceId,
                    verification.Binding,
                    Distinct(verification.Issues));
            }).ToArray();

        var planned = state.Plan.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var states = state.Steps.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var coverage = NoteDerivedCoverage.Reconcile(state.Plan.Input, planned, states, actualImages,
            completedTranscriptReceiptIds, allIssues);
        var consumed = sources.Count > 0 && sources.All(x => x.Status == "Consumed");
        var complete = consumed && coverage.Complete && steps.Length > 0 &&
            steps.All(x => x.Status == "Consumed") && batches.All(x => x.Status == "Consumed");
        return new(
            state.Request.TaskId,
            state.NoteId,
            state.Request.VersionId,
            state.Request.AsrTaskId,
            state.InputSha256,
            state.ManifestSha256,
            NoteDerivedStore.DescribePhase(state),
            complete,
            steps,
            sources,
            batches,
            Distinct(allIssues));
    }

    private static StepVerification VerifyVisionStep(NoteDerivedTaskState state,
        NoteDerivedStepState step,
        NoteDerivedReceiptContext context,
        IReadOnlyList<NoteDerivedActualImage> actualImages,
        ICollection<NoteDerivedIssueDto> allIssues)
    {
        var local = new List<NoteDerivedIssueDto>();
        if (step.Current.Status != "Succeeded")
            return NotSucceeded(step, "vision");

        var expectedPrompt = NoteDerivedPrompts.ImageEvidence(step.Key, step.Images);
        var expectedInput = VisionFingerprint(state, step, expectedPrompt);
        var expectedOutput = step.Current.ResultSha256;
        var log = FindLog(step.Current.LogId, step.Current.RequestId, context);
        if (log is null)
        {
            AddIssue(local, "vision_receipt_missing",
                "视觉步骤标记成功，但找不到与当前尝试对应的实际调用回执", stepKey: step.Key);
            var missing = EmptyBinding("VisionImage", step, expectedInput, expectedOutput);
            AddIssues(allIssues, local);
            return new(step, missing, local, false);
        }

        CheckCommonLog(local, log, state, step.Current.RequestId, NoteDerivedPrompts.VisionPurpose,
            "vision", step.Key);
        if (step.Current.LogId != log.Id)
            AddIssue(local, "vision_log_id_mismatch",
                "视觉步骤保存的调用记录标识与实际回执不一致", stepKey: step.Key);
        if (expectedInput is null || log.InputHash != expectedInput)
            AddIssue(local, "vision_input_mismatch",
                "视觉调用的实际输入指纹与固定提示、模型目标和图片清单不一致", stepKey: step.Key);
        if (!CompleteVisionWireMetadata(log.MetadataJson))
            AddIssue(local, "vision_wire_incomplete",
                "视觉回执缺少 providerAttempted、requestBodyCaptured、responseBodyComplete 或 imageInputsStored 证据",
                step.Key);
        if (string.IsNullOrWhiteSpace(log.ResponseText))
            AddIssue(local, "vision_output_missing", "视觉成功回执没有可核验正文", step.Key);
        else
        {
            var responseHash = Digest(log.ResponseText);
            if (log.OutputHash != responseHash)
                AddIssue(local, "vision_output_mismatch",
                    "视觉回执的输出摘要与保存正文不一致", stepKey: step.Key);
            if (expectedOutput != responseHash)
                AddIssue(local, "vision_result_mismatch",
                    "视觉步骤保存的结果摘要与实际回执正文不一致", stepKey: step.Key);
        }
        if (!BatchMatchesExactly(step, log.Id, actualImages))
            AddIssue(local, "vision_batch_mismatch",
                "视觉批次的实际图片行与固定计划索引、名称、类型、字节数或 SHA-256 不一致",
                step.Key);

        var verified = local.Count == 0;
        var binding = new NoteDerivedReceiptBindingDto(
            "VisionImage",
            step.Key,
            step.Current.RequestId,
            log.Id,
            null,
            verified ? "Consumed" : "Gap",
            NoteDerivedPrompts.SourceObjectType,
            state.Request.TaskId.ToString("D"),
            log.SourceObjectType,
            log.SourceObjectId,
            expectedInput,
            log.InputHash,
            expectedOutput,
            log.OutputHash,
            verified);
        AddIssues(allIssues, local);
        return new(step, binding, local, verified);
    }

    private static StepVerification VerifyAudioBatchStep(NoteDerivedTaskState state,
        NoteDerivedStepState step,
        NoteDerivedReceiptContext context,
        ICollection<NoteDerivedIssueDto> allIssues)
    {
        var local = new List<NoteDerivedIssueDto>();
        if (step.Current.Status != "Succeeded")
            return NotSucceeded(step, "audio_batch");

        var batchIndex = AudioBatchIndex(step.Key);
        var batchCount = state.Steps.Count(x =>
            x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        var transcripts = state.Plan.Input.Transcripts
            .Where(x => step.TranscriptOrdinals.Contains(x.Ordinal));
        var expectedPrompt = NoteDerivedPrompts.AudioBatch(step.Key, batchIndex, batchCount,
            step.Audio, transcripts);
        var expectedInput = TextFingerprint(state, NoteDerivedPrompts.AudioBatchPurpose,
            expectedPrompt, step.Audio);
        var expectedOutput = step.Current.ResultSha256;
        var log = FindLog(step.Current.LogId, step.Current.RequestId, context);
        if (log is null)
        {
            AddIssue(local, "audio_batch_receipt_missing",
                "录音批次标记成功，但找不到与当前尝试对应的实际调用回执", stepKey: step.Key);
            var missing = EmptyBinding("AudioBatchEvidence", step, expectedInput, expectedOutput);
            AddIssues(allIssues, local);
            return new(step, missing, local, false);
        }

        CheckCommonLog(local, log, state, step.Current.RequestId,
            NoteDerivedPrompts.AudioBatchPurpose, "audio_batch", step.Key);
        if (step.Current.LogId != log.Id)
            AddIssue(local, "audio_batch_log_id_mismatch",
                "录音批次保存的调用记录标识与实际回执不一致", stepKey: step.Key);
        if (log.InputHash != expectedInput)
            AddIssue(local, "audio_batch_input_mismatch",
                "录音批次调用的实际输入指纹与固定提示、模型目标或音频清单不一致",
                step.Key);
        CheckAudioBatchWire(local, log, state, step, step.Key);
        if (string.IsNullOrWhiteSpace(log.ResponseText))
            AddIssue(local, "audio_batch_output_missing",
                "录音批次成功回执没有可核验正文", stepKey: step.Key);
        else
        {
            var responseHash = Digest(log.ResponseText);
            if (log.OutputHash != responseHash)
                AddIssue(local, "audio_batch_output_mismatch",
                    "录音批次回执的输出摘要与保存正文不一致", stepKey: step.Key);
            if (expectedOutput != responseHash)
                AddIssue(local, "audio_batch_result_mismatch",
                    "录音批次步骤保存的结果摘要与实际回执正文不一致", stepKey: step.Key);
            var limit = step.MaxOutputCharacters > 0
                ? step.MaxOutputCharacters : NoteDerivedLimits.MaxEvidenceCharsPerStep;
            if (log.ResponseText.Length > limit)
                AddIssue(local, "audio_batch_output_too_large",
                    "录音批次输出超过本批字符预算；未截断", stepKey: step.Key);
        }

        var verified = local.Count == 0;
        var binding = new NoteDerivedReceiptBindingDto(
            "AudioBatchEvidence",
            step.Key,
            step.Current.RequestId,
            log.Id,
            null,
            verified ? "Consumed" : "Gap",
            NoteDerivedPrompts.SourceObjectType,
            state.Request.TaskId.ToString("D"),
            log.SourceObjectType,
            log.SourceObjectId,
            expectedInput,
            log.InputHash,
            expectedOutput,
            log.OutputHash,
            verified);
        AddIssues(allIssues, local);
        return new(step, binding, local, verified);
    }

    private static void CheckAudioBatchWire(ICollection<NoteDerivedIssueDto> issues,
        AiRequestLogEntity log, NoteDerivedTaskState state, NoteDerivedStepState step,
        string stepKey)
    {
        if (step.Audio.Count is < 1 or > NoteDerivedAudioContext.MaxSlices)
        {
            AddIssue(issues, "audio_batch_wire_incomplete",
                "录音批次计划数量无效，不能证明实际发送内容", stepKey);
            return;
        }
        if (step.Audio.Sum(x => x.SizeBytes) > NoteDerivedAudioContext.MaxBytes)
        {
            AddIssue(issues, "audio_batch_wire_too_large",
                "录音批次计划超过单次 8MiB 上限，不能证明未超限发送", stepKey);
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(log.MetadataJson);
            var root = document.RootElement;
            if (!Flag(root, "providerAttempted") || !Flag(root, "requestBodyCaptured") ||
                !Flag(root, "responseBodyComplete"))
                AddIssue(issues, "audio_batch_wire_incomplete",
                    "录音批次回执缺少 providerAttempted、requestBodyCaptured 或 responseBodyComplete 证据",
                    stepKey);
            var encoding = root.TryGetProperty("audioEncoding", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var referenced = root.TryGetProperty("audioInputsReferenced", out var count) &&
                count.ValueKind == JsonValueKind.Number ? count.GetInt32() : -1;
            var hashes = root.TryGetProperty("audioInputsSha256", out var hashValues) &&
                hashValues.ValueKind == JsonValueKind.Array
                ? hashValues.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .ToArray()
                : [];
            var expected = step.Audio.OrderBy(x => x.Ordinal)
                .Select(x => x.SourceSha256).ToArray();
            if (encoding != "sha256-reference" || referenced != expected.Length ||
                hashes.Length != expected.Length || !hashes.SequenceEqual(expected))
                AddIssue(issues, "audio_batch_wire_mismatch",
                    "录音批次回执缺少与固定分片一致的数量、来源摘要或审计编码", stepKey);
            var owner = ParseGuid(root, "audioContextOwnerId");
            var note = ParseGuid(root, "audioContextNoteId");
            var version = ParseGuid(root, "audioContextSourceVersionId");
            if (owner != state.Owner || note != state.NoteId ||
                version != state.Request.VersionId)
                AddIssue(issues, "audio_batch_wire_identity_mismatch",
                    "录音批次的音频上下文没有绑定当前账号、笔记和固定材料版本", stepKey);
            if (Occurrences(log.RequestMessagesJson, "pim-ai-audio:") < expected.Length ||
                log.RequestMessagesJson.Contains("data:audio/wav;base64,", StringComparison.Ordinal))
                AddIssue(issues, "audio_batch_wire_snapshot_mismatch",
                    "录音批次请求快照没有使用服务端音频引用或包含原始音频数据", stepKey);
        }
        catch (JsonException)
        {
            AddIssue(issues, "audio_batch_wire_incomplete",
                "录音批次回执没有可核对的音频审计元数据", stepKey);
        }
    }

    private static void CheckNoAudioWire(ICollection<NoteDerivedIssueDto> issues,
        AiRequestLogEntity log, string stepKey)
    {
        try
        {
            using var document = JsonDocument.Parse(log.MetadataJson);
            var root = document.RootElement;
            var referenced = root.TryGetProperty("audioInputsReferenced", out var count) &&
                count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0;
            var encoding = root.TryGetProperty("audioEncoding", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (referenced != 0 || !string.IsNullOrEmpty(encoding) ||
                log.RequestMessagesJson.Contains("pim-ai-audio:", StringComparison.Ordinal) ||
                log.RequestMessagesJson.Contains("data:audio/wav;base64,", StringComparison.Ordinal))
                AddIssue(issues, "audio_replayed_in_synthesis",
                    "汇总或分析步骤重复携带了原始音频；长录音必须只引用批次证据", stepKey);
        }
        catch (JsonException)
        {
            AddIssue(issues, "audio_replay_unverifiable",
                "无法核对汇总步骤是否重复携带原始音频", stepKey);
        }
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;

    private static Guid ParseGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var parsed) ? parsed : Guid.Empty;

    private static int AudioBatchIndex(string key)
    {
        var separator = key.LastIndexOf(':');
        return separator >= 0 && int.TryParse(key[(separator + 1)..], out var number)
            ? Math.Max(0, number - 1) : 0;
    }

    private static StepVerification VerifyTextStep(NoteDerivedTaskState state,
        NoteDerivedStepState step,
        NoteDerivedReceiptContext context,
        ICollection<NoteDerivedIssueDto> allIssues,
        string? expectedPrompt,
        Guid? expectedRecordVersionId)
    {
        var local = new List<NoteDerivedIssueDto>();
        if (step.Current.Status != "Succeeded")
            return NotSucceeded(step, "synthesis");

        var purpose = step.Kind == NoteDerivedStepKind.RecordSynthesis
            ? NoteDerivedPrompts.RecordPurpose
            : NoteDerivedPrompts.AnalysisPurpose;
        var stage = step.Kind == NoteDerivedStepKind.RecordSynthesis
            ? "RecordSynthesis"
            : "AnalysisSynthesis";
        var audioBatched = state.Steps.Any(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        var audio = step.Audio.Count > 0
            ? step.Audio
            : audioBatched ? [] : state.Plan.Input.Audio;
        var expectedInput = expectedPrompt is null ? null : TextFingerprint(state, purpose,
            expectedPrompt, audio);
        var log = FindLog(step.Current.LogId, step.Current.RequestId, context);
        context.VersionsByStep.TryGetValue(step.Key, out var version);
        var expectedOutput = version?.ContentSha256 ?? step.Current.ResultSha256;
        if (log is null)
        {
            AddIssue(local, "synthesis_receipt_missing",
                "合成步骤标记成功，但找不到与当前尝试对应的实际调用回执", stepKey: step.Key);
            var missing = EmptyBinding(stage, step, expectedInput, expectedOutput);
            AddIssues(allIssues, local);
            return new(step, missing, local, false);
        }

        CheckCommonLog(local, log, state, step.Current.RequestId, purpose, "synthesis", step.Key);
        if (audio.Count > 0)
            CheckAudioWire(local, log, state, audio, step.Key);
        else if (state.Plan.Input.Audio.Count > 0)
            CheckNoAudioWire(local, log, step.Key);
        if (step.Current.LogId != log.Id)
            AddIssue(local, "synthesis_log_id_mismatch",
                "合成步骤保存的调用记录标识与实际回执不一致", stepKey: step.Key);
        if (expectedInput is null)
            AddIssue(local, "synthesis_input_missing",
                "无法重建合成步骤的固定输入，不能证明实际发送内容", stepKey: step.Key);
        else if (log.InputHash != expectedInput)
            AddIssue(local, "synthesis_input_mismatch",
                "合成调用的实际输入指纹与固定来源、固定记录版本或模型目标不一致",
                step.Key);
        if (version is null)
            AddIssue(local, "synthesis_result_version_missing",
                "合成步骤标记成功，但找不到对应固定结果版本", stepKey: step.Key);
        else
        {
            if (version.GenerationTaskId != state.Request.TaskId ||
                version.SourceVersionId != state.Request.VersionId ||
                version.InputSha256 != state.InputSha256 ||
                version.ManifestSha256 != state.ManifestSha256 ||
                version.AiCallLogId != log.Id ||
                version.ModelProfileId != state.Request.Consent.ModelId ||
                version.ModelRevision != state.Request.Consent.ModelRevision ||
                !string.Equals(version.ModelEndpoint, state.Request.Consent.Endpoint,
                    StringComparison.Ordinal))
                AddIssue(local, "synthesis_result_identity_mismatch",
                    "固定结果版本的来源、调用记录或模型目标与当前任务不一致",
                    step.Key);
            if (step.Kind == NoteDerivedStepKind.AnalysisSynthesis &&
                version.SourceCompleteRecordVersionId != expectedRecordVersionId)
                AddIssue(local, "analysis_record_binding_mismatch",
                    "独立分析结果没有绑定已核验的完整记录固定版本", step.Key);
            if (string.IsNullOrWhiteSpace(log.ResponseText) ||
                !string.Equals(version.ContentMarkdown, log.ResponseText, StringComparison.Ordinal))
                AddIssue(local, "synthesis_result_mismatch",
                    "固定结果正文与实际成功回执正文不一致", step.Key);
        }
        if (string.IsNullOrWhiteSpace(log.ResponseText))
            AddIssue(local, "synthesis_output_missing", "合成成功回执没有可核验正文", step.Key);
        else
        {
            var responseHash = Digest(log.ResponseText);
            if (log.OutputHash != responseHash)
                AddIssue(local, "synthesis_output_mismatch",
                    "合成回执的输出摘要与保存正文不一致", stepKey: step.Key);
            if (expectedOutput != responseHash)
                AddIssue(local, "synthesis_result_hash_mismatch",
                    "固定结果版本摘要与实际成功回执正文不一致", stepKey: step.Key);
            if (step.Current.ResultSha256 != responseHash)
                AddIssue(local, "synthesis_step_result_mismatch",
                    "步骤检查点保存的结果摘要与实际成功回执正文不一致", stepKey: step.Key);
        }

        var verified = local.Count == 0;
        var binding = new NoteDerivedReceiptBindingDto(
            stage,
            step.Key,
            step.Current.RequestId,
            log.Id,
            null,
            verified ? "Consumed" : "Gap",
            NoteDerivedPrompts.SourceObjectType,
            state.Request.TaskId.ToString("D"),
            log.SourceObjectType,
            log.SourceObjectId,
            expectedInput,
            log.InputHash,
            expectedOutput,
            log.OutputHash,
            verified);
        AddIssues(allIssues, local);
        return new(step, binding, local, verified);
    }

    private static StepVerification VerifyTranscript(NoteDerivedTaskState state,
        NoteDerivedTranscriptInput transcript,
        NoteDerivedReceiptContext context,
        ICollection<NoteDerivedIssueDto> allIssues)
    {
        var stepKey = $"asr:{transcript.Ordinal + 1:D5}";
        var local = new List<NoteDerivedIssueDto>();
        var asrStep = context.Asr?.Steps.SingleOrDefault(x => x.Index == transcript.AsrSegmentIndex);
        if (transcript.EvidenceId is { } evidenceId)
        {
            if (!context.AcceptedEvidence.TryGetValue(transcript.AsrSegmentIndex, out var proof) ||
                proof.Id != evidenceId)
            {
                AddIssue(local, "asr_evidence_missing",
                    "ASR 片段缺少当前 owner、attempt 与固定来源对应的成功证据", stepKey: stepKey);
                var missing = EvidenceBinding(transcript, stepKey, null, null, null);
                AddIssues(allIssues, local);
                return new(new NoteDerivedStepState { Key = stepKey, Kind = NoteDerivedStepKind.RecordSynthesis },
                    missing, local, false);
            }

            var evidenceInput = EvidenceInputFingerprint(state, transcript, proof);
            var evidenceOutput = Digest(transcript.Text);
            if (proof.OwnerId != state.Owner || proof.TaskId != state.Request.AsrTaskId ||
                proof.VersionId != state.Request.VersionId ||
                context.Attempt is null || proof.AttemptId != context.Attempt.Id)
                AddIssue(local, "asr_owner_or_attempt_mismatch",
                    "ASR 证据不属于当前 owner、任务、固定版本或 attempt", stepKey);
            if (!NoteAsrAcceptedEvidence.MatchesStep(proof,
                    asrStep ?? throw new InvalidDataException("ASR step disappeared")) ||
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
                AddIssue(local, "asr_evidence_mismatch",
                    "ASR 证据的分片身份、来源范围、提供方或结果与固定快照不一致",
                    stepKey);
            if (proof.ResultSha256 != evidenceOutput)
                AddIssue(local, "asr_output_mismatch",
                    "ASR 证据结果摘要与固定转写正文不一致", stepKey);
            var evidenceVerified = local.Count == 0;
            var evidenceBinding = EvidenceBinding(transcript, stepKey, proof.Id, evidenceInput,
                evidenceOutput, evidenceVerified ? "Consumed" : "Gap", proof.ResultSha256,
                evidenceVerified);
            AddIssues(allIssues, local);
            return new(new NoteDerivedStepState { Key = stepKey, Kind = NoteDerivedStepKind.RecordSynthesis },
                evidenceBinding, local, evidenceVerified);
        }

        var log = FindLog(transcript.LogId, asrStep?.Current.RequestId, context);
        if (log is null)
        {
            AddIssue(local, "asr_receipt_missing",
                "ASR 片段缺少当前 owner、任务、分片与提示词对应的成功回执",
                stepKey);
            var missing = EvidenceBinding(transcript, stepKey, null, null, null);
            AddIssues(allIssues, local);
            return new(new NoteDerivedStepState { Key = stepKey, Kind = NoteDerivedStepKind.RecordSynthesis },
                missing, local, false);
        }

        var expectedInput = AsrFingerprint(state, context.Asr, context.Attempt, context.AsrModel,
            context.AudioByLogId.GetValueOrDefault(log.Id), log);
        var expectedOutput = Digest(transcript.Text);
        CheckAsrLog(local, log, state, transcript, asrStep, stepKey);
        var audio = context.AudioByLogId.GetValueOrDefault(log.Id);
        if (audio is null)
            AddIssue(local, "asr_audio_missing",
                "ASR 成功回执没有唯一可核验的原音频记录", stepKey);
        else if (audio.LogId != log.Id || audio.Name != $"segment-{transcript.AsrSegmentIndex + 1:D5}.wav" ||
                 audio.Format != "wav" || audio.SizeBytes != 44 + transcript.Samples * 2 ||
                 string.IsNullOrWhiteSpace(audio.ObjectKey))
            AddIssue(local, "asr_audio_mismatch",
                "ASR 原音频记录的名称、格式、大小或保存引用与固定片段不一致", stepKey);
        if (expectedInput is null)
            AddIssue(local, "asr_input_unverifiable",
                "ASR attempt 或原音频记录不完整，无法重建固定请求指纹", stepKey);
        else if (log.InputHash != expectedInput)
            AddIssue(local, "asr_input_mismatch",
                "ASR 实际输入指纹与固定分片、提示词、语言和模型目标不一致",
                stepKey);
        if (string.IsNullOrWhiteSpace(log.ResponseText))
            AddIssue(local, "asr_output_missing", "ASR 成功回执没有可核验正文", stepKey);
        else
        {
            if (!string.Equals(log.ResponseText, transcript.Text, StringComparison.Ordinal))
                AddIssue(local, "asr_result_mismatch",
                    "ASR 成功回执正文与固定转写快照不一致", stepKey);
            if (log.OutputHash != expectedOutput)
                AddIssue(local, "asr_output_mismatch",
                    "ASR 成功回执输出摘要与固定转写正文不一致", stepKey);
        }
        var verified = local.Count == 0;
        var binding = EvidenceBinding(transcript, stepKey, null, expectedInput, expectedOutput,
            verified ? "Consumed" : "Gap", log.OutputHash, verified, log);
        AddIssues(allIssues, local);
        return new(new NoteDerivedStepState { Key = stepKey, Kind = NoteDerivedStepKind.RecordSynthesis },
            binding, local, verified);
    }

    private static void CheckCommonLog(ICollection<NoteDerivedIssueDto> issues,
        AiRequestLogEntity log,
        NoteDerivedTaskState state,
        Guid requestId,
        string purpose,
        string prefix,
        string stepKey)
    {
        if (log.UserId != state.Owner)
            AddIssue(issues, $"{prefix}_owner_mismatch",
                "实际调用回执不属于当前任务 owner", stepKey);
        if (log.ClientRequestId != requestId)
            AddIssue(issues, $"{prefix}_request_mismatch",
                "实际调用回执的 ClientRequestId 与当前步骤尝试不一致", stepKey);
        if (log.Status != "succeeded")
            AddIssue(issues, $"{prefix}_status_mismatch",
                "步骤标记成功，但实际调用回执不是成功状态", stepKey);
        if (log.Purpose != purpose)
            AddIssue(issues, $"{prefix}_purpose_mismatch",
                "实际调用用途与步骤类型不一致", stepKey);
        if (log.SourceObjectType != NoteDerivedPrompts.SourceObjectType ||
            log.SourceObjectId != state.Request.TaskId.ToString("D"))
            AddIssue(issues, $"{prefix}_source_mismatch",
                "实际调用来源对象不是当前派生任务", stepKey);
        if (log.ModelProfileId != state.Request.Consent.ModelId ||
            log.ModelRevision != state.Request.Consent.ModelRevision ||
            !string.Equals(log.ModelEndpoint, state.Request.Consent.Endpoint, StringComparison.Ordinal))
            AddIssue(issues, $"{prefix}_model_mismatch",
                "实际调用模型、revision 或目标地址与固定同意不一致", stepKey);
    }

    private static void CheckAsrLog(ICollection<NoteDerivedIssueDto> issues,
        AiRequestLogEntity log,
        NoteDerivedTaskState state,
        NoteDerivedTranscriptInput transcript,
        NoteAsrStep? asrStep,
        string stepKey)
    {
        if (log.UserId != state.Owner)
            AddIssue(issues, "asr_owner_mismatch", "ASR 回执不属于当前任务 owner", stepKey);
        if (asrStep is null || log.ClientRequestId != asrStep.Current.RequestId ||
            transcript.LogId != asrStep.Current.LogId)
            AddIssue(issues, "asr_request_mismatch",
                "ASR 回执的 ClientRequestId 或 LogId 与固定分片尝试不一致", stepKey);
        if (log.Status != "succeeded")
            AddIssue(issues, "asr_status_mismatch",
                "固定分片标记成功，但 ASR 回执不是成功状态", stepKey);
        if (log.Purpose != "ai.asr-call")
            AddIssue(issues, "asr_purpose_mismatch", "ASR 回执用途不是 ai.asr-call", stepKey);
        if (log.SourceObjectType != "native-note-asr-task" ||
            log.SourceObjectId != state.Request.AsrTaskId?.ToString("D"))
            AddIssue(issues, "asr_source_mismatch", "ASR 回执来源不是当前固定 ASR 任务", stepKey);
    }

    private static string? AsrFingerprint(NoteDerivedTaskState state, NoteAsrState? asr,
        NoteAsrAttemptEntity? attempt, AiModelProfileEntity? model, AiCallAudioEntity? audio,
        AiRequestLogEntity log)
    {
        if (asr is null || audio is null || string.IsNullOrWhiteSpace(audio.Name) ||
            string.IsNullOrWhiteSpace(audio.Format) || string.IsNullOrWhiteSpace(audio.Sha256) ||
            asr.Request.Consent is not { } consent ||
            consent.ModelId == Guid.Empty || string.IsNullOrWhiteSpace(consent.Endpoint) ||
            string.IsNullOrWhiteSpace(consent.PresetId) || string.IsNullOrWhiteSpace(consent.PresetVersion))
            return null;

        string? promptRevision;
        string? promptSha256;
        if (log.ModelProfileId != consent.ModelId || log.ModelRevision != consent.ModelRevision ||
            log.ModelEndpoint != consent.Endpoint)
            return null;
        if (attempt is not null)
        {
            if (attempt.OwnerId != state.Owner || attempt.TaskId != asr.Request.TaskId ||
                attempt.VersionId != state.Request.VersionId || attempt.ModelId != consent.ModelId ||
                attempt.ModelRevision != consent.ModelRevision || attempt.Endpoint != consent.Endpoint ||
                attempt.PresetId != consent.PresetId || attempt.PresetVersion != consent.PresetVersion ||
                attempt.Language != asr.Request.Language)
                return null;
            promptRevision = attempt.PromptRevision;
            promptSha256 = attempt.PromptSha256;
        }
        else
        {
            // Legacy calls have no routing-attempt snapshot. A matching immutable profile
            // revision pins the prompt configuration; the reconstructed fingerprint must match.
            if (model is null || model.Id != consent.ModelId || model.Revision != consent.ModelRevision)
                return null;
            var prompt = AiAsrPromptTemplate.Normalize(model.AsrPrompt).Snapshot(asr.Request.Language);
            promptRevision = prompt.Revision;
            promptSha256 = prompt.Sha256;
        }
        if (string.IsNullOrWhiteSpace(promptRevision) || string.IsNullOrWhiteSpace(promptSha256))
            return null;

        return Hash(JsonSerializer.Serialize(new
        {
            kind = "asr",
            ModelId = (Guid?)consent.ModelId,
            Language = asr.Request.Language,
            consent,
            Revision = promptRevision,
            promptSha256,
            audio.Name,
            audio.Format,
            audio.Sha256,
            size = audio.SizeBytes
        }, Json));
    }

    private static string? VisionFingerprint(NoteDerivedTaskState state,
        NoteDerivedStepState step,
        string prompt)
    {
        var consent = new AiVisionDestinationConsent(state.Request.Consent.ModelId,
            state.Request.Consent.ModelRevision, state.Request.Consent.Endpoint, true);
        return Hash(JsonSerializer.Serialize(new
        {
            kind = "vision",
            sourceObjectType = NoteDerivedPrompts.SourceObjectType,
            sourceObjectId = state.Request.TaskId.ToString("D"),
            purpose = NoteDerivedPrompts.VisionPurpose,
            ModelId = (Guid?)state.Request.Consent.ModelId,
            SystemPrompt = (string?)null,
            Prompt = prompt,
            consent,
            images = step.Images.OrderBy(x => x.Ordinal).Select(x => new
            {
                Name = NoteDerivedPrompts.ImageName(x),
                x.ContentType,
                x.Sha256,
                size = x.SizeBytes
            })
        }, Json));
    }

    private static string TextFingerprint(NoteDerivedTaskState state, string purpose, string prompt,
        IReadOnlyList<NoteDerivedAudioInput>? audio = null)
    {
        var consent = new AiVisionDestinationConsent(state.Request.Consent.ModelId,
            state.Request.Consent.ModelRevision, state.Request.Consent.Endpoint, true);
        if (audio is { Count: > 0 })
            return Hash(JsonSerializer.Serialize(new
            {
                kind = "audio",
                sourceObjectType = NoteDerivedPrompts.SourceObjectType,
                sourceObjectId = state.Request.TaskId.ToString("D"),
                purpose,
                ModelId = (Guid?)state.Request.Consent.ModelId,
                SystemPrompt = (string?)null,
                Prompt = prompt,
                consent,
                audio = audio.OrderBy(x => x.Ordinal).Select(x => new
                {
                    x.Name,
                    ContentType = "audio/wav",
                    x.SourceSha256,
                    size = x.SizeBytes,
                    OwnerId = state.Owner,
                    NoteId = state.NoteId,
                    SourceVersionId = state.Request.VersionId,
                    x.RecordingId,
                    x.RecordingSegment,
                    x.ChunkIndex,
                    x.FileIndex,
                    x.SourceOffsetSamples,
                    x.Samples,
                    SourcePath = x.Path
                })
            }, Json));
        return Hash(JsonSerializer.Serialize(new
        {
            kind = "text",
            sourceObjectType = NoteDerivedPrompts.SourceObjectType,
            sourceObjectId = state.Request.TaskId.ToString("D"),
            purpose,
            ModelId = (Guid?)state.Request.Consent.ModelId,
            SystemPrompt = (string?)null,
            Prompt = prompt,
            consent
        }, Json));
    }

    private static void CheckAudioWire(ICollection<NoteDerivedIssueDto> issues,
        AiRequestLogEntity log, NoteDerivedTaskState state,
        IReadOnlyList<NoteDerivedAudioInput> planned, string stepKey)
    {
        if (planned.Count == 0) return;
        try
        {
            using var document = JsonDocument.Parse(log.MetadataJson);
            var root = document.RootElement;
            var encoding = root.TryGetProperty("audioEncoding", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var referenced = root.TryGetProperty("audioInputsReferenced", out var count) &&
                count.ValueKind == JsonValueKind.Number ? count.GetInt32() : -1;
            var hashes = root.TryGetProperty("audioInputsSha256", out var hashValues) &&
                hashValues.ValueKind == JsonValueKind.Array
                ? hashValues.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .ToArray()
                : [];
            var expected = planned.OrderBy(x => x.Ordinal).Select(x => x.SourceSha256).ToArray();
            if (encoding != "sha256-reference" || referenced != planned.Count ||
                hashes.Length != expected.Length || !hashes.SequenceEqual(expected))
                AddIssue(issues, "audio_wire_incomplete",
                    "合成回执缺少与固定录音清单一致的数量、来源摘要或审计编码证据", stepKey);
            var owner = root.TryGetProperty("audioContextOwnerId", out var ownerValue) &&
                ownerValue.ValueKind == JsonValueKind.String &&
                Guid.TryParse(ownerValue.GetString(), out var parsedOwner) ? parsedOwner : Guid.Empty;
            var note = root.TryGetProperty("audioContextNoteId", out var noteValue) &&
                noteValue.ValueKind == JsonValueKind.String &&
                Guid.TryParse(noteValue.GetString(), out var parsedNote) ? parsedNote : Guid.Empty;
            var version = root.TryGetProperty("audioContextSourceVersionId", out var versionValue) &&
                versionValue.ValueKind == JsonValueKind.String &&
                Guid.TryParse(versionValue.GetString(), out var parsedVersion) ? parsedVersion : Guid.Empty;
            if (owner != state.Owner || note != state.NoteId ||
                version != state.Request.VersionId)
                AddIssue(issues, "audio_wire_identity_mismatch",
                    "合成回执的音频上下文没有绑定当前账号、笔记和固定材料版本", stepKey);
            if (Occurrences(log.RequestMessagesJson, "pim-ai-audio:") < planned.Count ||
                log.RequestMessagesJson.Contains("data:audio/wav;base64,", StringComparison.Ordinal))
                AddIssue(issues, "audio_wire_mismatch",
                    "合成回执的请求快照没有使用服务端音频引用或包含原始音频数据", stepKey);
        }
        catch (JsonException)
        {
            AddIssue(issues, "audio_wire_incomplete",
                "合成回执没有可核对的音频审计元数据", stepKey);
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string EvidenceInputFingerprint(NoteDerivedTaskState state,
        NoteDerivedTranscriptInput transcript,
        NoteAsrSegmentEvidenceEntity proof) =>
        Hash(JsonSerializer.Serialize(new
        {
            kind = "asr-segment-evidence",
            taskId = state.Request.AsrTaskId,
            proof.Id,
            proof.AttemptId,
            proof.SegmentIndex,
            proof.RecordingId,
            proof.RecordingSegment,
            proof.ChunkIndex,
            proof.SourceFileIndex,
            proof.SourceSha256,
            proof.SourceOffsetSamples,
            proof.Samples,
            proof.ReceiptKind,
            proof.ProviderHandle,
            transcriptPath = transcript.SourcePath
        }, Json));

    private static NoteDerivedReceiptBindingDto EvidenceBinding(
        NoteDerivedTranscriptInput transcript,
        string stepKey,
        Guid? evidenceId,
        string? expectedInput,
        string? expectedOutput,
        string status = "Gap",
        string? observedOutput = null,
        bool verified = false,
        AiRequestLogEntity? log = null) =>
        new(
            evidenceId is null ? "AsrReceipt" : "AsrEvidence",
            stepKey,
            log?.ClientRequestId ?? (evidenceId is null ? transcript.LogId : null),
            log?.Id,
            evidenceId,
            status,
            log is null ? "note-asr-segment-evidence" : "native-note-asr-task",
            log is null ? evidenceId?.ToString("D") : transcript.LogId is null
                ? null : "native-note-asr-task",
            log?.SourceObjectType,
            log?.SourceObjectId,
            expectedInput,
            log?.InputHash ?? (evidenceId is null ? null : expectedInput),
            expectedOutput,
            observedOutput,
            verified);

    private static AiRequestLogEntity? FindLog(Guid? logId, Guid? requestId,
        NoteDerivedReceiptContext context)
    {
        if (logId is { } id && context.LogsById.TryGetValue(id, out var byId)) return byId;
        if (requestId is not { } request || !context.LogsByRequestId.TryGetValue(request, out var rows))
            return null;
        return rows.OrderBy(x => x.UserId).ThenBy(x => x.Id).FirstOrDefault();
    }

    private static NoteDerivedReceiptBindingDto EmptyBinding(string stage,
        NoteDerivedStepState step,
        string? expectedInput,
        string? expectedOutput) =>
        new(stage, step.Key, step.Current.RequestId, step.Current.LogId, null, "Gap",
            NoteDerivedPrompts.SourceObjectType, null, null, null,
            expectedInput, null, expectedOutput, null, false);

    private static StepVerification NotSucceeded(NoteDerivedStepState step, string prefix)
    {
        var terminal = step.Current.Status is "Failed" or "Blocked" or "Unknown" or "Cancelled";
        var issues = terminal
            ? new List<NoteDerivedIssueDto>
            {
                new(step.Current.ErrorCode ?? $"{prefix}_step_failed",
                    step.Current.ErrorMessage ?? "步骤未成功，不能形成完整回执",
                    StepKey: step.Key)
            }
            : [];
        return new(step, null, issues, false);
    }

    private static StepVerification Pending(NoteDerivedStepState step, string code, string message) =>
        new(step, null,
            step.Current.Status is "Queued" or "Running"
                ? []
                : [new(code, message, StepKey: step.Key)],
            false);

    private static string StepStatus(NoteDerivedStepState step, StepVerification verification)
    {
        if (verification.Verified && verification.Binding is { Verified: true }) return "Consumed";
        if (step.Current.Status is "Queued" or "Running" && verification.Issues.Count == 0)
            return "Pending";
        return "Gap";
    }

    private static string SourceStatus(IReadOnlyCollection<NoteDerivedIssueDto> issues,
        IReadOnlyCollection<NoteDerivedReceiptBindingDto> bindings)
    {
        if (issues.Count > 0 || bindings.Any(x => !x.Verified)) return "Gap";
        if (bindings.Count > 0 && bindings.All(x => x.Verified)) return "Consumed";
        return "Pending";
    }

    private static IReadOnlyList<NoteDerivedReceiptBindingDto> Bindings(
        params NoteDerivedReceiptBindingDto?[] values) =>
        values.Where(x => x is not null).Cast<NoteDerivedReceiptBindingDto>().ToArray();

    private static IReadOnlyList<NoteDerivedIssueDto> IssuesForStep(
        IEnumerable<NoteDerivedIssueDto> issues, string stepKey) =>
        issues.Where(x => x.StepKey == stepKey).ToArray();

    private static IReadOnlyList<NoteDerivedIssueDto> IssuesForSource(
        IEnumerable<NoteDerivedIssueDto> issues,
        string? blockId,
        string? sourcePath,
        string codePrefix) =>
        issues.Where(x =>
            blockId is not null && x.BlockId == blockId ||
            sourcePath is not null && x.SourcePath == sourcePath ||
            x.Code.StartsWith(codePrefix, StringComparison.Ordinal)).ToArray();

    private static bool BatchMatchesExactly(NoteDerivedStepState step,
        Guid logId,
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
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return Flag(root, "providerAttempted") && Flag(root, "requestBodyCaptured") &&
                Flag(root, "responseBodyComplete") && Flag(root, "imageInputsStored");
        }
        catch (JsonException) { return false; }

        static bool Flag(JsonElement root, string name) =>
            root.TryGetProperty(name, out var item) &&
            item.ValueKind == JsonValueKind.True;
    }

    private static void AddIssue(ICollection<NoteDerivedIssueDto> issues,
        string code,
        string message,
        string? stepKey = null)
    {
        if (issues.Any(x => x.Code == code && x.StepKey == stepKey)) return;
        issues.Add(new(code, message, StepKey: stepKey));
    }

    private static void AddIssues(ICollection<NoteDerivedIssueDto> target,
        IEnumerable<NoteDerivedIssueDto> source)
    {
        foreach (var issue in source) AddIssue(target, issue.Code, issue.Message, issue.StepKey);
    }

    private static IReadOnlyList<NoteDerivedIssueDto> Distinct(
        IEnumerable<NoteDerivedIssueDto> issues) =>
        NoteDerivedCoverage.DistinctIssues(issues);

    private static string ImageSourceId(NoteDerivedImageInput image) => $"image:{image.Ordinal:D4}";
    private static string AudioSourceId(NoteDerivedAudioInput audio) =>
        $"audio:{audio.RecordingId:D}:s{audio.RecordingSegment:D4}:c{audio.ChunkIndex:D6}:" +
        $"o{audio.SourceOffsetSamples:D012}:n{audio.Samples}";
    private static string Digest(string value) => Hash(value);
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record StepVerification(
        NoteDerivedStepState Step,
        NoteDerivedReceiptBindingDto? Binding,
        IReadOnlyList<NoteDerivedIssueDto> Issues,
        bool Verified);
}
