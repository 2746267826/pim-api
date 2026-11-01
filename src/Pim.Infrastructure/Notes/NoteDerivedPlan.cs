using System.Text;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

internal sealed class NoteDerivedPlanner : INoteDerivedPlanner
{
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
        var imageKeys = steps.Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence)
            .Select(x => x.Key).ToArray();
        var recordPrompt = NoteDerivedPrompts.Record(input, []);
        if (recordPrompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
            issues.Add(new("record_input_too_large",
                $"完整记录基础输入为 {recordPrompt.Length} 字符，超过 SDK 实际输入上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                StepKey: NoteDerivedPrompts.RecordKey));
        steps.Add(new(NoteDerivedPrompts.RecordKey, NoteDerivedStepKind.RecordSynthesis,
            imageKeys, [], recordPrompt.Length));

        var analysisPrompt = NoteDerivedPrompts.Analysis(input, Guid.Empty, "", []);
        if (analysisPrompt.Length > NoteDerivedLimits.MaxSynthesisInputChars)
            issues.Add(new("analysis_input_too_large",
                $"独立分析基础输入为 {analysisPrompt.Length} 字符，超过 SDK 实际输入上限 {NoteDerivedLimits.MaxSynthesisInputChars}；未截断且不会发送",
                StepKey: NoteDerivedPrompts.AnalysisKey));
        steps.Add(new(NoteDerivedPrompts.AnalysisKey, NoteDerivedStepKind.AnalysisSynthesis,
            [NoteDerivedPrompts.RecordKey], [], analysisPrompt.Length));

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
    internal const string RecordPurpose = "note.derived.record-synthesis";
    internal const string AnalysisPurpose = "note.derived.analysis-synthesis";
    internal const string PromptVersion = "note-derived-prompts-v1";

    private const string VisionSystem =
        "你是教材笔记材料的视觉证据提取器。逐张查看输入原图，准确记录文字、图表、公式、手写内容和版面关系。" +
        "不得遗漏任何输入图片，不得根据文件名猜测内容，不得输出与图片无关的概括。";
    private const string RecordSystem =
        "你是教材笔记完整记录整理器。必须覆盖用户提供的全部 Markdown、全部 ASR 成功转写和全部视觉证据。" +
        "不得省略、合并掉或编造来源；空转写必须保留明确占位；输出应为可独立阅读的完整记录 Markdown。";
    private const string AnalysisSystem =
        "你是独立分析器，不是摘要器。基于用户提供的全部固定材料进行批判性分析，指出结构、论点、证据、疑点和可行动结论。" +
        "不得把完整记录原文重新复述成另一份摘要，也不得声称分析了未提供的内容。";

    internal static string ImageBatchKey(int zeroBasedIndex) => $"image-batch:{zeroBasedIndex + 1:D4}";

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

    internal static string Record(NoteDerivedInputSnapshot input, IReadOnlyList<NoteDerivedEvidence> evidence)
    {
        var text = new StringBuilder();
        text.AppendLine(RecordSystem);
        text.AppendLine();
        AppendSourceMaterial(text, input, evidence);
        text.AppendLine();
        text.AppendLine("输出：只输出完整记录 Markdown。必须逐项覆盖上述全部来源；不得截断、省略或虚构。");
        return text.ToString();
    }

    internal static string Analysis(NoteDerivedInputSnapshot input, Guid completeRecordVersionId,
        string completeRecordMarkdown, IReadOnlyList<NoteDerivedEvidence> evidence)
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
        AppendSourceMaterial(text, input, evidence);
        text.AppendLine();
        text.AppendLine("输出：只输出独立分析 Markdown。按主题分析而非逐段复述；明确证据来源和不确定性。");
        return text.ToString();
    }

    private static string Digest(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AppendSourceMaterial(StringBuilder text, NoteDerivedInputSnapshot input,
        IReadOnlyList<NoteDerivedEvidence> evidence)
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
        if (input.Transcripts.Count == 0)
        {
            text.AppendLine("（本固定材料没有录音，也没有 ASR 转写片段。）");
        }
        else
        {
            foreach (var transcript in input.Transcripts.OrderBy(x => x.Ordinal))
            {
                text.AppendLine($"### ASR {transcript.Ordinal + 1:D5} | log={transcript.LogId:D} | " +
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

        text.AppendLine();
        text.AppendLine("## 全部图片视觉证据");
        if (input.Images.Count == 0)
        {
            text.AppendLine("（本固定材料没有照片或手写预览图。）");
            return;
        }
        foreach (var image in input.Images.OrderBy(x => x.Ordinal))
            text.AppendLine($"- {ImageName(image)} | source={image.Path} | kind={image.Kind} | sha256={image.Sha256}");
        text.AppendLine();
        if (evidence.Count == 0)
        {
            text.AppendLine("（视觉证据尚未生成。）");
            return;
        }
        foreach (var item in evidence.OrderBy(x => x.StepKey, StringComparer.Ordinal))
        {
            text.AppendLine($"### {item.StepKey} | aiLog={item.LogId:D}");
            text.AppendLine(item.Text);
        }
    }
}
