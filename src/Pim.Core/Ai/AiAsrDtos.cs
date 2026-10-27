namespace Pim.Core.Ai;

public sealed record AiAsrPresetDto(string Id, string Version, string Name, string DefaultBaseUrl,
    string OperationPath, string DefaultModel, IReadOnlyList<string> Models, IReadOnlyList<string> Formats,
    IReadOnlyList<string> Languages, int MaxAudioBytes, bool Streaming);

public sealed record AiAsrAudioRequest(string Name, string Format, string Base64)
{
    public override string ToString() => $"AiAsrAudioRequest {{ Name = {Name}, Format = {Format}, Data = [audio] }}";
}

public sealed record AiAsrDestinationConsent(Guid ModelId, Guid ModelRevision, string Endpoint,
    string PresetId, string PresetVersion, bool Confirmed);

public sealed record AiAsrCallRequest(Guid ClientRequestId, Guid ExpectedUserId, AiAsrAudioRequest Audio,
    AiAsrDestinationConsent Consent, string Language = "auto", Guid? ModelId = null)
{
    public override string ToString() => $"AiAsrCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, Data = [audio] }}";
}

public sealed record AiCallAudioDto(Guid Id, string Name, string ContentType, long SizeBytes, string Sha256, bool Stored);
