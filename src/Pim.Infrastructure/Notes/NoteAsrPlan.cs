using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

internal static class NoteAsrPlan
{
    internal const int SampleRate = 16_000;
    // One minute is within the existing attachment/ASR boundary. Samples, not rounded milliseconds, define coverage.
    internal const int SamplesPerCall = SampleRate * 60;
    internal static List<NoteAsrStep> Build(NoteMaterialVersionDto version, int samplesPerCall = SamplesPerCall)
    {
        if (version.Upload.Phase == "Uploading") throw new DomainException(40901, "请先上传完整固定材料，再开始转写");
        if (samplesPerCall <= 0) throw new DomainException(40001, "ASR单次音频预算无效");
        using var doc = JsonDocument.Parse(version.InputJson);
        var files = version.Upload.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var steps = new List<NoteAsrStep>(); long timeline = 0;
        foreach (var recording in doc.RootElement.GetProperty("recordings").EnumerateArray())
        {
            var recordingId = recording.GetProperty("id").GetGuid(); long position = 0;
            foreach (var chunk in recording.GetProperty("chunks").EnumerateArray())
            {
                var segment = chunk.GetProperty("segment").GetInt32(); var index = chunk.GetProperty("index").GetInt32();
                var count = chunk.GetProperty("samples").GetInt64();
                var file = files[$"recordings/{recordingId:D}/s{segment:D4}-c{index:D6}.wav"];
                for (long offset = 0; offset < count; offset += samplesPerCall)
                {
                    if (steps.Count >= 5_000) throw new DomainException(41301, "本次转写超过5000片，请按材料版本分批处理；原录音保留");
                    steps.Add(new() { Index = steps.Count, RecordingId = recordingId, RecordingSegment = segment,
                        ChunkIndex = index, FileIndex = file.Index, Path = file.Path, Sha256 = file.Sha256,
                        SourceOffset = offset, RecordingOffset = position + offset, TimelineOffset = timeline + position + offset,
                        Samples = Math.Min(samplesPerCall, count - offset), Attempts = [new()] });
                }
                position = checked(position + count);
            }
            timeline = checked(timeline + position);
        }
        if (steps.Count == 0) throw new DomainException(40001, "本版本没有可转写的录音");
        return steps;
    }
}

internal sealed class NoteAsrState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Owner { get; set; }
    public required StartNoteAsrRequest Request { get; set; }
    public required string Fingerprint { get; set; }
    public Guid NoteId { get; set; }
    public required string InputSha256 { get; set; }
    public required string ManifestSha256 { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool StopRequested { get; set; }
    public Dictionary<Guid, string> Retries { get; set; } = [];
    // The intent freezes the exact pre-mutation attempt epoch. Recovery can clear an old stop,
    // but can never erase a stop that landed after this intent was committed to the task file.
    public Dictionary<Guid, NoteAsrRetryIntent> RetryIntents { get; set; } = [];
    public List<NoteAsrStep> Steps { get; set; } = [];
    // Null is the legacy dedicated cloud-ASR pipeline. New tasks persist the resolved route before an attempt exists.
    public string? Route { get; set; }
    public Guid? AttemptId { get; set; }
    public Guid RouteConfigRevision { get; set; }
    public string RouteConsentVersion { get; set; } = "";
    public string? ExecutionMode { get; set; }
    public Guid? ParentTaskId { get; set; }
    public string? ReprocessReason { get; set; }
    public bool CompletionAcknowledged { get; set; }
    public int SamplesPerCall { get; set; }
}

internal sealed class NoteAsrRetryIntent
{
    public string Fingerprint { get; set; } = "";
    public Guid AttemptId { get; set; }
    public Guid TaskId { get; set; }
    public Guid VersionId { get; set; }
    public string InputSha256 { get; set; } = "";
    public string Route { get; set; } = "";
    public string AttemptState { get; set; } = "";
    public bool AttemptStopRequested { get; set; }
    public long FencingToken { get; set; }
    public DateTimeOffset AttemptUpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class NoteAsrStep
{
    public int Index { get; set; }
    public Guid RecordingId { get; set; }
    public int RecordingSegment { get; set; }
    public int ChunkIndex { get; set; }
    public int FileIndex { get; set; }
    public required string Path { get; set; }
    public required string Sha256 { get; set; }
    public long SourceOffset { get; set; }
    public long RecordingOffset { get; set; }
    public long TimelineOffset { get; set; }
    public long Samples { get; set; }
    public List<NoteAsrAttempt> Attempts { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public NoteAsrAttempt Current => Attempts[^1];
}

internal sealed class NoteAsrAttempt
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid? LogId { get; set; }
    public Guid? EvidenceId { get; set; }
    public string Status { get; set; } = "Queued";
    public string? ErrorMessage { get; set; }
    // Adaptive retries stay children of the top-level segment attempt so Current and retry
    // history keep their existing meaning.
    public long SourceOffsetSamples { get; set; }
    public long Samples { get; set; }
    public int AdaptiveDepth { get; set; }
    public string? AdaptiveBranch { get; set; }
    public List<NoteAsrAttempt> Children { get; set; } = [];
}
