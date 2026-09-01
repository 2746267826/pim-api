using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Captures the Pim.Api request pipeline for in-process MCP dispatch WITHOUT starting
/// the host (no sockets, no hosted services). Replicates what the GenericWebHost
/// startup does for a WebApplication (see WebApplicationBuilder.ConfigureApplication +
/// WireSourcePipeline in aspnetcore v8):
/// <list type="bullet">
/// <item>the app's own middleware chain is appended with <c>UseEndpoints</c> (endpoint execution);</item>
/// <item>the whole thing is wrapped in <c>UseRouting</c> on an outer builder whose
/// global route builder is the WebApplication itself (its DataSources carry every mapped endpoint).</item>
/// </list>
/// Verified behaviorally against the real host: literal routes beat fallbacks, 404/405
/// semantics, auth/scope middleware ordering, SPA fallback.
/// </summary>
public static class McpInProcessPipeline
{
    // Internal framework property keys (WebApplication.GlobalEndpointRouteBuilderKey /
    // EndpointRouteBuilderKey in Microsoft.AspNetCore.Routing).
    private const string GlobalEndpointRouteBuilderKey = "__GlobalEndpointRouteBuilder";
    private const string EndpointRouteBuilderKey = "__EndpointRouteBuilder";

    /// <summary>
    /// Builds the full request pipeline (middleware chain + routing + endpoints + fallbacks).
    /// Must be called after all endpoints are mapped (Program.cs end) and before the first
    /// in-process dispatch.
    /// </summary>
    public static RequestDelegate BuildPipeline(WebApplication app)
    {
        // 1) Endpoint execution is wired INTO the app's own middleware chain (WireSourcePipeline parity):
        //    [user middleware..., EndpointMiddleware, terminal]. UseEndpoints resolves its route
        //    builder from the __EndpointRouteBuilder property.
        var tail = new ApplicationBuilder(app.Services);
        tail.Properties[EndpointRouteBuilderKey] = app;
        tail.UseEndpoints(_ => { });
        var tailPipeline = tail.Build();
        app.Use(_ => tailPipeline);

        // 2) The app's own pipeline (includes every app.Use + the tail above).
        var inner = ((IApplicationBuilder)app).Build();

        // 3) Routing wraps the inner pipeline (ConfigureApplication parity): UseRouting matches
        //    against the WebApplication's DataSources (global route builder property).
        var outer = new ApplicationBuilder(app.Services);
        outer.Properties[GlobalEndpointRouteBuilderKey] = app;
        outer.UseRouting();
        outer.Run(inner);
        return outer.Build();
    }
}
