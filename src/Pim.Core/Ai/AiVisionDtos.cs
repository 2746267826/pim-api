namespace Pim.Core.Ai;

public sealed record AiVisionImageRequest(string Name, string ContentType, string Base64)
{
    public override string ToString() => $"AiVisionImageRequest {{ Name = {Name}, ContentType = {ContentType}, Content = [image] }}";
}

public sealed record AiVisionDestinationConsent(Guid ModelId, Guid ModelRevision, string Endpoint, bool Confirmed);

public sealed record AiVisionCallRequest(Guid ClientRequestId, Guid ExpectedUserId, string Prompt,
    int ImageCount, IReadOnlyList<AiVisionImageRequest> Images, AiVisionDestinationConsent Consent,
    string? SystemPrompt = null, Guid? ModelId = null)
{
    public override string ToString() => $"AiVisionCallRequest {{ ClientRequestId = {ClientRequestId}, ImageCount = {ImageCount}, PromptChars = {Prompt?.Length ?? 0} }}";
}

public sealed record AiCallImageDto(Guid Id, int Index, string Name, string ContentType, long SizeBytes,
    string Sha256, bool Stored);
