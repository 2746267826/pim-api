using System.Linq;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpReadEndpointPolicyTests
{
    [Fact]
    public void ReadTokens_AllowGetOnApi()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("GET", "/api/v1/calendar/tasks"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("GET", "/api/v1/pc/summary"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("GET", "/api/version"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get", "/api/v1/calendar/events/"));
    }

    [Fact]
    public void ReadTokens_AllowRootHealthProbe()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("GET", "/health"));
    }

    [Fact]
    public void ReadTokens_AllowReadSemanticPosts()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/data-center/query"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/data-center/batch/preview"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/data-center/restore/preview"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/recycle-bin/event/x/restore-preview"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/schedule"));
    }

    [Fact]
    public void ReadTokens_DenyWrites_IncludingUnmappedHighRisk()
    {
        // High-risk write not in the 50-tool map — must still be denied for read tokens.
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/data-center/batch/execute"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/calendar/events"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("PUT", "/api/v1/calendar/events/x"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("DELETE", "/api/v1/calendar/tasks/x"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/pc/classification/rules"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/files/items/upload"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("GET", "/not-an-api"));
    }

    [Fact]
    public void WriteTokenScope_IsCaseAndSlashInsensitive()
    {
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("create_task", "POST", "/api/v1/calendar/tasks"));
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("create_task", "POST", "/API/V1/CALENDAR/TASKS"));
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("delete_event", "DELETE", "/api/v1/calendar/events/x"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("create_task", "POST", "/api/v1/calendar/tasks/x"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("create_task", "POST", "/api/v1/quick-notes"));
    }
}
