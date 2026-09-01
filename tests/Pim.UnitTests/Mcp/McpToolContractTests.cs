using System.Text.Json;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Equivalence contract tests: the embedded 151-tool wire contract (dumped from the Python
/// reference) must match the .NET catalog and tool table exactly — names, counts, schemas.
/// </summary>
public sealed class McpToolContractTests
{
    [Fact]
    public void Contract_ContainsExactly151Tools()
    {
        Assert.Equal(151, McpToolExecutor.ToolContract.Count);
    }

    [Fact]
    public void Contract_ReadWriteSplit_MatchesCatalog()
    {
        var contractNames = McpToolExecutor.ToolContract.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var readNames = McpToolCatalog.ReadTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var writeNames = McpToolCatalog.WriteTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(101, readNames.Count);
        Assert.Equal(50, writeNames.Count);
        Assert.Equal(contractNames, readNames.Union(writeNames).ToHashSet(StringComparer.Ordinal));
        Assert.Empty(contractNames.Intersect(writeNames).Intersect(readNames));
    }

    [Fact]
    public void Contract_NamesAreUnique()
    {
        var names = McpToolExecutor.ToolContract.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Contract_EveryToolHasSchemaAndDescription()
    {
        foreach (var tool in McpToolExecutor.ToolContract)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} description missing");
            Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
            Assert.True(tool.InputSchema.TryGetProperty("type", out var schemaType)
                && schemaType.GetString() == "object", $"{tool.Name} inputSchema must be an object schema");
        }
    }

    [Fact]
    public void Contract_MatchesToolTable()
    {
        var contractNames = McpToolExecutor.ToolContract.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var tableNames = McpToolTable.All.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(contractNames, tableNames);
    }

    [Fact]
    public void Contract_WriteToolsHaveWriteCatalogEntries()
    {
        var writeNames = McpToolCatalog.WriteTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in McpToolExecutor.ToolContract)
        {
            var isWrite = writeNames.Contains(tool.Name);
            var spec = McpToolTable.TryGet(tool.Name)!;
            // Write tools map to POST/PUT/DELETE routes; read tools to GET or read-semantic POST.
            if (isWrite)
                Assert.True(spec.Method is "POST" or "PUT" or "DELETE", $"{tool.Name} must be a write route");
            else
                Assert.True(spec.Method is "GET" or "POST", $"{tool.Name} must be a read route");
        }
    }

    [Fact]
    public void Contract_SchemaSpotChecks()
    {
        var byName = McpToolExecutor.ToolContract.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // get_events: start/end/calendarId optional, redactUrls default true.
        var events = byName["get_events"];
        var eventsProperties = events.InputSchema.GetProperty("properties");
        Assert.True(eventsProperties.GetProperty("redactUrls").GetProperty("default").GetBoolean());
        Assert.False(events.InputSchema.TryGetProperty("required", out _)); // all params optional

        // create_event: calendarId/title/dtStart/dtEnd required.
        var createEvent = byName["create_event"];
        var createEventRequired = createEvent.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("calendarId", createEventRequired);
        Assert.Contains("title", createEventRequired);
        Assert.Contains("dtStart", createEventRequired);
        Assert.Contains("dtEnd", createEventRequired);

        // create_mobile_goal: limitSeconds required.
        var createGoal = byName["create_mobile_goal"];
        var goalRequired = createGoal.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("limitSeconds", goalRequired);

        // upload_file: base64 content param present.
        var uploadFile = byName["upload_file"];
        Assert.True(uploadFile.InputSchema.GetProperty("properties").TryGetProperty("fileContentBase64", out _));
    }
}
