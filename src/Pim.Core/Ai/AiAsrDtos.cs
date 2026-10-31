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

/// <summary>Immutable rendered ASR prompt revision shared by synchronous and Batch calls.</summary>
public sealed record AiAsrPromptSnapshot(
    string Text,
    string Language,
    string Sha256,
    string Revision,
    string TemplateSha256,
    string TemplateRevision)
{
    public override string ToString() =>
        $"AiAsrPromptSnapshot {{ Language = {Language}, Revision = {Revision}, Chars = {Text.Length} }}";
}

public sealed record AiAsrCallRequest(Guid ClientRequestId, Guid ExpectedUserId, AiAsrAudioRequest Audio,
    AiAsrDestinationConsent Consent, string Language = "auto", Guid? ModelId = null,
    AiAsrPromptSnapshot? Prompt = null)
{
    public override string ToString() => $"AiAsrCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, Data = [audio] }}";
}

/// <summary>
/// Public HTTPS audio source. Explicit confirmations acknowledge both the source read and that
/// the provider will fetch that URL again; the preflight digest cannot attest the provider copy.
/// </summary>
public sealed record AiAsrUrlAudioRequest(string Name, string Format, string Url, string? ExpectedSha256,
    bool SourceConfirmed, bool ProviderFetchConfirmed)
{
    public static bool IsSha256(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    public override string ToString() =>
        $"AiAsrUrlAudioRequest {{ Name = [redacted], Format = {SafeFormat(Format)}, Url = [redacted], " +
        $"ExpectedSha256 = {(IsSha256(ExpectedSha256) ? ExpectedSha256 : "[not-printed]")} }}";

    private static string SafeFormat(string? value) =>
        value is "wav" or "mp3" or "flac" or "m4a" or "ogg" ? value : "[invalid]";
}

public sealed record AiAsrUrlCallRequest(Guid ClientRequestId, Guid ExpectedUserId, AiAsrUrlAudioRequest Audio,
    AiAsrDestinationConsent Consent, string Language = "auto", Guid? ModelId = null,
    AiAsrPromptSnapshot? Prompt = null)
{
    public override string ToString() =>
        $"AiAsrUrlCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, Source = [public-url] }}";
}

/// <summary>
/// URL-mode result. Its 100 MiB source limit is a decoded-byte budget, not a process peak-memory
/// guarantee; the preflight digest also cannot prove the provider fetched the same bytes later.
/// </summary>
public sealed record AiAsrUrlCallResultDto(Guid LogId, AiRequestStatus Status, string? ResponseText,
    string? ErrorMessage, bool Replayed, string? SourceOrigin, string? PreflightSha256,
    bool PreflightVerified, bool ProviderFetchVerified, string Warning);

public sealed record AiCallAudioDto(Guid Id, string Name, string ContentType, long SizeBytes, string Sha256, bool Stored);
