using System.Linq;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpWriteEndpointMapTests
{
    [Fact]
    public void EveryWriteTool_HasAnAllowedEndpoint()
    {
        var tools = McpToolCatalog.WriteTools.Select(t => t.Name).ToList();
        Assert.Equal(50, tools.Count);

        // Every write tool must be reachable through its McpToolTable route with the mapped
        // endpoint (IsAllowedForTool must accept the exact route the executor dispatches to).
        foreach (var tool in tools)
        {
            var spec = McpToolTable.TryGet(tool)
                ?? throw new Xunit.Sdk.XunitException($"write tool {tool} missing from McpToolTable");
            var path = spec.Route;
            Assert.True(McpWriteEndpointMap.IsAllowedForTool(tool, spec.Method, path),
                $"{tool}: {spec.Method} {path} not allowed by McpWriteEndpointMap");
        }
    }

    [Fact]
    public void WriteEndpoints_AreRecognized()
    {
        Assert.True(McpWriteEndpointMap.IsWriteEndpoint("POST", "/api/v1/calendar/tasks"));
        Assert.True(McpWriteEndpointMap.IsWriteEndpoint("DELETE", "/api/v1/calendar/events/some-guid"));
        Assert.True(McpWriteEndpointMap.IsWriteEndpoint("PUT", "/api/v1/calendar/events/some-guid"));
        Assert.True(McpWriteEndpointMap.IsWriteEndpoint("POST", "/api/v1/quick-notes"));
        Assert.True(McpWriteEndpointMap.IsWriteEndpoint("POST", "/api/v1/files/items/upload"));
        Assert.True(McpWriteEndpointMap.IsWriteEndpoint("DELETE", "/api/v1/mobile/analytics/goals/g1"));
    }

    [Fact]
    public void ReadEndpoints_AreNotWriteEndpoints()
    {
        Assert.False(McpWriteEndpointMap.IsWriteEndpoint("GET", "/api/v1/calendar/events"));
        Assert.False(McpWriteEndpointMap.IsWriteEndpoint("GET", "/api/v1/calendar/tasks"));
        Assert.False(McpWriteEndpointMap.IsWriteEndpoint("GET", "/api/v1/pc/summary"));
        Assert.False(McpWriteEndpointMap.IsWriteEndpoint("GET", "/api/version"));
    }

    [Fact]
    public void ToolScope_AllowsOnlyItsOwnEndpoint()
    {
        // create_task maps to POST /api/v1/calendar/tasks
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("create_task", "POST", "/api/v1/calendar/tasks"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("create_task", "POST", "/api/v1/quick-notes"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("create_task", "DELETE", "/api/v1/calendar/tasks/x"));
        // delete_task maps to DELETE /api/v1/calendar/tasks/{id}
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("delete_task", "DELETE", "/api/v1/calendar/tasks/x"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("delete_task", "GET", "/api/v1/calendar/tasks/x"));
        // update_event maps to PUT /api/v1/calendar/events/{id}
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("update_event", "PUT", "/api/v1/calendar/events/x"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("update_event", "PUT", "/api/v1/calendar/events/x/restore"));
    }
}
