namespace Pim.Module.Mcp.DTOs;

/// <summary>One entry of the embedded 151-tool wire contract (dumped from the Python reference).</summary>
public sealed class McpToolContract
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public System.Text.Json.JsonElement InputSchema { get; set; }
}

public sealed record McpToolInfo(string Name, string Group, string Description, bool IsWrite);

public sealed record McpCatalogDto(List<McpToolInfo> Read, List<McpToolInfo> Write);

public sealed record McpClientDto(
    Guid Id,
    string Name,
    string Status,
    string TokenPrefix,
    Dictionary<string, Dictionary<string, bool>> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastSeenAt,
    long CallCount,
    long WriteCallCount,
    string? LastTool,
    bool Online,
    string? CreatedByUsername);

/// <summary>Create response carries the plain token exactly once. Never persisted.</summary>
public sealed record McpClientCreateResult(McpClientDto Client, string Token);

public sealed record McpCreateClientRequest(string? Name);

public sealed record McpClientUpdateRequest(
    string? Name,
    Dictionary<string, Dictionary<string, bool>>? Permissions);

/// <summary>Payload sent by the MCP server on every tool call.</summary>
public sealed record McpVerifyRequest(
    string? Tool,
    string? ParamsSummary);

/// <summary>Successful verification: permission set + short-lived user JWT for REST passthrough.</summary>
public sealed record McpVerifyResult(
    Guid ClientId,
    string ClientName,
    Guid UserId,
    Dictionary<string, Dictionary<string, bool>> Permissions,
    string AccessToken,
    bool IsWrite);

/// <summary>In-memory record of recent MCP tool calls, kept for the WebUI activity view.</summary>
public sealed record McpActivityEntry(
    DateTimeOffset Timestamp,
    string ClientName,
    string ToolName,
    int StatusCode,
    long DurationMs,
    string ArgumentsSummary,
    Guid? OwnerUserId = null);
