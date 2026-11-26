using System.Text;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

internal sealed class NoteDerivedPlanner(NoteDerivedAudioContextOptions? audioContext = null)
    : INoteDerivedPlanner
{
    private readonly NoteDerivedAudioContextOptions _audio = audioContext ?? new();

    public NoteDerivedPlan Build(NoteDerivedInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = input.Issues.ToList();
        var steps = new List<NoteDerivedPlanStep>();
        var batches = BuildImageBatches(input.Images, issues);
        foreach (var (index, images) in batches.Select((value, index) => (index, value)))
        {
            var key = NoteDerivedPrompts.ImageBatchKey(index);
            var prompt = NoteDerivedPrompts.ImageEvidence(key, images);
            steps.Add(new(key, NoteDerivedStepKind.ImageEvidence, [], images, prompt.Length));
        }

        ValidateTranscripts(input.Transcripts, issues);
        ValidateAudio(input.Audio, issues);
        var imageKeys = steps.Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence)
            .Select(x => x.Key).ToArray();
        var audioBatchMode = input.Audio.Count > 0 && !FitsSingleAudioCall(input.Audio);
        var audioKeys = new List<string>();
        if (audioBatchMode)
        {
            var audioBatches = BuildAudioBatches(input, issues);
            var coveredTranscripts = audioBatches
                .SelectMany(x => x.TranscriptOrdinals ?? [])
                .ToHashSet();
            var unassigned = input.Transcripts
                .Where(x => !coveredTranscripts.Contains(x.Ordinal))
                .OrderBy(x => x.Ordinal)
                .ToArray();
            if (audioBatches.Count > 0 && unassigned.Length > 0)
            {
                issues.Add(new("audio_transcript_unassigned",
                    $"有 {unassigned.Length} 段成功 ASR 转写无法按录音与时间范围归入任何录音批次；未截断、未发送",
                    unassigned[0].SourcePath));
                audioBatches = [];
            }
            foreach (var batch in audioBatches)
            {
                audioKeys.Add(batch.Key);
                steps.Add(batch);
            }
        }
        var recordPrompt = NoteDerivedPrompts.Record(input, [], audioBatchMode);
        if (recordPrompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
            issues.Add(new("record_input_too_large",
                $"完整记录基础输入为 {recordPrompt.Length} 字符，超过 SDK 实际输入上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                StepKey: NoteDerivedPrompts.RecordKey));
        steps.Add(new(NoteDerivedPrompts.RecordKey, NoteDerivedStepKind.RecordSynthesis,
            imageKeys.Concat(audioKeys).ToArray(), [], recordPrompt.Length,
            audioBatchMode ? [] : input.Audio));

        var analysisPrompt = NoteDerivedPrompts.Analysis(input, Guid.Empty, "", [], audioBatchMode);
        if (analysisPrompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
            issues.Add(new("analysis_input_too_large",
                $"独立分析基础输入为 {analysisPrompt.Length} 字符，超过 SDK 实际输入上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                StepKey: NoteDerivedPrompts.AnalysisKey));
        steps.Add(new(NoteDerivedPrompts.AnalysisKey, NoteDerivedStepKind.AnalysisSynthesis,
            [NoteDerivedPrompts.RecordKey], [], analysisPrompt.Length,
            audioBatchMode ? [] : input.Audio));

        if (steps.Count > NoteDerivedLimits.MaxEvidenceSteps)
            issues.Add(new("evidence_step_limit",
                $"派生出 {steps.Count} 个步骤，超过上限 {NoteDerivedLimits.MaxEvidenceSteps}；未截断材料"));

        issues = DistinctIssues(issues);
        var coverage = new NoteDerivedCoverageDto(
            input.TextBlocks.Count,
            input.Transcripts.Count,
            input.Transcripts.Count,
            input.Images.Count(x => x.Kind == "Photo"),
            0,
            input.Images.Count(x => x.Kind == "InkPreview"),
            0,
            steps.Count(x => x.Kind == NoteDerivedStepKind.ImageEvidence),
            0,
            false,
            issues);

        return new(NoteDerivedLimits.PlanVersion, input, steps, coverage);
    }

    private bool FitsSingleAudioCall(IReadOnlyList<NoteDerivedAudioInput> audio)
    {
        if (audio.Count == 0 || audio.Count > NoteDerivedAudioContext.MaxSlices) return false;
        long total = 0;
        foreach (var item in audio)
        {
            if (item.SizeBytes > NoteDerivedAudioContext.MaxBytes) return false;
            if (total > long.MaxValue - item.SizeBytes) return false;
            total += item.SizeBytes;
            if (total > Math.Min(NoteDerivedAudioContext.MaxBytes, MaxBatchBytes)) return false;
        }
        return true;
    }

    private int MaxBatchBytes => Math.Clamp(_audio.MaxBatchBytes, 1024,
        NoteDerivedAudioContext.MaxBytes);
    private int MaxBatches => Math.Clamp(_audio.MaxBatches, 1,
        NoteDerivedAudioContextOptions.HardMaxBatches);
    private long MaxTotalBytes => Math.Clamp(_audio.MaxTotalBytes,
        NoteDerivedAudioContext.MaxBytes, NoteDerivedAudioContextOptions.HardMaxTotalBytes);

    private List<NoteDerivedPlanStep> BuildAudioBatches(NoteDerivedInputSnapshot input,
        List<NoteDerivedIssueDto> issues)
    {
        var ordered = input.Audio.OrderBy(x => x.Ordinal).ToArray();
        var batches = new List<List<NoteDerivedAudioInput>>();
        var current = new List<NoteDerivedAudioInput>();
        long currentBytes = 0;
        foreach (var slice in ordered)
        {
            if (slice.SizeBytes is < 1 or > NoteDerivedAudioContext.MaxBytes) continue;
            if (slice.SizeBytes > MaxBatchBytes)
            {
                issues.Add(new("audio_batch_slice_too_large",
                    $"单个原始录音片段为 {slice.SizeBytes} 字节，超过单批 {MaxBatchBytes} 字节预算；未截断、未发送",
                    slice.Path));
                return [];
            }
            if (current.Count > 0 && (current.Count >= NoteDerivedAudioContext.MaxSlices ||
                currentBytes + slice.SizeBytes > MaxBatchBytes))
            {
                batches.Add(current);
                current = [];
                currentBytes = 0;
            }
            current.Add(slice);
            currentBytes = checked(currentBytes + slice.SizeBytes);
        }
        if (current.Count > 0) batches.Add(current);

        if (batches.Count > MaxBatches)
        {
            issues.Add(new("audio_batch_limit",
                $"原始录音需要 {batches.Count} 个 provider 批次，超过上限 {MaxBatches}；未截断、未发送"));
            return [];
        }

        var skeleton = NoteDerivedPrompts.Record(input, [], audioBatched: true).Length;
        var reserve = 512 + batches.Count * 128;
        var available = NoteDerivedLimits.MaxSynthesisInputChars - skeleton - reserve;
        var perBatchLimit = available <= 0 ? 0 :
            Math.Min(NoteDerivedLimits.MaxEvidenceCharsPerStep, available / batches.Count);
        if (batches.Count > 0 && perBatchLimit < 1000)
        {
            issues.Add(new("audio_evidence_budget_insufficient",
                $"完整记录基础输入已占用 {skeleton} 字符，无法为 {batches.Count} 个录音批次各保留至少 1000 字符的整理证据；未截断、未发送"));
            return [];
        }

        var result = new List<NoteDerivedPlanStep>(batches.Count);
        for (var index = 0; index < batches.Count; index++)
        {
            var key = NoteDerivedPrompts.AudioBatchKey(index);
            var audio = batches[index].ToArray();
            var transcriptOrdinals = OverlappingTranscriptOrdinals(input.Transcripts, audio);
            var prompt = NoteDerivedPrompts.AudioBatch(key, index, batches.Count, audio,
                input.Transcripts.Where(x => transcriptOrdinals.Contains(x.Ordinal)));
            if (prompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
            {
                issues.Add(new("audio_batch_prompt_too_large",
                    $"{key} 的整理提示为 {prompt.Length} 字符，超过 SDK 实际输入上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断、未发送",
                    StepKey: key));
                continue;
            }
            result.Add(new(key, NoteDerivedStepKind.AudioBatchEvidence, [], [], prompt.Length,
                Audio: audio, TranscriptOrdinals: transcriptOrdinals,
                MaxOutputCharacters: perBatchLimit));
        }
        return result;
    }

    private static int[] OverlappingTranscriptOrdinals(
        IReadOnlyList<NoteDerivedTranscriptInput> transcripts,
        IReadOnlyList<NoteDerivedAudioInput> audio) =>
        transcripts.Where(transcript => audio.Any(slice =>
                slice.RecordingId == transcript.RecordingId &&
                slice.RecordingSegment == transcript.RecordingSegment &&
                slice.ChunkIndex == transcript.ChunkIndex &&
                transcript.SourceOffsetSamples < slice.SourceOffsetSamples + slice.Samples &&
                slice.SourceOffsetSamples < transcript.SourceOffsetSamples + transcript.Samples))
            .Select(x => x.Ordinal)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

    private static List<IReadOnlyList<NoteDerivedImageInput>> BuildImageBatches(
        IReadOnlyList<NoteDerivedImageInput> source, List<NoteDerivedIssueDto> issues)
    {
        if (source.Count > NoteDerivedLimits.MaxImages)
            issues.Add(new("image_count_limit",
                $"材料包含 {source.Count} 张照片/手写预览图，超过单次派生上限 {NoteDerivedLimits.MaxImages}；全部保留且不截断"));

        var valid = new List<NoteDerivedImageInput>();
        var ordinals = new HashSet<int>();
        var fileIndexes = new HashSet<int>();
        foreach (var image in source.OrderBy(x => x.Ordinal))
        {
            if (!ordinals.Add(image.Ordinal))
                issues.Add(new("image_duplicate_ordinal", "图片材料顺序标识重复", image.Path));
            if (!fileIndexes.Add(image.FileIndex))
                issues.Add(new("image_duplicate_file", "图片材料文件标识重复", image.Path));
            if (image.Kind is not ("Photo" or "InkPreview"))
            {
                issues.Add(new("image_kind_invalid", "图片材料类型不是 Photo 或 InkPreview", image.Path));
                continue;
            }
            if (image.ContentType is not ("image/png" or "image/jpeg"))
            {
                var code = image.Path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                    ? "image_webp_unsupported" : "image_type_unsupported";
                issues.Add(new(code, "视觉 SDK 仅接受 PNG/JPEG；WebP 或其他类型必须阻断，不转码、不截断", image.Path));
                continue;
            }
            if (image.SizeBytes is < 1 or > NoteDerivedLimits.MaxImageBytes)
            {
                issues.Add(new("image_size_limit",
                    $"图片大小为 {image.SizeBytes} 字节，不符合单图 1-{NoteDerivedLimits.MaxImageBytes} 字节限制；不压缩、不截断",
                    image.Path));
                continue;
            }
            if (image.Sha256 is not { Length: 64 } ||
                image.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                issues.Add(new("image_hash_invalid", "图片 SHA-256 无效", image.Path));
                continue;
            }
            if (image.Sha256 != image.Sha256.ToLowerInvariant())
            {
                issues.Add(new("image_hash_invalid", "图片 SHA-256 必须使用小写十六进制", image.Path));
                continue;
            }
            valid.Add(image);
        }

        var batches = new List<IReadOnlyList<NoteDerivedImageInput>>();
        var current = new List<NoteDerivedImageInput>();
        long currentBytes = 0;
        foreach (var image in valid)
        {
            var exceeds = current.Count == NoteDerivedLimits.MaxImagesPerVisionCall ||
                currentBytes + image.SizeBytes > NoteDerivedLimits.MaxVisionCallBytes;
            if (exceeds)
            {
                batches.Add(current.ToArray());
                current.Clear();
                currentBytes = 0;
            }
            current.Add(image);
            currentBytes = checked(currentBytes + image.SizeBytes);
        }
        if (current.Count > 0) batches.Add(current.ToArray());
        if (batches.Count > NoteDerivedLimits.MaxVisionCalls)
            issues.Add(new("vision_batch_limit",
                $"材料需要 {batches.Count} 批视觉调用，超过上限 {NoteDerivedLimits.MaxVisionCalls}；未减少、转码或截断任何图片"));
        return batches;
    }

    private static void ValidateTranscripts(IReadOnlyList<NoteDerivedTranscriptInput> transcripts,
        List<NoteDerivedIssueDto> issues)
    {
        var ordinals = new HashSet<int>();
        var asrSegments = new HashSet<int>();
        foreach (var transcript in transcripts.OrderBy(x => x.Ordinal))
        {
            if (!ordinals.Add(transcript.Ordinal))
                issues.Add(new("transcript_duplicate_ordinal", "ASR 转写顺序标识重复", transcript.SourcePath));
            if (!asrSegments.Add(transcript.AsrSegmentIndex))
                issues.Add(new("transcript_duplicate_asr_segment", "同一 ASR 分片标识被重复纳入", transcript.SourcePath));
            if (transcript.AsrSegmentIndex < 0 || transcript.RecordingId == Guid.Empty ||
                transcript.RecordingSegment <= 0 ||
                transcript.ChunkIndex <= 0 || transcript.SourceFileIndex < 0 || transcript.LogId == Guid.Empty ||
                transcript.SourceOffsetSamples < 0 || transcript.Samples <= 0 ||
                string.IsNullOrWhiteSpace(transcript.SourcePath) || transcript.SourceSha256 is not { Length: 64 })
                issues.Add(new("transcript_source_invalid", "ASR 转写缺少 owner、版本、来源或回执身份", transcript.SourcePath));
        }
    }

    private void ValidateAudio(IReadOnlyList<NoteDerivedAudioInput> audio,
        List<NoteDerivedIssueDto> issues)
    {
        if (audio.Count == 0) return;
        var maxSlices = NoteDerivedAudioContext.MaxSlices * MaxBatches;
        if (audio.Count > maxSlices)
            issues.Add(new("audio_slice_limit",
                $"原始录音上下文包含 {audio.Count} 个片段，超过总上限 {maxSlices}；未截断"));

        var ordinals = new HashSet<int>();
        var identities = new HashSet<(Guid RecordingId, int Segment, int Chunk, long Offset)>();
        long total = 0;
        foreach (var item in audio.OrderBy(x => x.Ordinal))
        {
            if (!ordinals.Add(item.Ordinal))
                issues.Add(new("audio_context_duplicate_ordinal", "原始录音上下文顺序标识重复"));
            if (item.RecordingId == Guid.Empty || item.RecordingSegment <= 0 ||
                item.ChunkIndex <= 0 || item.FileIndex < 0 ||
                item.SourceOffsetSamples < 0 || item.Samples <= 0 ||
                item.SizeBytes != NoteDerivedAudioContext.ByteSize(item.Samples) ||
                string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Any(char.IsControl) ||
                item.Name.Contains('/') || item.Name.Contains('\\') ||
                !NoteMaterialManifestValidator.IsDigest(item.SourceSha256))
            {
                issues.Add(new("audio_context_invalid",
                    "原始录音上下文缺少固定录音、材料文件或摘要身份", item.Path));
                continue;
            }
            if (!identities.Add((item.RecordingId, item.RecordingSegment, item.ChunkIndex,
                    item.SourceOffsetSamples)))
                issues.Add(new("audio_context_duplicate",
                    "原始录音上下文包含重复片段", item.Path));
            if (item.SizeBytes > NoteDerivedAudioContext.MaxBytes)
                issues.Add(new("audio_slice_too_large",
                    $"单个原始录音片段为 {item.SizeBytes} 字节，超过单次 {NoteDerivedAudioContext.MaxBytes} 字节上限；未截断",
                    item.Path));
            if (total > long.MaxValue - item.SizeBytes)
            {
                issues.Add(new("audio_context_too_large",
                    "原始录音上下文合计字节数溢出；未截断、未发送"));
                total = long.MaxValue;
            }
            else
            {
                total += item.SizeBytes;
            }
        }
        if (total > Math.Clamp(_audio.MaxTotalBytes, NoteDerivedAudioContext.MaxBytes,
                NoteDerivedAudioContextOptions.HardMaxTotalBytes))
            issues.Add(new("audio_total_budget_exceeded",
                $"原始录音上下文合计 {total} 字节，超过任务总预算；未截断、未发送"));
    }

    private static List<NoteDerivedIssueDto> DistinctIssues(IEnumerable<NoteDerivedIssueDto> issues) =>
        issues.DistinctBy(x => (x.Code, x.Message, x.SourcePath, x.BlockId, x.StepKey)).ToList();
}

internal sealed record NoteDerivedEvidence(
    string StepKey,
    Guid LogId,
    string Text,
    IReadOnlyList<NoteDerivedImageInput> Images);

internal static class NoteDerivedPrompts
{
    internal const string RecordKey = "record-synthesis";
    internal const string AnalysisKey = "analysis-synthesis";
    internal const string SourceObjectType = "native-note-derived-task";
    internal const string VisionPurpose = "note.derived.vision";
    internal const string AudioBatchPurpose = "note.derived.audio-batch";
    internal const string RecordPurpose = "note.derived.record-synthesis";
    internal const string AnalysisPurpose = "note.derived.analysis-synthesis";
    internal const string PromptVersion = "note-derived-prompts-v2";

    private const string VisionSystem =
        "你是教材笔记材料的视觉证据提取器。逐张查看输入原图，准确记录文字、图表、公式、手写内容和版面关系。" +
        "不得遗漏任何输入图片，不得根据文件名猜测内容，不得输出与图片无关的概括。";
    private const string RecordSystem =
        "你是教材笔记完整记录整理器。必须覆盖用户提供的全部 Markdown、全部 ASR 成功转写或对应的录音批次证据，以及全部视觉证据。" +
        "不得省略、合并掉或编造来源；空转写必须保留明确占位；输出应为可独立阅读的完整记录 Markdown。";
    private const string AnalysisSystem =
        "你是独立分析器，不是摘要器。基于用户提供的全部固定材料进行批判性分析，指出结构、论点、证据、疑点和可行动结论。" +
        "不得把完整记录原文重新复述成另一份摘要，也不得声称分析了未提供的内容。";
    private const string AudioBatchSystem =
        "你是长录音分批整理器。只处理本批 WAV 分片及其与本批时间范围重叠的 ASR 转写；" +
        "按录音与时间顺序完整整理本批内容，保留说话人/时间线索、事实和不确定点。" +
        "不得声称读取了其他批次，不得编造未提供的音频或转写，也不得输出完整记录或独立分析。";

    internal static string ImageBatchKey(int zeroBasedIndex) => $"image-batch:{zeroBasedIndex + 1:D4}";
    internal static string AudioBatchKey(int zeroBasedIndex) => $"audio-batch:{zeroBasedIndex + 1:D4}";

    internal static string ImageName(NoteDerivedImageInput image)
    {
        var extension = Path.GetExtension(image.Path).ToLowerInvariant();
        var prefix = image.Sha256.Length >= 12 ? image.Sha256[..12] : image.Sha256;
        var kind = image.Kind == "Photo" ? "photo" : "ink";
        return $"{image.Ordinal:D4}-{prefix}-{kind}{extension}";
    }

    internal static string ImageEvidence(string stepKey, IReadOnlyList<NoteDerivedImageInput> images)
    {
        var text = new StringBuilder();
        text.AppendLine(VisionSystem);
        text.AppendLine();
        text.AppendLine($"步骤：{stepKey}");
        text.AppendLine("请按以下清单逐图分析；图片将以清单顺序作为视觉输入：");
        foreach (var image in images)
        {
            text.AppendLine($"- {ImageName(image)} | source={image.Path} | sha256={image.Sha256} | " +
                $"kind={image.Kind} | contentType={image.ContentType} | size={image.SizeBytes}");
        }
        text.AppendLine();
        text.AppendLine("输出要求：逐图给出可核验的完整证据记录，明确图片名称；最终给出本批图片的共同信息汇总。");
        return text.ToString();
    }

    internal static string Record(NoteDerivedInputSnapshot input, IReadOnlyList<NoteDerivedEvidence> evidence,
        bool audioBatched = false)
    {
        var text = new StringBuilder();
        text.AppendLine(RecordSystem);
        text.AppendLine();
        AppendSourceMaterial(text, input, evidence, !audioBatched, !audioBatched, audioBatched);
        text.AppendLine();
        text.AppendLine(audioBatched
            ? "输出：只输出完整记录 Markdown。必须按录音时间顺序覆盖全部录音批次证据、全部 Markdown 和全部图片视觉证据；不得截断、省略或虚构。"
            : "输出：只输出完整记录 Markdown。必须逐项覆盖上述全部来源；不得截断、省略或虚构。");
        return text.ToString();
    }

    internal static string Analysis(NoteDerivedInputSnapshot input, Guid completeRecordVersionId,
        string completeRecordMarkdown, IReadOnlyList<NoteDerivedEvidence> evidence,
        bool audioBatched = false)
    {
        ArgumentNullException.ThrowIfNull(completeRecordMarkdown);
        var text = new StringBuilder();
        text.AppendLine(AnalysisSystem);
        text.AppendLine();
        text.AppendLine($"必须引用的完整记录版本：{completeRecordVersionId:D}");
        text.AppendLine($"完整记录版本 SHA-256：{Digest(completeRecordMarkdown)}");
        text.AppendLine();
        text.AppendLine("## 已校验的完整记录正文");
        text.AppendLine(completeRecordMarkdown);
        text.AppendLine();
        if (audioBatched)
        {
            text.AppendLine("本任务的长录音已由受控批次按原始顺序整理为上述完整记录；独立分析只引用该已校验版本，不重复拼接各批原始分片。");
            text.AppendLine();
        }
        else
        {
            AppendSourceMaterial(text, input, evidence, true, true, false);
        }
        text.AppendLine();
        text.AppendLine("输出：只输出独立分析 Markdown。按主题分析而非逐段复述；明确证据来源和不确定性。");
        return text.ToString();
    }

    internal static string AudioBatch(string stepKey, int zeroBasedIndex, int batchCount,
        IReadOnlyList<NoteDerivedAudioInput> audio,
        IEnumerable<NoteDerivedTranscriptInput> transcripts)
    {
        var text = new StringBuilder();
        text.AppendLine(AudioBatchSystem);
        text.AppendLine();
        text.AppendLine($"步骤：{stepKey}（第 {zeroBasedIndex + 1} / {batchCount} 批）");
        text.AppendLine($"本批 WAV 分片：{audio.Count} 个；会按下列清单顺序作为同一请求的音频内容发送。");
        foreach (var item in audio.OrderBy(x => x.Ordinal))
        {
            var start = item.SourceOffsetSamples / 16_000m;
            var end = (item.SourceOffsetSamples + item.Samples) / 16_000m;
            text.AppendLine($"- {item.Name} | recording={item.RecordingId:D} | " +
                $"segment={item.RecordingSegment} | chunk={item.ChunkIndex} | " +
                $"sourceOffsetSamples={item.SourceOffsetSamples} | samples={item.Samples} | " +
                $"time={start:0.000}-{end:0.000}s | sourceSha256={item.SourceSha256}");
        }
        text.AppendLine();
        text.AppendLine("## 本批时间范围重叠的成功 ASR 转写");
        var transcriptList = transcripts.OrderBy(x => x.Ordinal).ToArray();
        if (transcriptList.Length == 0)
        {
            text.AppendLine("（本批没有成功 ASR 转写；仍须依据音频整理，并明确标注无法确认的内容。）");
        }
        else
        {
            foreach (var transcript in transcriptList)
            {
                text.AppendLine($"### ASR {transcript.Ordinal + 1:D5} | " +
                    $"log={transcript.LogId?.ToString("D") ?? "-"} | " +
                    $"asrSegment={transcript.AsrSegmentIndex} | recording={transcript.RecordingId:D} | " +
                    $"segment={transcript.RecordingSegment} | chunk={transcript.ChunkIndex} | " +
                    $"sourceOffset={transcript.SourceOffsetSamples} | samples={transcript.Samples} | " +
                    $"sha256={transcript.SourceSha256}");
                text.AppendLine(string.IsNullOrWhiteSpace(transcript.Text)
                    ? "（空转写：ASR 成功但未识别出文字，请复核本批原始录音。）"
                    : transcript.Text);
            }
        }
        text.AppendLine();
        text.AppendLine("输出要求：按时间顺序输出本批整理证据；明确区分音频中直接可确认的内容与仅由 ASR 转写支持的内容；" +
            "不得遗漏本批任何分片或转写，不得输出跨批次的完整记录。");
        return text.ToString();
    }

    private static string Digest(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AppendSourceMaterial(StringBuilder text, NoteDerivedInputSnapshot input,
        IReadOnlyList<NoteDerivedEvidence> evidence, bool includeTranscripts,
        bool includeAudioContext, bool audioBatched)
    {
        text.AppendLine($"# 固定材料：{input.NoteTitle}");
        text.AppendLine($"笔记：{input.NoteId:D}");
        text.AppendLine($"材料版本：{input.SourceVersionId:D}");
        text.AppendLine($"材料输入 SHA-256：{input.InputSha256}");
        text.AppendLine($"材料清单 SHA-256：{input.ManifestSha256}");
        if (input.AsrTaskId is { } asrTaskId)
            text.AppendLine($"ASR 任务：{asrTaskId:D} | revision={input.AsrRevision?.ToString("D") ?? "unknown"}");
        text.AppendLine();
        text.AppendLine("## 全部 Markdown");
        text.AppendLine(string.IsNullOrEmpty(input.NoteMarkdown)
            ? string.Join("\n\n", input.TextBlocks.OrderBy(x => x.Ordinal).Select(x => x.Text))
            : input.NoteMarkdown);

        text.AppendLine();
        text.AppendLine("## 全部成功 ASR 转写");
        if (!includeTranscripts)
        {
            text.AppendLine($"（共 {input.Transcripts.Count} 段成功转写，已按时间范围并入录音批次证据；" +
                "此处不重复拼接，完整记录必须覆盖全部批次。）");
        }
        else if (input.Transcripts.Count == 0)
        {
            text.AppendLine("（本固定材料没有成功 ASR 转写片段。）");
        }
        else
        {
            foreach (var transcript in input.Transcripts.OrderBy(x => x.Ordinal))
            {
                text.AppendLine($"### ASR {transcript.Ordinal + 1:D5} | " +
                    $"log={transcript.LogId?.ToString("D") ?? "-"} | " +
                    $"asrSegment={transcript.AsrSegmentIndex} | recording={transcript.RecordingId:D} | " +
                    $"segment={transcript.RecordingSegment} | sourceOffset={transcript.SourceOffsetSamples} | " +
                    $"samples={transcript.Samples} | " +
                    $"chunk={transcript.ChunkIndex} | file={transcript.SourceFileIndex} | " +
                    $"sha256={transcript.SourceSha256} | source={transcript.SourcePath}");
                text.AppendLine(string.IsNullOrWhiteSpace(transcript.Text)
                    ? "（空转写：ASR 成功但未识别出文字，请复核原始录音。）"
                    : transcript.Text);
            }
        }
        if (includeAudioContext)
            NoteDerivedAudioContext.AppendPrompt(text, input.Audio);

        text.AppendLine();
        text.AppendLine("## 全部图片视觉证据");
        if (input.Images.Count == 0)
        {
            text.AppendLine("（本固定材料没有照片或手写预览图。）");
        }
        else
        {
            foreach (var image in input.Images.OrderBy(x => x.Ordinal))
                text.AppendLine($"- {ImageName(image)} | source={image.Path} | kind={image.Kind} | sha256={image.Sha256}");
        }
        text.AppendLine();
        var imageEvidence = evidence.Where(x => x.Images.Count > 0)
            .OrderBy(x => x.StepKey, StringComparer.Ordinal).ToArray();
        var audioEvidence = evidence.Where(x => x.Images.Count == 0)
            .OrderBy(x => x.StepKey, StringComparer.Ordinal).ToArray();
        if (imageEvidence.Length == 0)
        {
            text.AppendLine("（视觉证据尚未生成。）");
        }
        foreach (var item in imageEvidence)
        {
            text.AppendLine($"### {item.StepKey} | aiLog={item.LogId:D}");
            text.AppendLine(item.Text);
        }
        text.AppendLine();
        text.AppendLine("## 全部录音批次证据");
        if (audioEvidence.Length == 0)
        {
            text.AppendLine(audioBatched
                ? "（录音批次证据尚未生成。）"
                : "（本固定材料未按批次发送原始录音。）");
            return;
        }
        foreach (var item in audioEvidence)
        {
            text.AppendLine($"### {item.StepKey} | aiLog={item.LogId:D}");
            text.AppendLine(item.Text);
        }
    }
}
