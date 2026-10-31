using System.Text.Json.Serialization;

namespace Pim.Core.Ai;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiModelCapability { Text, Vision, Transcription }

/// <summary>Stored capability bit flags (the enum above is not the persistence representation).</summary>
public static class AiModelCapabilityFlags
{
    public const int Text = 1;
    public const int Vision = 2;
    public const int Transcription = 4;
}

public sealed record AiModelDto(Guid Id, Guid Revision, string Name, string BaseUrl, string Model,
    bool Enabled, IReadOnlyList<AiModelCapability> Capabilities, bool HasApiKey, int TimeoutSeconds,
    int MaxOutputTokens, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AsrPresetId = null,
    string? AsrBatchBaseUrl = null, string? AsrPrompt = null, string? AsrPromptRevision = null);

public sealed record SaveAiModelRequest(string Name, string BaseUrl, string Model, bool Enabled,
    IReadOnlyList<AiModelCapability> Capabilities, int TimeoutSeconds = 60, int MaxOutputTokens = 1024,
    string? ApiKey = null, Guid? ExpectedRevision = null, string? AsrPresetId = null,
    string? AsrBatchBaseUrl = null, string? AsrPrompt = null)
{
    public override string ToString() => $"SaveAiModelRequest {{ Name = {Name}, Model = {Model}, ApiKey = [protected] }}";
}

public sealed record AiModelSettingsDto(Guid Revision, bool Enabled, Guid? TextModelId,
    Guid? VisionModelId, Guid? TranscriptionModelId);

public sealed record SaveAiModelSettingsRequest(Guid ExpectedRevision, bool Enabled, Guid? TextModelId,
    Guid? VisionModelId, Guid? TranscriptionModelId);

public sealed record AiTextCallRequest(Guid ClientRequestId, Guid ExpectedUserId, string Prompt, string? SystemPrompt = null,
    Guid? ModelId = null)
{
    public override string ToString() => $"AiTextCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, PromptChars = {Prompt?.Length ?? 0} }}";
}

public sealed record AiTextCallResultDto(Guid LogId, AiRequestStatus Status, string? ResponseText,
    string? ErrorMessage, bool Replayed);
