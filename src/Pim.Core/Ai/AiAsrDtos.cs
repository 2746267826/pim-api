using System.Text.Json.Serialization;

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
    AiAsrPromptSnapshot? Prompt = null, bool ExplicitCancel = false)
{
    public override string ToString() => $"AiAsrCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, Data = [audio], ExplicitCancel = {ExplicitCancel} }}";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiAsrJobStatus
{
    Queued,
    Running,
    Cancelling,
    Succeeded,
    Incomplete,
    Failed,
    Cancelled,
    Blocked,
    TimedOut
}

public sealed record AiAsrJobDiagnosticsDto(
    string AbortSource,
    bool OutcomeKnown,
    int? GatewayStatus,
    string? GatewayName,
    string? GatewayEvidenceSource,
    string? TransportObservationKind,
    DateTimeOffset? GatewayObservedAt,
    int? UpstreamStatus,
    bool ProviderAttempted,
    bool ResponseBodyComplete,
    int MaxOutputTokens,
    double? EstimatedAudioSeconds,
    int? EstimatedCompletionTokens,
    bool OutputLimitExceeded,
    string? NextAction);

/// <summary>
/// Durable asynchronous ASR facade. ClientRequestId is both the owner-scoped idempotency key and
/// the polling key; LogId points to the provider audit record consumed by the existing detail UI.
/// </summary>
public sealed record AiAsrJobResultDto(
    Guid ClientRequestId,
    Guid LogId,
    AiAsrJobStatus Status,
    string? ResponseText,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CancelRequestedAt,
    DateTimeOffset? FinishedAt,
    bool Replayed,
    AiAsrJobDiagnosticsDto Diagnostics);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiAsrTransportKind
{
    AsyncSubmit,
    AsyncPoll,
    SyncFileCall,
    SyncUrlCall
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiAsrTransportObservationSource
{
    ClientHttpResponse,
    ClientNetworkFailure
}

/// <summary>
/// Browser-observed transport evidence. HTTP response observations require a gateway-class
/// status and may include the response's gateway/server header. Socket/network failures carry no
/// HTTP status, so they cannot be presented as proof of a gateway response.
/// </summary>
public sealed record AiAsrTransportObservationRequest(
    Guid ClientRequestId,
    Guid ExpectedUserId,
    AiAsrTransportKind TransportKind,
    AiAsrTransportObservationSource Source,
    int? GatewayStatus = null,
    string? GatewayName = null);

public sealed record AiAsrTransportObservationDto(
    Guid ClientRequestId,
    Guid LogId,
    AiAsrTransportKind TransportKind,
    AiAsrTransportObservationSource Source,
    int? GatewayStatus,
    string? GatewayName,
    DateTimeOffset ObservedAt);

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
    AiAsrPromptSnapshot? Prompt = null, bool ExplicitCancel = false)
{
    public override string ToString() =>
        $"AiAsrUrlCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, Source = [public-url], ExplicitCancel = {ExplicitCancel} }}";
}

/// <summary>
/// URL-mode result. Its 100 MiB source limit is a decoded-byte budget, not a process peak-memory
/// guarantee; the preflight digest also cannot prove the provider fetched the same bytes later.
/// </summary>
public sealed record AiAsrUrlCallResultDto(Guid LogId, AiRequestStatus Status, string? ResponseText,
    string? ErrorMessage, bool Replayed, string? SourceOrigin, string? PreflightSha256,
    bool PreflightVerified, bool ProviderFetchVerified, string Warning,
    AiAsrJobDiagnosticsDto? Diagnostics = null);

public sealed record AiCallAudioDto(Guid Id, string Name, string ContentType, long SizeBytes, string Sha256, bool Stored);
