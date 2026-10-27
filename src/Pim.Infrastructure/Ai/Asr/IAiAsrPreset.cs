using Pim.Core.Ai;

namespace Pim.Infrastructure.Ai.Asr;

public sealed record AiAsrInput(string Name, string Format, byte[] Bytes, string Sha256)
{
    public string ContentType => Format == "wav" ? "audio/wav" : "audio/mpeg";
    public override string ToString() => $"AiAsrInput {{ Name = {Name}, Format = {Format}, Bytes = {Bytes.Length} }}";
}

public sealed record AiAsrProviderResult(string Text, string? FinishReason, long? InputTokens, long? OutputTokens,
    long? TotalTokens, string RawJson, bool HasTranscript, double? ReportedSeconds);

/// <summary>Trusted provider strategy: protocol differences stay behind one adapter, not controller/UI switches.</summary>
public interface IAiAsrPreset
{
    AiAsrPresetDto Descriptor { get; }
    string NormalizeEndpoint(string value);
    void ValidateModel(string model);
    string AuditRequest(byte[] sdkBody, AiModelConnection connection, AiAsrInput audio, Guid audioId, string language);
    Task<AiAsrProviderResult> TranscribeAsync(AiModelConnection connection, AiAsrInput audio, string language, CancellationToken ct);
}
