using System.Globalization;
using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>
/// Parses the synchronous multimodal chat completion. A response is never treated as a
/// successful transcript unless usage evidence says audio was actually consumed.
/// </summary>
public static class MimoMultimodalResponseParser
{
    public const string EmptyFinishReason = "incomplete:empty";
    public const string RepetitionFinishReason = "incomplete:repetition";
    public const string TruncationFinishReason = "incomplete:truncation";
    public const string MissingFinishReason = "incomplete:missing-finish";

    /// <summary>
    /// Shared semantic inspection used by synchronous and Batch results. It never throws on a
    /// malformed shape; callers can retain the raw body and publish an explicit invalid code.
    /// </summary>
    public static MimoResponseSemantics Inspect(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new MimoResponseSemantics("", "none", null, null, false, false, false);
        var audioTokens = ReadAudioTokens(root);
        var choice = root.TryGetProperty("choices", out var choices) &&
                     choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0] : default;
        var message = choice.ValueKind == JsonValueKind.Object &&
                      choice.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
            ? m : default;
        var content = ReadString(message, "content");
        var reasoning = ReadString(message, "reasoning_content");
        var source = !string.IsNullOrWhiteSpace(content) ? "content"
            : !string.IsNullOrWhiteSpace(reasoning) ? "reasoning_content" : "none";
        var text = source switch
        {
            "content" => content!,
            "reasoning_content" => reasoning!,
            _ => ""
        };
        var hasTranscript = source != "none";
        var rawFinish = ReadString(choice, "finish_reason");
        var truncation = string.Equals(rawFinish, "length", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rawFinish, "max_tokens", StringComparison.OrdinalIgnoreCase);
        return new(text, source, audioTokens, rawFinish, hasTranscript,
            hasTranscript && LooksRepetitive(text), truncation);
    }

    public static AiAsrProviderResult Parse(string rawJson, AiAsrInput audio, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new MimoBatchException(50203, "MiMo多模态响应为空，无法确认audio_tokens，未视为成功");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            // A non-JSON body cannot carry usage evidence; fail closed instead of accepting raw text.
            throw new MimoBatchException(50203, "MiMo多模态响应不是有效JSON，无法确认audio_tokens，未视为成功");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MimoBatchException(50203, "MiMo多模态响应结构无效，无法确认audio_tokens，未视为成功");

            var audioTokens = ReadAudioTokens(root);
            if (audioTokens is null or <= 0)
                throw new MimoBatchException(50202, "MiMo多模态响应audio_tokens缺失或为0，无法确认音频被处理");
            var semantics = Inspect(root);

            var usage = root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
                ? usageElement : default;
            long? inputTokens = ReadLong(usage, "prompt_tokens") ?? ReadLong(usage, "input_tokens");
            long? outputTokens = ReadLong(usage, "completion_tokens") ?? ReadLong(usage, "output_tokens");
            long? totalTokens = ReadLong(usage, "total_tokens");
            var reportedSeconds = ReadSeconds(usage) ?? EstimateAudioSeconds(audio);

            var source = semantics.ContentSource;
            var text = semantics.Text;
            var hasTranscript = semantics.HasTranscript;
            var rawFinish = semantics.FinishReasonRaw;
            var empty = !hasTranscript;
            var repetition = semantics.Repetition;
            var truncation = semantics.Truncation;
            var transcript = hasTranscript ? MimoTranscriptParser.Parse(text) : MimoTranscriptParseResult.Empty(text);
            var timestampFallback = transcript.TimestampFallback;
            var finishReason = empty ? EmptyFinishReason
                : repetition ? RepetitionFinishReason
                : truncation ? TruncationFinishReason
                : string.IsNullOrWhiteSpace(rawFinish) ? MissingFinishReason : rawFinish;

            var wrapper = JsonSerializer.Serialize(new
            {
                recordFormat = "mimo-multimodal-v1",
                model,
                contentSource = source,
                finishReasonRaw = rawFinish,
                finishReason,
                audioTokens,
                flags = new { empty, repetition, truncation, timestampFallback,
                    formatDetected = transcript.FormatDetected, hasGaps = transcript.HasGaps,
                    hasOverlaps = transcript.HasOverlaps },
                transcript = new
                {
                    text,
                    coverageSeconds = transcript.CoverageSeconds,
                    segments = transcript.Segments.Select(segment => new
                    {
                        startSeconds = segment.StartSeconds,
                        endSeconds = segment.EndSeconds,
                        speaker = segment.Speaker,
                        text = segment.Text
                    }),
                    issues = transcript.Issues
                },
                upstream = root.Clone()
            });
            return new AiAsrProviderResult(text, finishReason, inputTokens, outputTokens, totalTokens,
                wrapper, hasTranscript, reportedSeconds);
        }
    }

    /// <summary>Best-effort duration from container headers only; never invents a value on malformed input.</summary>
    public static double? EstimateAudioSeconds(AiAsrInput audio) =>
        audio.Format == "wav" ? EstimateWaveSeconds(audio.Bytes) : EstimateMp3Seconds(audio.Bytes);

    private static long? ReadAudioTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        // #516 AC12 uses the canonical chat-completions field. Never let a different,
        // unproven alias rescue a canonical zero/missing value.
        return usage.TryGetProperty("prompt_tokens_details", out var details) &&
               details.ValueKind == JsonValueKind.Object
            ? ReadLong(details, "audio_tokens")
            : null;
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : null;

    private static double? ReadSeconds(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object || !usage.TryGetProperty("seconds", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0) return null;
        return seconds;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? EstimateWaveSeconds(byte[] bytes)
    {
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return null;
        var offset = 12;
        uint? byteRate = null;
        uint? dataSize = null;
        while (offset + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToUInt32(bytes, offset + 4);
            if (id == "fmt " && size >= 12 && offset + 20 <= bytes.Length)
                byteRate = BitConverter.ToUInt32(bytes, offset + 16);
            if (id == "data")
            {
                dataSize = size;
                break;
            }
            var next = (long)offset + 8 + size + (size & 1);
            if (next <= offset || next > bytes.Length) break;
            offset = (int)next;
        }
        return byteRate is > 0 && dataSize is not null
            ? Math.Round((double)dataSize.Value / byteRate.Value, 3)
            : null;
    }

    private static double? EstimateMp3Seconds(byte[] bytes)
    {
        var offset = 0;
        if (bytes.Length >= 10 && bytes.AsSpan(0, 3).SequenceEqual("ID3"u8))
            offset = 10 + ((bytes[6] & 0x7f) << 21 | (bytes[7] & 0x7f) << 14 |
                           (bytes[8] & 0x7f) << 7 | (bytes[9] & 0x7f));
        for (var i = offset; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] != 0xff || (bytes[i + 1] & 0xe0) != 0xe0) continue;
            var version = (bytes[i + 1] >> 3) & 3;
            var layer = (bytes[i + 1] >> 1) & 3;
            if (version == 1 || layer != 1) continue;
            var bitrateIndex = (bytes[i + 2] >> 4) & 0xf;
            var sampleIndex = (bytes[i + 2] >> 2) & 3;
            if (bitrateIndex is 0 or 15 || sampleIndex == 3) continue;
            var mpeg1 = version == 3;
            int[] bitrates = mpeg1
                ? [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]
                : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
            var remaining = bytes.Length - i;
            return Math.Round(remaining * 8.0 / (bitrates[bitrateIndex] * 1000.0), 3);
        }
        return null;
    }

    private static bool LooksRepetitive(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0) return false;
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 3 && lines.Distinct(StringComparer.Ordinal).Count() == 1) return true;
        var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 6)
        {
            var longest = 1;
            var run = 1;
            for (var i = 1; i < tokens.Length; i++)
            {
                run = string.Equals(tokens[i], tokens[i - 1], StringComparison.Ordinal) ? run + 1 : 1;
                longest = Math.Max(longest, run);
            }
            if (longest >= 6 || tokens.Distinct(StringComparer.Ordinal).Count() * 5 <= tokens.Length) return true;
        }
        if (normalized.Length >= 30)
        {
            var unit = normalized[..Math.Min(20, normalized.Length / 3)];
            if (unit.Length >= 10 &&
                normalized.StartsWith(string.Concat(unit, unit, unit), StringComparison.Ordinal)) return true;
        }
        return false;
    }

}
