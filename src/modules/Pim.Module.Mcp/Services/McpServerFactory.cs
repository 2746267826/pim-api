using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Builds the in-process MCP server on the official Microsoft <c>ModelContextProtocol</c>
/// SDK. The tool registry is shared by the Streamable HTTP transport (/mcp) and the
/// stdio transport (--mcp-stdio); tools/list serves the embedded 151-tool contract
/// verbatim and tools/call routes through <see cref="McpToolExecutor"/>.
/// </summary>
public static class McpServerFactory
{
    public const string ServerName = "pim-mcp-server";
    public const string ServerVersion = "2.0.0";

    public static IServiceCollection AddPimMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<McpOptions>().Bind(configuration.GetSection(McpOptions.SectionName));
        services.AddSingleton<McpInProcessDispatcher>();
        services.AddSingleton(sp => new McpInProcessClient(
            sp.GetRequiredService<McpInProcessDispatcher>(),
            sp.GetRequiredService<IOptions<McpOptions>>().Value.DispatchTimeout));
        services.AddSingleton<McpStdioTokenSource>();

        services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion };
            options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability() };
            options.Handlers.ListToolsHandler = ListTools;
            options.Handlers.CallToolHandler = CallTool;
        }).WithHttpTransport(transport =>
        {
            transport.IdleTimeout = TimeSpan.FromMinutes(30);
        });

        return services;
    }

    /// <summary>Builds the shared server options (also used by the stdio transport).</summary>
    public static McpServerOptions CreateOptions()
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        };
        options.Handlers.ListToolsHandler = ListTools;
        options.Handlers.CallToolHandler = CallTool;
        return options;
    }

    private static ValueTask<ListToolsResult> ListTools(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        var tools = new List<Tool>(McpToolExecutor.ToolContract.Count);
        foreach (var contract in McpToolExecutor.ToolContract)
        {
            tools.Add(new Tool
            {
                Name = contract.Name,
                Description = contract.Description,
                InputSchema = contract.InputSchema,
            });
        }
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    private static ValueTask<CallToolResult> CallTool(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var services = request.Services;
        var executor = new McpToolExecutor(
            services.GetRequiredService<McpClientService>(),
            services.GetRequiredService<McpInProcessClient>(),
            services.GetRequiredService<McpStdioTokenSource>(),
            services.GetService<IHttpContextAccessor>()?.HttpContext);
        return executor.ExecuteAsync(request.Params, ct);
    }
}