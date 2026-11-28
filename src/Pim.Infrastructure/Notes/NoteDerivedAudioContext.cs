using System.Security.Cryptography;
using System.Text;

namespace Pim.Infrastructure.Notes;

internal static class NoteDerivedAudioContext
{
    // This is the same request boundary enforced by AiTextCallService. Keep the limit
    // here so planning can block before a task ever attempts a provider call.
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal const int MaxSlices = 5_000;
    // Context slices are one minute; this is the per-call read budget, not a wider ASR budget.
    internal const int MaxSamplesPerSlice = NoteAsrPlan.SamplesPerCall;

    internal static long ByteSize(long samples) => checked(44 + samples * 2);

    internal static string Name(NoteDerivedAudioInput audio) =>
        Name(audio.RecordingId, audio.RecordingSegment, audio.ChunkIndex,
            audio.SourceOffsetSamples, audio.Samples);

    internal static string Name(Guid recordingId, int recordingSegment, int chunkIndex,
        long sourceOffsetSamples, long samples) =>
        $"recording-{recordingId:D}-s{recordingSegment:D4}-c{chunkIndex:D6}" +
        $"-o{sourceOffsetSamples:D012}-n{samples}.wav";

    internal static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static void AppendPrompt(StringBuilder text, IReadOnlyList<NoteDerivedAudioInput> audio)
    {
        if (audio.Count == 0) return;
        text.AppendLine();
        text.AppendLine("## 服务端固定原始录音上下文");
        text.AppendLine("以下录音会作为同一请求的音频内容发送；路径只在服务端用于读取，不能由客户端指定。");
        foreach (var item in audio.OrderBy(x => x.Ordinal))
        {
            text.AppendLine($"- {item.Name} | recording={item.RecordingId:D} | " +
                $"segment={item.RecordingSegment} | chunk={item.ChunkIndex} | " +
                $"sourceOffsetSamples={item.SourceOffsetSamples} | samples={item.Samples} | " +
                $"sourceSha256={item.SourceSha256} | contentType=audio/wav");
        }
    }
}
