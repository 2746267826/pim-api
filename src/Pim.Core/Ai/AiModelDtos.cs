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

    public static int ToFlag(AiModelCapability capability) => capability switch
    {
        AiModelCapability.Text => Text,
        AiModelCapability.Vision => Vision,
        AiModelCapability.Transcription => Transcription,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown AI capability")
    };

    public static bool Has(int flags, AiModelCapability capability) => (flags & ToFlag(capability)) != 0;
}

public sealed record AiModelDto(Guid Id, Guid Revision, string Name, string BaseUrl, string Model,
    bool Enabled, IReadOnlyList<AiModelCapability> Capabilities, bool HasApiKey, int TimeoutSeconds,
    int MaxOutputTokens, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AsrPresetId = null,
    string? AsrBatchBaseUrl = null, string? AsrPrompt = null, string? AsrPromptRevision = null,
    Guid? ProviderConnectionId = null);

public sealed record SaveAiModelRequest(string Name, string BaseUrl, string Model, bool Enabled,
    IReadOnlyList<AiModelCapability> Capabilities, int TimeoutSeconds = 60, int MaxOutputTokens = 1024,
    string? ApiKey = null, Guid? ExpectedRevision = null, string? AsrPresetId = null,
    string? AsrBatchBaseUrl = null, string? AsrPrompt = null, Guid? ProviderConnectionId = null)
{
    public override string ToString() => $"SaveAiModelRequest {{ Name = {Name}, Model = {Model}, ApiKey = [protected] }}";
}

public sealed record AiModelSettingsDto(Guid Revision, bool Enabled, Guid? TextModelId,
    Guid? VisionModelId, Guid? TranscriptionModelId, Guid? NoteOrganizationModelId = null,
    Guid? NoteTodoModelId = null, Guid? CalendarPlanningModelId = null);

/// <summary>
/// Settings patch. Legacy clients omit the purpose fields; an omitted field keeps the stored
/// value, while an explicit <c>Guid.Empty</c> clears it. The <c>Has...</c> flags are populated
/// by the JSON deserializer only when the caller actually sent that property.
/// </summary>
public sealed record SaveAiModelSettingsRequest(Guid ExpectedRevision, bool Enabled, Guid? TextModelId,
    Guid? VisionModelId, Guid? TranscriptionModelId)
{
    private Guid? _noteOrganizationModelId;
    private Guid? _noteTodoModelId;
    private Guid? _calendarPlanningModelId;

    public Guid? NoteOrganizationModelId
    {
        get => _noteOrganizationModelId;
        init { _noteOrganizationModelId = value; HasNoteOrganizationModelId = true; }
    }

    public Guid? NoteTodoModelId
    {
        get => _noteTodoModelId;
        init { _noteTodoModelId = value; HasNoteTodoModelId = true; }
    }

    public Guid? CalendarPlanningModelId
    {
        get => _calendarPlanningModelId;
        init { _calendarPlanningModelId = value; HasCalendarPlanningModelId = true; }
    }

    [JsonIgnore] public bool HasNoteOrganizationModelId { get; private init; }
    [JsonIgnore] public bool HasNoteTodoModelId { get; private init; }
    [JsonIgnore] public bool HasCalendarPlanningModelId { get; private init; }
}

/// <summary>Stable purpose keys used for per-purpose default model selection.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiPurpose
{
    NoteOrganization,
    NoteTodo,
    CalendarPlanning
}

public sealed record AiTextCallRequest(Guid ClientRequestId, Guid ExpectedUserId, string Prompt, string? SystemPrompt = null,
    Guid? ModelId = null)
{
    public override string ToString() => $"AiTextCallRequest {{ ClientRequestId = {ClientRequestId}, ModelId = {ModelId}, PromptChars = {Prompt?.Length ?? 0} }}";
}

public sealed record AiTextCallResultDto(Guid LogId, AiRequestStatus Status, string? ResponseText,
    string? ErrorMessage, bool Replayed, AiAsrJobDiagnosticsDto? Diagnostics = null);

/// <summary>Server-side purpose call used by modules that own their prompt and fallback rules.</summary>
public interface IAiPurposeTextCallService
{
    Task<AiTextCallResultDto> CallForPurposeAsync(AiPurpose purpose, AiTextCallRequest request,
        CancellationToken ct);
}
