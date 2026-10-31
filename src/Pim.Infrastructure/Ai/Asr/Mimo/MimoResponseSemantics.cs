namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>
/// Shared result validation. HTTP 200 and non-empty text alone are not a usable ASR success:
/// canonical audio usage must be positive and the transcript must not be empty/repetitive/truncated.
/// </summary>
public sealed record MimoResponseSemantics(string Text, string ContentSource, long? AudioTokens,
    string? FinishReasonRaw, bool HasTranscript, bool Repetition, bool Truncation)
{
    public bool Valid => AudioTokens is > 0 && HasTranscript && !Repetition && !Truncation;

    public string? ErrorCode => AudioTokens is null or <= 0 ? "audio_tokens_missing"
        : !HasTranscript ? "empty"
        : Repetition ? "repetition"
        : Truncation ? "truncation"
        : null;
}
