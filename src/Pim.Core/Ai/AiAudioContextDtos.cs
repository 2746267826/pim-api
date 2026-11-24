namespace Pim.Core.Ai;

/// <summary>
/// Exact fixed-material audio slice admitted to a generation call. Identity fields are
/// server-derived from the immutable material manifest; callers never provide paths or tokens.
/// </summary>
public sealed record AiAudioContextInput(
    string Name,
    string ContentType,
    string SourceSha256,
    string Sha256,
    long SizeBytes,
    Guid OwnerId,
    Guid NoteId,
    Guid SourceVersionId,
    Guid RecordingId,
    int RecordingSegment,
    int ChunkIndex,
    int FileIndex,
    long SourceOffsetSamples,
    long Samples,
    string SourcePath,
    byte[] Bytes)
{
    public override string ToString() =>
        $"AiAudioContextInput {{ Name = {Name}, OwnerId = {OwnerId}, NoteId = {NoteId}, " +
        $"SourceVersionId = {SourceVersionId}, RecordingId = {RecordingId}, " +
        $"Samples = {Samples}, SourceSha256 = {SourceSha256}, Sha256 = {Sha256}, Data = [audio] }}";
}
