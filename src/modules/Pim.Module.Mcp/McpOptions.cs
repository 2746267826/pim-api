namespace Pim.Module.Mcp;

/// <summary>
/// Configuration for the in-process MCP server (ticket pim-mcp-inline-20260901).
/// Bound from the "MCP" configuration section.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "MCP";

    /// <summary>Master switch; when false the /mcp endpoint is not mapped (stdio still honors --mcp-stdio).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Streamable HTTP endpoint path. Default <c>/mcp</c> (matches Hermes config and the retired Python service).</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>Streamable HTTP session idle timeout before the server prunes the session.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Per-call HTTP timeout for in-process REST dispatch (upper bound safety net).</summary>
    public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
