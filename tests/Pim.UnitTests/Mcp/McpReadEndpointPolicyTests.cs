using System.Linq;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpReadEndpointPolicyTests
{
    [Fact]
    public void ReadTokens_AllowGetOnApi()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "GET", "/api/v1/calendar/tasks"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "GET", "/api/v1/pc/summary"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "GET", "/api/version"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_events", "get", "/api/v1/calendar/events/"));
    }

    [Fact]
    public void ReadTokens_AllowRootHealthProbe()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_system_health", "GET", "/health"));
    }

    [Fact]
    public void ReadTokens_AllowReadSemanticPosts()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/data-center/query"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/data-center/batch/preview"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/data-center/restore/preview"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/recycle-bin/event/x/restore-preview"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/schedule"));
    }

    [Fact]
    public void ReadTokens_DenyWrites_IncludingUnmappedHighRisk()
    {
        // High-risk write not in the 50-tool map — must still be denied for read tokens.
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/data-center/batch/execute"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/calendar/events"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "PUT", "/api/v1/calendar/events/x"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "DELETE", "/api/v1/calendar/tasks/x"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/pc/classification/rules"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "POST", "/api/v1/files/items/upload"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_tasks", "GET", "/not-an-api"));
    }

    [Fact]
    public void GenericReadTokens_CannotAccessAnyNativeNoteRoute()
    {
        foreach (var tool in new[] { "get_tasks", "get_events", "get_quick_notes" })
        {
            foreach (var path in new[]
                     {
                         "/api/v1/native-notes",
                         "/api/v1/native-notes/mcp/notes",
                         "/api/v1/native-notes/mcp/notes/note-1",
                         "/api/v1/native-notes/note-1/current",
                         "/api/v1/native-notes/versions/version-1",
                         "/api/v1/native-notes/note-1/versions",
                     })
                Assert.False(McpReadEndpointPolicy.IsReadAllowed(tool, "GET", path),
                    $"{tool} must not read {path}");
        }
    }

    [Fact]
    public void NativeNoteTools_AreLimitedToTheirExactRoute()
    {
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_native_notes", "GET",
            "/api/v1/native-notes/mcp/notes"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_native_notes", "GET",
            "/api/v1/native-notes/mcp/notes/note-1"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_native_note", "GET",
            "/api/v1/native-notes/mcp/notes/note-1"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_native_note", "GET",
            "/api/v1/native-notes/mcp/notes/note-1/versions"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_native_note", "GET",
            "/api/v1/native-notes/mcp/notes/note-1/processing"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_native_note", "GET",
            "/api/v1/native-notes/mcp/notes/note-1/package"));
        Assert.True(McpReadEndpointPolicy.IsReadAllowed("get_native_note_package", "GET",
            "/api/v1/native-notes/mcp/notes/note-1/package"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_native_note_package", "GET",
            "/api/v1/native-notes/mcp/notes/note-1/versions"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("get_native_note", "GET",
            "/api/v1/native-notes/mcp/notes/note-1/unmapped"));
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
