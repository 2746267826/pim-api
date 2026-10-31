using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>Values explicitly documented for MiMo Batch; do not treat them as runtime-verified guarantees.</summary>
public static class MimoBatchDefaults
{
    public const long MaxJsonlBytes = 128L * 1024 * 1024;
    public const string CompletionWindow = "24h";
    public const string ChatCompletionsEndpoint = "/v1/chat/completions";
}

public enum MimoBatchStatusKind
{
    Validating,
    InProgress,
    Completed,
    Failed,
    Expired,
    Cancelling,
    Cancelled,
    Unknown
}

public static class MimoBatchStatus
{
    private static readonly IReadOnlyDictionary<string, MimoBatchStatusKind> Known =
        new Dictionary<string, MimoBatchStatusKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["validating"] = MimoBatchStatusKind.Validating,
            ["in_progress"] = MimoBatchStatusKind.InProgress,
            ["completed"] = MimoBatchStatusKind.Completed,
            ["failed"] = MimoBatchStatusKind.Failed,
            ["expired"] = MimoBatchStatusKind.Expired,
            ["cancelling"] = MimoBatchStatusKind.Cancelling,
            ["cancelled"] = MimoBatchStatusKind.Cancelled
        };

    public static MimoBatchStatusKind Normalize(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Known.TryGetValue(raw.Trim(), out var kind)
            ? kind
            : MimoBatchStatusKind.Unknown;

    public static bool IsTerminal(MimoBatchStatusKind kind) =>
        kind is MimoBatchStatusKind.Completed or MimoBatchStatusKind.Failed
            or MimoBatchStatusKind.Expired or MimoBatchStatusKind.Cancelled;
}

/// <summary>Per-file expiry from <c>expire_at</c>; distinct from a batch's <c>expires_at</c>.</summary>
public sealed record MimoFileRef(string FileId, DateTimeOffset? ExpireAt);

/// <summary>Batch handle keeps raw status for audit and a normalized kind for branching.</summary>
public sealed record MimoBatchRef(string BatchId, string StatusRaw, MimoBatchStatusKind Status,
    DateTimeOffset? ExpiresAt, int? Total, int? Completed, int? Failed)
{
    public bool IsTerminal => MimoBatchStatus.IsTerminal(Status);
}

public enum MimoBatchItemDisposition
{
    /// <summary>HTTP 200 with a response body, but the body has not been accepted as ASR output.</summary>
    TransportSucceeded,
    /// <summary>Passed the same audio-token/empty/repetition/truncation checks as synchronous ASR.</summary>
    AsrSucceeded,
    /// <summary>HTTP 200 but failed ASR validation; raw body is retained for inspection.</summary>
    AsrInvalid,
    Failed,
    Empty,
    Missing,
    Duplicate,
    UnknownCustomId,
    NonSuccessStatus
}

public sealed record MimoBatchItemOutcome(string CustomId, MimoBatchItemDisposition Disposition,
    int? StatusCode, string? Content, string? RawBody, string? ErrorCode, string? ErrorMessage,
    bool AsrValidated, string? AsrErrorCode, int Occurrences);

public sealed record MimoBatchReconciliation(IReadOnlyList<MimoBatchItemOutcome> Items,
    IReadOnlyList<string> MissingCustomIds, IReadOnlyList<string> DuplicateCustomIds,
    IReadOnlyList<string> UnknownCustomIds, int MalformedLines)
{
    public bool HasFailures => MalformedLines > 0 || Items.Any(x => x.Disposition != MimoBatchItemDisposition.AsrSucceeded);
}

/// <summary>Outcome of a create attempt. Unknown means no verified handle exists; never retry it blindly.</summary>
public abstract record MimoBatchCreateOutcome
{
    public sealed record Known(MimoBatchRef Batch) : MimoBatchCreateOutcome;
    public sealed record Unknown(string Reason) : MimoBatchCreateOutcome;
}

/// <summary>Immutable intent snapshot supplied by the routing layer when a handle is persisted.</summary>
public sealed record MimoBatchIntent(Guid Owner, string MaterialVersion, string Segment, int Attempt,
    string ProviderHandle, string CredentialConfigId, int CredentialConfigRevision, string BaseUrl,
    string Model, string CompletionWindow, string ConsentRef);

public sealed record MimoBatchClientOptions(Uri BaseAddress, TimeSpan CreateTimeout,
    long MaxResponseBytes = 64L * 1024 * 1024, long MaxJsonlBytes = MimoBatchDefaults.MaxJsonlBytes)
{
    public static MimoBatchClientOptions Default(Uri baseAddress) =>
        new(baseAddress, TimeSpan.FromSeconds(60));
}

public sealed class MimoBatchException(int errorCode, string message) : DomainException(errorCode, message);
