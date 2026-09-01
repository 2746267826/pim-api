namespace Pim.Module.Mcp.Entities;

/// <summary>
/// An MCP client connection. One client = one token = one permission set.
/// Token is never stored in plain text; only its SHA-256 hash and a short display prefix.
/// </summary>
public class McpClientEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name, unique per owner.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the raw <c>pim_mcp_*</c> token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>First 12 chars of the raw token (e.g. <c>pim_mcp_ab12</c>) for display only.</summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>Tool-level permissions: <c>{read: {tool: bool}, write: {tool: bool}}</c>.</summary>
    public Dictionary<string, Dictionary<string, bool>> Permissions { get; set; } = new();

    /// <summary>active / revoked.</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Last successful call time (updated by /verify).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Total calls (read + write).</summary>
    public long CallCount { get; set; }

    /// <summary>Write calls only.</summary>
    public long WriteCallCount { get; set; }

    /// <summary>Most recently invoked tool name.</summary>
    public string? LastTool { get; set; }

    /// <summary>User that created this client.</summary>
    public Guid CreatedBy { get; set; }
}
