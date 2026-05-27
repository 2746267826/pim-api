using System.Text.Json;

namespace Pim.Core.Ai;

public sealed record AiGatewayMessage(string Role, string Content);

public sealed record AiGatewayRequest(
    Guid UserId,
    string Module,
    string Purpose,
    string SourceObjectType,
    Guid SourceObjectId,
    IReadOnlyList<AiGatewayMessage> Messages,
    string? Model = null,
    string? SchemaName = null,
    string? SchemaVersion = null,
    int? MaxOutputTokens = null,
    int MaxAttempts = 1,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AiTokenUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

public sealed record AiGatewayResult(
    string Status,
    string? ResponseText,
    JsonDocument? ParsedOutput,
    IReadOnlyList<string> SchemaValidationErrors,
    AiTokenUsage? TokenUsage,
    Guid? LogId,
    string? UserFacingError,
    string? Model);

public interface IAiGateway
{
    Task<AiGatewayResult> SendAsync(AiGatewayRequest request, CancellationToken ct = default);
}

public sealed record AiSchemaDefinition(
    string Name,
    string Version,
    string JsonSchema,
    int MaxOutputTokens,
    int MaxAttempts);

public interface IAiSchemaRegistry
{
    void Register(AiSchemaDefinition schema);
    AiSchemaDefinition? Find(string name, string version);
}
